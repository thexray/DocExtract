using System.Text;
using System.Text.Json;
using DocExtract.Services;
using Microsoft.Extensions.Configuration;

// Config layering: committed defaults → gitignored dev secrets → env vars.
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var dataDir = Path.GetFullPath(config["DataDirectory"] ?? "./data");
Directory.CreateDirectory(dataDir);

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var exit = 0;
try
{
    var ledger = new CostLedger(dataDir);
    var claude = new ClaudeCliService(config, ledger);
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var budget = decimal.TryParse(config["EvalBudgetUsd"], out var b) ? b : 10m;

    switch (verb)
    {
        case "extract" when args.Length >= 2:
        {
            var target = args[1];
            var parallel = Math.Max(1, OptInt("--parallel", 1));
            var tier = Opt("--tier", "extraction");
            var model = tier == "escalation" ? claude.EscalationModel : claude.ExtractionModel;

            string[] files =
                Directory.Exists(target)
                    ? Directory.EnumerateFiles(target)
                        .Where(f => ExtractionService.SupportedExtensions.Contains(
                            Path.GetExtension(f).ToLowerInvariant()))
                        .Order().ToArray()
                : Path.GetExtension(target).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    ? File.ReadAllLines(target).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
                    : [target];
            var limit = OptInt("--limit", files.Length);
            files = files.Take(limit).ToArray();
            if (args.Contains("--skip-existing"))
            {
                var done = ExtractionService.LoadArtifacts(dataDir)
                    .Select(a => Path.GetFileNameWithoutExtension(a.Source))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var before = files.Length;
                files = files.Where(f => !done.Contains(Path.GetFileNameWithoutExtension(f))).ToArray();
                Console.WriteLine($"extract: --skip-existing removed {before - files.Length} already-done docs");
            }
            if (files.Length == 0) { Console.Error.WriteLine($"extract: no supported files in {target}"); exit = 1; break; }

            var svc = new ExtractionService(claude, config, dataDir);
            var gate = new SemaphoreSlim(parallel);
            var console = new object();
            var (ok, review, skipped, retried) = (0, 0, 0, 0);
            var totalCost = 0m;

            var tasks = files.Select(async file =>
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    // Budget guard: the cap is structural, not advisory. Month-to-date spend
                    // at or over the budget stops new calls; in-flight ones finish.
                    if (ledger.MonthToDate() >= budget)
                    {
                        lock (console) { skipped++; }
                        return;
                    }
                    var (accepted, cost, attempts) = await svc.ProcessAsync(file, model, cts.Token);
                    lock (console)
                    {
                        totalCost += cost;
                        if (accepted) ok++; else review++;
                        if (attempts > 1) retried++;
                        Console.WriteLine($"  {Path.GetFileName(file),-20} {(accepted ? "accepted" : "needs-review"),-12} ${cost:0.0000}" +
                            (attempts > 1 ? $"  (retried ×{attempts - 1})" : ""));
                    }
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);

            Console.WriteLine($"extract [{model}]: {ok} accepted, {review} needs-review" +
                // "retried" counts documents that needed a second attempt, not documents the
                // retry saved — a doc that failed both attempts is still needs-review above.
                (retried > 0 ? $", {retried} retried" : "") +
                (skipped > 0 ? $", {skipped} SKIPPED (budget ${budget:0.00} reached)" : "") +
                $", ${totalCost:0.00} this run → {Path.Combine(dataDir, "extractions")}");
            if (skipped > 0) exit = 2;
            break;
        }

        case "extract":
            Console.Error.WriteLine("extract: usage: docextract extract <file|dir|list.txt> [--parallel N] [--tier extraction|escalation] [--limit N] [--skip-existing]");
            exit = 1;
            break;

        case "eval" when args.Contains("--retrieval"):
        {
            // Retrieval scores the artifacts that already exist — it never re-extracts, because
            // every re-extraction is paid twice and the corpus is already on disk.
            var opt = new RetrievalOptions(
                Label: Opt("--label", $"retrieval-{DateTime.UtcNow:yyyyMMdd-HHmmss}"),
                Questions: OptInt("--questions", 30),
                AnswerK: OptInt("--k", 5),
                Rewrite: args.Contains("--rewrite"),
                Rerank: args.Contains("--rerank"),
                Answers: !args.Contains("--no-answers"));
            exit = await new RetrievalEvalService(claude, ledger, config, dataDir).RunAsync(opt, cts.Token);
            break;
        }

        case "eval":
            exit = new EvalService(config, dataDir).Run(Opt("--label", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}"), cts.Token);
            break;

        case "report":
        {
            var spent = ledger.MonthToDate();
            Console.WriteLine($"LLM cost month-to-date: ${spent:0.00} of ${budget:0.00} budget");
            var (calls, retries, retryCost) = ledger.MonthToDateRetries();
            Console.WriteLine(calls == 0
                ? "  calls: none this month"
                : $"  calls: {calls}, of which {retries} retries ({(double)retries / calls:P1}) costing ${retryCost:0.00##}");
            var retrievalTable = RetrievalEvalService.BuildTable(dataDir);
            if (retrievalTable.Length > 0) Console.WriteLine("\nretrieval runs:\n" + retrievalTable);

            var runsPath = Path.Combine(dataDir, "eval_runs.jsonl");
            if (!File.Exists(runsPath))
            {
                Console.WriteLine("no extraction eval runs recorded yet");
                if (retrievalTable.Length > 0) WriteReadmeBlocks(null, retrievalTable);
                break;
            }

            var table = new StringBuilder();
            var missTable = new StringBuilder();
            // Rows are joined with an explicit \n, never Environment.NewLine: this table is
            // written into the README by whichever machine runs `report`, and line endings
            // that follow the OS would produce a diff that changes no number.
            void Row(string s) => table.Append(s).Append('\n');
            void MissRow(string s) => missTable.Append(s).Append('\n');

            Row("| Run | Models | Docs | Company | Date | Address | Total | Exact match | Cost | $/doc | Avg s/doc |");
            Row("|---|---|---|---|---|---|---|---|---|---|---|");
            // The accuracy table answers "how often"; this one answers "how many", because a
            // failure taxonomy needs counts and a reader should not have to do the arithmetic.
            MissRow("| Run | Company | Date | Address | Total |");
            MissRow("|---|---|---|---|---|");
            foreach (var line in File.ReadLines(runsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var run = JsonDocument.Parse(line);
                var r = run.RootElement;
                var docs = r.GetProperty("docs").GetInt32();
                var cost = r.GetProperty("cost_usd").GetDecimal();
                var acc = r.GetProperty("accuracy");
                Row(
                    $"| {r.GetProperty("label").GetString()} | {r.GetProperty("models").GetString()} | {docs} " +
                    $"| {acc.GetProperty("company").GetDouble():P1} | {acc.GetProperty("date").GetDouble():P1} " +
                    $"| {acc.GetProperty("address").GetDouble():P1} | {acc.GetProperty("total").GetDouble():P1} " +
                    $"| {r.GetProperty("exact_match").GetDouble():P1} | ${cost:0.00} | ${cost / docs:0.000} " +
                    $"| {r.GetProperty("avg_ms").GetInt64() / 1000.0:0.0} |");

                var graded = r.GetProperty("graded");
                string Missed(string field)
                {
                    var g = graded.GetProperty(field).GetInt32();
                    return $"{(int)Math.Round(g * (1 - acc.GetProperty(field).GetDouble()))} of {g}";
                }
                MissRow(
                    $"| {r.GetProperty("label").GetString()} | {Missed("company")} | {Missed("date")} " +
                    $"| {Missed("address")} | {Missed("total")} |");
            }
            Console.WriteLine(table.ToString());
            Console.WriteLine("misses:\n" + missTable.ToString());
            WriteReadmeBlocks(table.ToString(), retrievalTable, missTable.ToString());
            break;
        }

        case "check":
            Console.WriteLine("DocExtract config check");
            Console.WriteLine($"  DataDirectory     {dataDir}");
            Console.WriteLine($"  ExtractionModel   {claude.ExtractionModel}");
            Console.WriteLine($"  EscalationModel   {claude.EscalationModel}");
            Console.WriteLine($"  MaxAttempts       {config["ClaudeCli:MaxAttempts"] ?? "(default: 2)"}");
            Console.WriteLine($"  EvalBudgetUsd     {config["EvalBudgetUsd"]}");
            Console.WriteLine($"  Retrieval tripwire {config["Retrieval:QuestionCostTripwireUsd"] ?? "(default: 0.15)"} per question");
            Console.WriteLine($"  Sroie:KeysDir     {(Directory.Exists(config["Sroie:KeysDir"] ?? "") ? "ok" : "MISSING")}");
            Console.WriteLine($"  ClaudeCli:Path    {(string.IsNullOrWhiteSpace(config["ClaudeCli:Path"]) ? "(PATH default: claude)" : "set")}");
            break;

        default:
            Console.WriteLine("""
                docextract — LLM document-extraction pipeline with an eval harness

                usage:
                  docextract extract <file|dir|list.txt> [--parallel N] [--tier extraction|escalation] [--limit N]
                  docextract eval [--label NAME]      score artifacts against ground truth
                  docextract eval --retrieval [--label NAME] [--questions N] [--k N]
                                  [--rewrite] [--rerank] [--no-answers]
                                                      recall@k / MRR / groundedness over the
                                                      artifacts already extracted (never re-extracts;
                                                      --no-answers is the $0 retrieval-only pass)
                  docextract report                   eval results tables + month-to-date LLM cost
                  docextract check                    config smoke check (key presence only)
                """);
            exit = verb == "help" ? 0 : 1;
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"docextract {verb}: {ex.Message}");
    exit = 1;
}
return exit;

int OptInt(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

string Opt(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

// Both README tables are generated, never hand-edited — that is the claim the README makes
// about its own numbers, so the only way a figure gets in there is through this function.
void WriteReadmeBlocks(string? evalTable, string? retrievalTable, string? missTable = null)
{
    var readme = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "README.md"));
    if (!File.Exists(readme)) return;

    var text = File.ReadAllText(readme);
    var updated = Replace(
        Replace(Replace(text, "eval-results", evalTable), "retrieval-results", retrievalTable),
        "miss-counts", missTable);
    if (updated == text) return;
    File.WriteAllText(readme, updated);
    Console.WriteLine($"README tables regenerated: {readme}");

    static string Replace(string text, string marker, string? table)
    {
        if (string.IsNullOrEmpty(table)) return text;
        string begin = $"<!-- {marker}:begin -->", end = $"<!-- {marker}:end -->";
        var (i, j) = (text.IndexOf(begin, StringComparison.Ordinal), text.IndexOf(end, StringComparison.Ordinal));
        return i >= 0 && j > i ? text[..(i + begin.Length)] + "\n" + table + text[j..] : text;
    }
}
