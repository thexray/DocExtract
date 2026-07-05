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
    private static readonly string[] GtDateFormats = ["dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "dd MMM yyyy", "d MMM yyyy", "dd MMM yy"];

    public int Run(string label, CancellationToken ct)
    {
        var keysDir = config["Sroie:KeysDir"] ?? "./data/datasets/sroie/data/key";
        var imagesDir = config["Sroie:ImagesDir"] ?? "./data/datasets/sroie/data/img";
        var artifacts = ExtractionService.LoadArtifacts(dataDir)
            .Where(a => File.Exists(Path.Combine(keysDir, Path.GetFileNameWithoutExtension(a.Source) + ".json")))
            .OrderBy(a => a.Source).ToList();
        if (artifacts.Count == 0) { Console.Error.WriteLine("eval: no artifacts with matching ground truth"); return 1; }

        var correct = Fields.ToDictionary(f => f, _ => 0);
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
            accuracy = Fields.ToDictionary(f => f, f => Math.Round((double)correct[f] / artifacts.Count, 4)),
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
        foreach (var f in Fields) sb.Append($"  {f,-8} {(double)correct[f] / artifacts.Count:P1}\n");
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
            "total" => decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var et)
                       && decimal.TryParse(got, NumberStyles.Any, CultureInfo.InvariantCulture, out var gt2)
                       && et == gt2,
            _ => Normalize(expected) == Normalize(got),
        };
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
