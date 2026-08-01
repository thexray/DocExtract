namespace DocExtract.Services;

using System.Diagnostics;
using System.Text.Json;
using DocExtract.Models;
using Microsoft.Extensions.Configuration;

/// <summary>
/// One document in, one JSON artifact out — either data/extractions/accepted/ or
/// data/extractions/needs-review/, decided solely by the deterministic validation layer.
/// The vision call goes through the headless CLI with only the Read tool allowed.
/// </summary>
public sealed class ExtractionService(ClaudeCliService claude, IConfiguration config, string dataDir)
{
    public static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".pdf", ".txt"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public async Task<(bool Accepted, decimal Cost, int Attempts)> ProcessAsync(string file,
        string model, CancellationToken ct)
    {
        var full = Path.GetFullPath(file);
        var sw = Stopwatch.StartNew();
        // Unparseable output is a retryable failure, not a verdict: the call layer re-asks once
        // with the shape restated. What it is *not* is a second opinion — the retry only decides
        // whether a document was read, never whether the reading was right. That stays with the
        // deterministic validator below, which the retry never re-runs or relaxes.
        var res = await claude.ExecAsync(BuildPrompt(full), model, "extract", ct,
            allowedTools: "Read", payloadCheck: PayloadProblem, retryNudge: RetryNudge);
        if (!res.Ok)
            return (Write(full, null, [$"extraction failed: {res.Error}"], res.CostUsd, model,
                sw.ElapsedMilliseconds, res.Attempts, res.FirstFailure), res.CostUsd, res.Attempts);

        // Parses by construction — PayloadProblem returned null for this exact text.
        var doc = JsonSerializer.Deserialize<ExtractedDoc>(
            ClaudeCliService.ExtractJson(res.Text, '{', '}'), JsonOpts)!;
        var violations = ValidationService.Validate(doc, config);
        return (Write(full, doc, violations, res.CostUsd, model, sw.ElapsedMilliseconds,
            res.Attempts, res.FirstFailure), res.CostUsd, res.Attempts);
    }

    /// <summary>
    /// Null when the model's text yields a usable document, otherwise why it does not. Cheap
    /// enough to run as the retry gate and then again for real — receipts are small documents.
    /// </summary>
    private static string? PayloadProblem(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<ExtractedDoc>(
                ClaudeCliService.ExtractJson(text, '{', '}'), JsonOpts) is null
                ? "model returned empty document"
                : null;
        }
        catch (JsonException ex)
        {
            return $"unparseable model output: {ex.Message}";
        }
    }

    private const string RetryNudge = """


        Your previous response could not be parsed as a JSON object. Return the same extraction
        again as ONE raw JSON object in exactly the shape above — no prose before or after it, no
        code fences, no trailing commentary. Do not re-read the document if you already have the
        values; only the formatting was wrong.
        """;

    private static string BuildPrompt(string absolutePath) => $$"""
        Read the receipt/invoice at this path and extract its fields: {{absolutePath}}

        Output ONLY one JSON object — no prose, no code fences — in exactly this shape:
        {
          "vendor":   { "value": "store or company name", "confidence": 0.0 },
          "date":     { "value": "yyyy-MM-dd", "confidence": 0.0 },
          "address":  { "value": "full address as printed", "confidence": 0.0 },
          "total":    { "value": 0.0, "confidence": 0.0 },
          "currency": { "value": "ISO 4217 code", "confidence": 0.0 },
          "tax":      { "value": 0.0, "confidence": 0.0 },
          "line_items": [
            { "description": "", "qty": 0.0, "unit_price": 0.0, "amount": 0.0 }
          ]
        }

        Rules:
        - confidence is your own 0..1 estimate per field, based on legibility and ambiguity
        - use null for a field value you cannot find; never invent values
        - date: convert whatever format is printed to yyyy-MM-dd
        - total/tax/amounts: plain numbers, no currency symbols
        - currency: infer the ISO code from symbols, language, or tax wording on the receipt
        - line_items: [] if the document has none or they are illegible
        """;

    private bool Write(string sourceFile, ExtractedDoc? doc, List<string> violations, decimal cost,
        string model, long elapsedMs, int attempts, string? retriedAfter)
    {
        var accepted = violations.Count == 0;
        var dir = Path.Combine(dataDir, "extractions", accepted ? "accepted" : "needs-review");
        Directory.CreateDirectory(dir);
        // A re-run (e.g. escalation) replaces the older artifact wherever it landed, so one
        // basename never has two competing artifacts across the accepted/needs-review split.
        var name = Path.GetFileNameWithoutExtension(sourceFile) + ".json";
        var other = Path.Combine(dataDir, "extractions", accepted ? "needs-review" : "accepted", name);
        if (File.Exists(other)) File.Delete(other);
        var artifact = new ExtractionArtifact(
            Path.GetFileName(sourceFile), accepted ? "accepted" : "needs-review",
            violations, doc, cost, DateTime.UtcNow.ToString("o"), model, elapsedMs,
            attempts, retriedAfter);
        File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(artifact, JsonOpts));
        return accepted;
    }

    public static IEnumerable<ExtractionArtifact> LoadArtifacts(string dataDir)
    {
        foreach (var sub in new[] { "accepted", "needs-review" })
        {
            var dir = Path.Combine(dataDir, "extractions", sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var artifact = JsonSerializer.Deserialize<ExtractionArtifact>(
                    File.ReadAllText(file), JsonOpts);
                if (artifact is not null) yield return artifact;
            }
        }
    }
}
