namespace DocExtract.Services;

using System.Globalization;
using System.Text;
using System.Text.Json;
using DocExtract.Models;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Scores extraction artifacts against SROIE ground truth (company/date/address/total).
/// Appends one line per run to data/eval_runs.jsonl — the report table is regenerated from
/// that file, never hand-edited — and writes the failed-doc list that drives escalation.
/// </summary>
public sealed class EvalService(IConfiguration config, string dataDir)
{
    private static readonly string[] Fields = ["company", "date", "address", "total"];
    // SROIE keys keep whatever format the receipt printed, day-first (Malaysia): two-digit
    // years and single-digit day/month appear alongside the long forms.
    private static readonly string[] GtDateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy",
        "dd/MM/yy", "d/M/yy", "dd-MM-yy", "d-M-yy", "dd.MM.yy",
        "yyyy-MM-dd", "dd MMM yyyy", "d MMM yyyy", "dd MMM yy",
    ];

    public int Run(string label, CancellationToken ct)
    {
        var keysDir = config["Sroie:KeysDir"] ?? "./data/datasets/sroie/data/key";
        var imagesDir = config["Sroie:ImagesDir"] ?? "./data/datasets/sroie/data/img";
        var artifacts = ExtractionService.LoadArtifacts(dataDir)
            .Where(a => File.Exists(Path.Combine(keysDir, Path.GetFileNameWithoutExtension(a.Source) + ".json")))
            .OrderBy(a => a.Source).ToList();
        if (artifacts.Count == 0) { Console.Error.WriteLine("eval: no artifacts with matching ground truth"); return 1; }

        var correct = Fields.ToDictionary(f => f, _ => 0);
        var graded = Fields.ToDictionary(f => f, _ => 0);
        var mismatchExamples = Fields.ToDictionary(f => f, _ => new List<string>());
        var failedDocs = new List<string>();
        var (exact, totalCost, totalMs) = (0, 0m, 0L);
        var models = new SortedSet<string>();

        foreach (var artifact in artifacts)
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(artifact.Source);
            using var gt = JsonDocument.Parse(File.ReadAllText(Path.Combine(keysDir, id + ".json")));
            var docOk = true;
            foreach (var field in Fields)
            {
                var expected = gt.RootElement.TryGetProperty(field, out var p) ? p.GetString() ?? "" : "";
                // No ground truth for this field on this doc — ungradeable, not wrong.
                if (string.IsNullOrWhiteSpace(expected)) continue;
                graded[field]++;
                var got = Extracted(artifact.Extraction, field);
                if (FieldMatches(field, expected, got)) correct[field]++;
                else
                {
                    docOk = false;
                    if (mismatchExamples[field].Count < 8)
                        mismatchExamples[field].Add($"{id}: expected '{expected}' got '{got ?? "(null)"}'");
                }
            }
            if (docOk) exact++;
            else failedDocs.Add(Path.GetFullPath(Path.Combine(imagesDir, id + ".jpg")));
            totalCost += artifact.CostUsd;
            totalMs += artifact.ElapsedMs;
            models.Add(artifact.Model);
        }

        var run = new
        {
            label,
            ts = DateTime.UtcNow.ToString("o"),
            models = string.Join("+", models),
            docs = artifacts.Count,
            accuracy = Fields.ToDictionary(f => f, f => Math.Round((double)correct[f] / Math.Max(1, graded[f]), 4)),
            graded = Fields.ToDictionary(f => f, f => graded[f]),
            exact_match = Math.Round((double)exact / artifacts.Count, 4),
            cost_usd = totalCost,
            avg_ms = totalMs / artifacts.Count,
            mismatch_examples = mismatchExamples,
        };
        File.AppendAllText(Path.Combine(dataDir, "eval_runs.jsonl"),
            JsonSerializer.Serialize(run) + Environment.NewLine);

        Directory.CreateDirectory(Path.Combine(dataDir, "eval"));
        File.WriteAllLines(Path.Combine(dataDir, "eval", $"failed-{label}.txt"), failedDocs);

        var sb = new StringBuilder($"eval [{label}] {artifacts.Count} docs, models {run.models}:\n");
        foreach (var f in Fields)
            sb.Append($"  {f,-8} {(double)correct[f] / Math.Max(1, graded[f]):P1}  ({correct[f]}/{graded[f]})\n");
        sb.Append($"  exact    {(double)exact / artifacts.Count:P1}\n");
        sb.Append($"  cost ${totalCost:0.00}, avg {totalMs / artifacts.Count / 1000.0:0.0}s/doc, {failedDocs.Count} docs → eval/failed-{label}.txt");
        Console.WriteLine(sb.ToString());
        return 0;
    }

    private static string? Extracted(ExtractedDoc? doc, string field) => field switch
    {
        "company" => doc?.Vendor?.Value,
        "date" => doc?.Date?.Value,
        "address" => doc?.Address?.Value,
        "total" => doc?.Total?.Value?.ToString("0.00", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static bool FieldMatches(string field, string expected, string? got)
    {
        if (string.IsNullOrWhiteSpace(got)) return string.IsNullOrWhiteSpace(expected);
        return field switch
        {
            "date" => ParseDate(expected) is { } e && ParseDate(got) is { } g && e == g,
            "total" => ParseAmount(expected) is { } et && ParseAmount(got) is { } gt2 && et == gt2,
            _ => Normalize(expected) == Normalize(got),
        };
    }

    /// <summary>GT amounts carry currency marks the invariant parser rejects ("$8.20",
    /// "RM 8.20") — strip to the numeric core before comparing.</summary>
    private static decimal? ParseAmount(string s)
    {
        var numeric = new string(s.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        return decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s.Trim(), GtDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var d2) ? d2 : null;

    /// <summary>Case/whitespace/punctuation-insensitive: OCR-adjacent strings should tie.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastSpace = false;
        foreach (var ch in s.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastSpace = false; }
            else if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().TrimEnd();
    }
}
