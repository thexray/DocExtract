namespace DocExtract.Services;

using System.Globalization;
using System.Text.Json;
using DocExtract.Models;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Derives the retrieval question set mechanically from SROIE ground truth.
///
/// Two rules make the resulting numbers mean something. First, questions are generated from
/// the keys, never from the extraction artifacts — if the corpus were also the answer key,
/// a retriever that found the wrong document would still look right whenever the extractor
/// misread it. Second, a question is only emitted when its relevant-document set is provably
/// complete: lookups require the (company, date) pair to identify exactly one receipt, and
/// aggregates take every receipt the ground truth attributes to that vendor. A question whose
/// relevant set is merely probable would silently cap recall.
///
/// Derivation is deterministic — same keys in, same questions out — so a rerun compares
/// against the same yardstick rather than a freshly-sampled one.
/// </summary>
public sealed class QuestionSetService(IConfiguration config, string dataDir)
{
    private readonly string _keysDir = config["Sroie:KeysDir"] ?? "./data/datasets/sroie/data/key";

    /// <summary>Vendors with more receipts than this are skipped for aggregates: a question
    /// with 12 relevant documents measures corpus skew, not retrieval.</summary>
    private const int MaxAggregateDocs = 5;

    public string QuestionsPath => Path.Combine(dataDir, "retrieval", "questions.jsonl");

    /// <summary>
    /// Builds the question set from ground truth for the documents that have artifacts, and
    /// writes it to data/retrieval/questions.jsonl. Regenerating is free — no model is called.
    /// </summary>
    public List<RetrievalQuestion> Build(IEnumerable<string> corpusDocIds, int wanted)
    {
        var gt = LoadGroundTruth(corpusDocIds);
        if (gt.Count == 0)
            throw new InvalidOperationException(
                $"no SROIE ground-truth keys found in {Path.GetFullPath(_keysDir)} for the indexed " +
                "artifacts — run scripts/download-datasets.ps1 first (questions are derived from " +
                "the keys, never from the artifacts)");

        var byCompany = gt.Values
            .GroupBy(r => r.CompanyKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.DocId, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        // Aggregates first: they are the scarcer kind, so letting lookups fill the remainder
        // keeps the set at the requested size instead of short by however few vendors repeat.
        var aggregates = byCompany.Values
            .Where(rs => rs.Count is >= 2 and <= MaxAggregateDocs)
            .OrderBy(rs => rs[0].DocId, StringComparer.Ordinal)
            .Select(rs => new RetrievalQuestion(
                Id: $"agg-{rs[0].DocId}",
                Kind: "aggregate",
                Text: $"What is the combined total of all receipts from {rs[0].Company}?",
                RelevantDocIds: rs.Select(r => r.DocId).ToList(),
                ExpectedValue: (double)rs.Sum(r => r.Total),
                ExpectedUnit: "sum of totals"))
            .ToList();

        var lookups = gt.Values
            // Uniqueness is on (company, date): the pair has to name one receipt, or the
            // question has an ambiguous answer and no honest relevant set.
            .Where(r => byCompany[r.CompanyKey].Count(o => o.Date == r.Date) == 1)
            .OrderBy(r => r.DocId, StringComparer.Ordinal)
            .Select(r => new RetrievalQuestion(
                Id: $"look-{r.DocId}",
                Kind: "lookup",
                Text: $"How much was spent at {r.Company} on {r.Date:yyyy-MM-dd}?",
                RelevantDocIds: [r.DocId],
                ExpectedValue: (double)r.Total,
                ExpectedUnit: "receipt total"))
            .ToList();

        // Two thirds lookups by default — single-doc retrieval is the honest recall@1 signal,
        // aggregates exist to show multi-document questions are not being quietly avoided.
        var wantAggregate = Math.Min(aggregates.Count, Math.Max(1, wanted / 3));
        var questions = lookups.Take(wanted - wantAggregate)
            .Concat(aggregates.Take(wantAggregate))
            .OrderBy(q => q.Id, StringComparer.Ordinal)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(QuestionsPath)!);
        File.WriteAllLines(QuestionsPath, questions.Select(q => JsonSerializer.Serialize(q)));
        return questions;
    }

    private sealed record GtRow(string DocId, string Company, string CompanyKey, DateTime Date, decimal Total);

    /// <summary>
    /// Reads the keys for documents that are actually in the corpus. Rows missing any of
    /// company/date/total, or carrying an unparseable one, are dropped rather than guessed —
    /// they can still be retrieved, they just cannot anchor a question.
    /// </summary>
    private Dictionary<string, GtRow> LoadGroundTruth(IEnumerable<string> corpusDocIds)
    {
        var rows = new Dictionary<string, GtRow>(StringComparer.Ordinal);
        foreach (var docId in corpusDocIds.OrderBy(d => d, StringComparer.Ordinal))
        {
            var path = Path.Combine(_keysDir, docId + ".json");
            if (!File.Exists(path)) continue;

            JsonDocument key;
            try { key = JsonDocument.Parse(File.ReadAllText(path)); }
            catch (JsonException) { continue; }
            using (key)
            {
                var company = Str(key.RootElement, "company");
                var date = Str(key.RootElement, "date");
                var total = Str(key.RootElement, "total");
                if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(date) ||
                    string.IsNullOrWhiteSpace(total)) continue;
                if (GroundTruth.ParseDate(date) is not { } d) continue;
                if (GroundTruth.ParseAmount(total) is not { } t || t <= 0) continue;
                rows[docId] = new GtRow(docId, Tidy(company), GroundTruth.Normalize(company), d, t);
            }
        }
        return rows;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    /// <summary>
    /// Collapses the whitespace SROIE keys inherit from OCR line breaks, so the question text
    /// reads like a question. Unlike <see cref="GroundTruth.Normalize"/> this keeps the
    /// vendor's real punctuation and case — it is what a person would have typed.
    /// </summary>
    private static string Tidy(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim().Trim(',', '.', ';')
            .ToString(CultureInfo.InvariantCulture);
}
