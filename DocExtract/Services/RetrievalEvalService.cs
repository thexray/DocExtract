namespace DocExtract.Services;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DocExtract.Models;
using Microsoft.Extensions.Configuration;

/// <summary>Knobs for one retrieval run; the defaults are the free lexical baseline.</summary>
public sealed record RetrievalOptions(
    string Label,
    int Questions = 30,
    int AnswerK = 5,
    bool Rewrite = false,
    bool Rerank = false,
    bool Answers = true);

/// <summary>
/// Scores retrieval over the existing extraction artifacts: recall@k and MRR against a
/// mechanically-derived question set, plus groundedness as a deterministic check rather than
/// a judgement. Appends one line per run to data/retrieval_runs.jsonl, which is what the
/// report table is regenerated from.
///
/// The LLM does not grade anything here. It answers questions; three predicates then decide
/// whether the answer was grounded — did it cite at all, were the citations documents it was
/// actually shown, and does the figure equal the ground-truth figure. An LLM judge would have
/// been fewer lines and worth nothing, because it would share the failure modes of the thing
/// under test.
/// </summary>
public sealed class RetrievalEvalService(
    ClaudeCliService claude, CostLedger ledger, IConfiguration config, string dataDir)
{
    private static readonly int[] KValues = [1, 5, 10];
    private const int MaxK = 10;

    public async Task<int> RunAsync(RetrievalOptions opt, CancellationToken ct)
    {
        var artifacts = ExtractionService.LoadArtifacts(dataDir).ToList();
        if (artifacts.Count == 0)
        {
            Console.Error.WriteLine("eval --retrieval: no extraction artifacts to index — run extract first");
            return 1;
        }

        var index = RetrievalIndex.Build(artifacts);
        var questions = new QuestionSetService(config, dataDir).Build(index.DocIds, opt.Questions);
        if (questions.Count == 0)
        {
            Console.Error.WriteLine("eval --retrieval: ground truth produced no answerable questions");
            return 1;
        }

        var budget = decimal.TryParse(config["EvalBudgetUsd"], out var b) ? b : 10m;
        // Stuffing tripwire, measured per question rather than per run. A per-run ceiling was
        // the first instinct and it was wrong twice over: it conflates prompt size with
        // --questions, which is just a flag, and it fires on a healthy long run while missing
        // a stuffed short one. What actually indicates stuffing is one question suddenly
        // costing multiples of its peers. Calibrated against measurement, not estimate: the
        // headless CLI charges ~$0.018 of fixed overhead before a single prompt token, so a
        // lean answer call is ~$0.025 and the full rewrite+rerank+answer path ~$0.075.
        // Total spend stays bounded by the monthly cap above, which is the real ceiling.
        var tripwire = config.GetValue("Retrieval:QuestionCostTripwireUsd", 0.15m);
        var tolerance = config.GetValue("Retrieval:AmountToleranceAbs", 0.01);

        var pipeline = "bm25" + (opt.Rewrite ? "+rewrite" : "") + (opt.Rerank ? "+rerank" : "");
        Console.WriteLine($"eval --retrieval [{opt.Label}] {pipeline}: {index.Count} docs, {questions.Count} questions" +
            (opt.Answers ? "" : ", retrieval only (no model calls)"));

        var outcomes = new List<QuestionOutcome>();
        var examples = new List<string>();
        var (runCost, totalMs) = (0m, 0L);
        var costliestQuestion = 0m;
        var stoppedEarly = (string?)null;

        foreach (var q in questions)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var costBefore = runCost;

            // Paid steps are gated twice: once by the month's committed cap, once by this
            // run's tripwire. Either trip stops scheduling new calls; nothing is retried
            // behind the guard's back.
            var paidAllowed = ledger.MonthToDate() < budget && costliestQuestion <= tripwire;
            if (!paidAllowed && (opt.Rewrite || opt.Rerank || opt.Answers) && stoppedEarly is null)
            {
                stoppedEarly = costliestQuestion > tripwire
                    ? $"one question cost ${costliestQuestion:0.0000}, over the ${tripwire:0.00##} per-question " +
                      "tripwire — check that the answer prompt is carrying top-k summaries and not whole documents"
                    : $"month-to-date spend reached the ${budget:0.00} budget";
                // Retrieval itself is free, so the remaining questions are still scored; only
                // the model-backed columns go unmeasured, and the run record says so.
                Console.Error.WriteLine($"eval --retrieval: STOPPED paid steps — {stoppedEarly}");
            }

            var query = q.Text;
            if (opt.Rewrite && paidAllowed)
            {
                var (rewritten, cost) = await RewriteAsync(q.Text, ct);
                runCost += cost;
                if (rewritten is not null) query = rewritten;
            }

            var hits = index.Search(query, MaxK);
            if (opt.Rerank && paidAllowed && hits.Count > 1)
            {
                var (reordered, cost) = await RerankAsync(q.Text, hits, index, ct);
                runCost += cost;
                if (reordered is not null) hits = reordered;
            }

            var ranked = hits.Select(h => h.DocId).ToList();
            var relevant = q.RelevantDocIds.ToHashSet(StringComparer.Ordinal);
            var firstRelevantRank = ranked.FindIndex(relevant.Contains) + 1; // 0 = not retrieved
            var recall = KValues.ToDictionary(
                k => k,
                k => Math.Round((double)ranked.Take(k).Count(relevant.Contains) / relevant.Count, 4));

            var (answered, cited, inContext, figureOk, note) = (false, false, false, false, (string?)null);
            var answerAttempted = opt.Answers && paidAllowed;
            if (answerAttempted)
            {
                var context = ranked.Take(opt.AnswerK).ToList();
                var (payload, cost, error) = await AnswerAsync(q, context, index, ct);
                runCost += cost;
                if (payload is null) note = error;
                else
                {
                    var citations = payload.CitedDocIds ?? [];
                    cited = citations.Count > 0;
                    // Subset of what was actually shown — a citation the model was not given
                    // is fabricated provenance, which is worse than no citation at all.
                    inContext = cited && citations.All(c => context.Contains(c, StringComparer.Ordinal));
                    if (payload.Answer is { } figure)
                    {
                        answered = true;
                        figureOk = Math.Abs(figure - q.ExpectedValue) <= tolerance;
                        if (!figureOk)
                            note = $"answered {figure:0.00}, ground truth {q.ExpectedValue:0.00}";
                    }
                    else note = "no figure in the answer";
                    if (note is null && !inContext && cited)
                        note = "cited a document that was not in its context";
                }
            }

            totalMs += sw.ElapsedMilliseconds;
            costliestQuestion = Math.Max(costliestQuestion, runCost - costBefore);
            outcomes.Add(new QuestionOutcome(q.Id, q.Kind, firstRelevantRank, recall,
                answerAttempted, answered, cited, inContext, figureOk, note));

            if (examples.Count < 10 && (firstRelevantRank is 0 or > 5 || (answerAttempted && !figureOk)))
                examples.Add($"{q.Id} ({q.Kind}): first relevant at rank " +
                    $"{(firstRelevantRank == 0 ? "none in top " + MaxK : firstRelevantRank.ToString())}" +
                    (note is null ? "" : $"; {note}"));

            Console.WriteLine($"  {q.Id,-22} {q.Kind,-9} rank {(firstRelevantRank == 0 ? "-" : firstRelevantRank.ToString()),-3}" +
                $" r@5 {recall[5]:0.00}" + (answerAttempted ? $"  {(figureOk && inContext ? "grounded" : "ungrounded")}" : ""));
        }

        return Record(opt, pipeline, index.Count, questions, outcomes, examples, runCost, totalMs, stoppedEarly);
    }

    private int Record(RetrievalOptions opt, string pipeline, int docs, List<RetrievalQuestion> questions,
        List<QuestionOutcome> outcomes, List<string> examples, decimal runCost, long totalMs, string? stoppedEarly)
    {
        var graded = outcomes.Count(o => o.AnswerAttempted);
        var run = new
        {
            label = opt.Label,
            ts = DateTime.UtcNow.ToString("o"),
            pipeline,
            docs,
            questions = questions.Count,
            recall = KValues.ToDictionary(k => k.ToString(), k => Round(outcomes.Select(o => o.RecallAtK[k]))),
            recall_by_kind = outcomes.GroupBy(o => o.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => KValues.ToDictionary(k => k.ToString(), k => Round(g.Select(o => o.RecallAtK[k])))),
            // MRR over the first relevant document; a question with nothing relevant in the
            // top-10 contributes 0 rather than being dropped.
            mrr = Round(outcomes.Select(o => o.FirstRelevantRank == 0 ? 0.0 : 1.0 / o.FirstRelevantRank)),
            answers = new
            {
                attempted = graded,
                answered = outcomes.Count(o => o.Answered),
                cited = outcomes.Count(o => o.Cited),
                citations_in_context = outcomes.Count(o => o.CitationsInContext),
                figure_matches_gt = outcomes.Count(o => o.FigureMatchesGt),
                grounded = outcomes.Count(o => o.CitationsInContext && o.FigureMatchesGt),
            },
            cost_usd = runCost,
            avg_ms = outcomes.Count == 0 ? 0 : totalMs / outcomes.Count,
            stopped_early = stoppedEarly,
            examples,
        };
        File.AppendAllText(Path.Combine(dataDir, "retrieval_runs.jsonl"),
            JsonSerializer.Serialize(run) + Environment.NewLine);

        var sb = new StringBuilder($"\nretrieval [{opt.Label}] {pipeline}, {docs} docs, {questions.Count} questions:\n");
        foreach (var k in KValues)
            sb.Append($"  recall@{k,-3} {Round(outcomes.Select(o => o.RecallAtK[k])):P1}\n");
        sb.Append($"  MRR       {run.mrr:0.000}\n");
        foreach (var (kind, _) in run.recall_by_kind)
            sb.Append($"    {kind,-10} r@1 {run.recall_by_kind[kind]["1"]:P1}  r@5 {run.recall_by_kind[kind]["5"]:P1}  r@10 {run.recall_by_kind[kind]["10"]:P1}\n");
        if (graded > 0)
            sb.Append($"  grounded  {run.answers.grounded}/{graded} " +
                $"(cited {run.answers.cited}, citations in context {run.answers.citations_in_context}, " +
                $"figure matches GT {run.answers.figure_matches_gt})\n");
        sb.Append($"  cost ${runCost:0.0000}, avg {totalMs / Math.Max(1, outcomes.Count) / 1000.0:0.0}s/question");
        if (stoppedEarly is not null) sb.Append($"\n  INCOMPLETE: {stoppedEarly}");
        Console.WriteLine(sb.ToString());

        return stoppedEarly is null ? 0 : 2;
    }

    private static double Round(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : Math.Round(list.Average(), 4);
    }

    // ---- model-backed steps -------------------------------------------------------------

    private async Task<(AnswerPayload? Payload, decimal Cost, string? Error)> AnswerAsync(
        RetrievalQuestion q, List<string> context, RetrievalIndex index, CancellationToken ct)
    {
        var docs = string.Join("\n", context.Select(id => index.Get(id)?.Summary).Where(s => s is not null));
        var prompt = $$"""
            Answer the question using ONLY the receipts listed below. Each line starts with its
            document ID in square brackets.

            {{docs}}

            Question: {{q.Text}}

            Output ONLY one JSON object — no prose, no code fences:
            {"answer": 0.00, "cited_doc_ids": ["ID", "..."]}

            Rules:
            - answer: a plain number ({{q.ExpectedUnit}}), no currency symbol
            - cited_doc_ids: every document above that your answer depends on, exactly as printed
            - if the receipts above do not contain the answer, use null for answer and [] for cited_doc_ids
            """;

        var res = await claude.ExecAsync(prompt, claude.ExtractionModel, "retrieval:answer", ct,
            payloadCheck: t => ParseAnswer(t) is null ? "unparseable answer payload" : null,
            retryNudge: "\n\nYour previous response was not a single JSON object of that exact shape. " +
                        "Return it again as ONE raw JSON object, no prose and no code fences.");
        return res.Ok ? (ParseAnswer(res.Text), res.CostUsd, null) : (null, res.CostUsd, res.Error);
    }

    private static AnswerPayload? ParseAnswer(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(ClaudeCliService.ExtractJson(text, '{', '}'));
            var root = doc.RootElement;
            // Well-formed JSON of the wrong shape is still an unusable payload: TryGetProperty
            // throws rather than returning false when the root is not an object, and that
            // exception is not a JsonException.
            if (root.ValueKind != JsonValueKind.Object) return null;
            double? answer = root.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.Number
                ? a.GetDouble() : null;
            var cited = root.TryGetProperty("cited_doc_ids", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).ToList()
                : [];
            return new AnswerPayload(answer, cited);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The first half of the improvement pass: restate the question as retrieval keywords.
    /// Whether it earns its call is decided by the recall columns, not by it sounding sensible.
    /// </summary>
    private async Task<(string? Query, decimal Cost)> RewriteAsync(string question, CancellationToken ct)
    {
        var prompt = $$"""
            Rewrite this question as a keyword query for a lexical (BM25) search over receipt
            records holding vendor name, address, date and total.

            Question: {{question}}

            Output ONLY one JSON object — no prose, no code fences:
            {"query": "keywords"}

            Keep every distinctive proper noun and every date component. Drop question words and
            filler. Do not invent terms that are not implied by the question.
            """;
        var res = await claude.ExecAsync(prompt, claude.ExtractionModel, "retrieval:rewrite", ct,
            payloadCheck: t => ParseField(t, "query") is null ? "unparseable rewrite payload" : null);
        // A failed rewrite falls back to the original question rather than failing the
        // question: the baseline is always available, so the improvement pass can never make
        // retrieval worse than free.
        return res.Ok ? (ParseField(res.Text, "query"), res.CostUsd) : (null, res.CostUsd);
    }

    /// <summary>
    /// The second half: reorder the lexical candidates. Note what this structurally cannot do —
    /// it only permutes the top <see cref="MaxK"/>, so recall@10 is fixed by the baseline and
    /// only recall@1/@5 and MRR can move.
    /// </summary>
    private async Task<(List<Hit>? Hits, decimal Cost)> RerankAsync(
        string question, List<Hit> hits, RetrievalIndex index, CancellationToken ct)
    {
        var docs = string.Join("\n", hits.Select(h => index.Get(h.DocId)?.Summary).Where(s => s is not null));
        var prompt = $$"""
            Rank these receipts by how well each answers the question. Each line starts with its
            document ID in square brackets.

            {{docs}}

            Question: {{question}}

            Output ONLY a JSON array of every document ID above, most relevant first — no prose,
            no code fences, no IDs that are not listed above:
            ["ID", "ID", "..."]
            """;
        var res = await claude.ExecAsync(prompt, claude.ExtractionModel, "retrieval:rerank", ct,
            payloadCheck: t => ParseIdList(t) is null ? "unparseable rerank payload" : null);
        if (!res.Ok || ParseIdList(res.Text) is not { } order) return (null, res.CostUsd);

        var byId = hits.ToDictionary(h => h.DocId, StringComparer.Ordinal);
        // Only IDs the model was shown survive, deduplicated; anything it dropped or invented
        // is appended in — or discarded back to — the baseline order. The reranker is allowed
        // to reorder the candidate set, never to change what is in it.
        var reordered = order.Distinct(StringComparer.Ordinal).Where(byId.ContainsKey)
            .Select(id => byId[id]).ToList();
        reordered.AddRange(hits.Where(h => !reordered.Contains(h)));
        return (reordered, res.CostUsd);
    }

    private static string? ParseField(string text, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(ClaudeCliService.ExtractJson(text, '{', '}'));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static List<string>? ParseIdList(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(ClaudeCliService.ExtractJson(text, '[', ']'));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var ids = doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim().Trim('[', ']')).ToList();
            return ids.Count == 0 ? null : ids;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Regenerates the retrieval table for report/README from stored runs.</summary>
    public static string BuildTable(string dataDir)
    {
        var path = Path.Combine(dataDir, "retrieval_runs.jsonl");
        if (!File.Exists(path)) return "";

        var table = new StringBuilder();
        // Explicit \n, not Environment.NewLine — same reason as the extraction table in
        // Program.cs: a README regenerated on another OS must come out byte-identical.
        void Row(string s) => table.Append(s).Append('\n');

        Row("| Run | Pipeline | Docs | Questions | Recall@1 | Recall@5 | Recall@10 | MRR | Grounded | Cost |");
        Row("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var run = JsonDocument.Parse(line);
            var r = run.RootElement;
            var recall = r.GetProperty("recall");
            var answers = r.GetProperty("answers");
            var attempted = answers.GetProperty("attempted").GetInt32();
            var grounded = attempted == 0 ? "n/a"
                : $"{answers.GetProperty("grounded").GetInt32()}/{attempted}";
            Row(
                $"| {r.GetProperty("label").GetString()} | {r.GetProperty("pipeline").GetString()} " +
                $"| {r.GetProperty("docs").GetInt32()} | {r.GetProperty("questions").GetInt32()} " +
                $"| {recall.GetProperty("1").GetDouble():P1} | {recall.GetProperty("5").GetDouble():P1} " +
                $"| {recall.GetProperty("10").GetDouble():P1} | {r.GetProperty("mrr").GetDouble():0.000} " +
                $"| {grounded} | ${r.GetProperty("cost_usd").GetDecimal().ToString("0.0000", CultureInfo.InvariantCulture)} |");
        }
        return table.ToString();
    }
}
