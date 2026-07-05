namespace DocExtract.Services;

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

    public async Task<(bool Accepted, decimal Cost)> ProcessAsync(string file, CancellationToken ct)
    {
        var full = Path.GetFullPath(file);
        var res = await claude.ExecAsync(BuildPrompt(full), claude.ExtractionModel, "extract", ct,
            allowedTools: "Read");
        if (!res.Ok)
            return (Write(full, null, [$"model call failed: {res.Error}"], res.CostUsd), res.CostUsd);

        ExtractedDoc? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ExtractedDoc>(
                ClaudeCliService.ExtractJson(res.Text, '{', '}'), JsonOpts);
        }
        catch (JsonException ex)
        {
            return (Write(full, null, [$"unparseable model output: {ex.Message}"], res.CostUsd), res.CostUsd);
        }
        if (doc is null)
            return (Write(full, null, ["model returned empty document"], res.CostUsd), res.CostUsd);

        var violations = ValidationService.Validate(doc, config);
        return (Write(full, doc, violations, res.CostUsd), res.CostUsd);
    }

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

    private bool Write(string sourceFile, ExtractedDoc? doc, List<string> violations, decimal cost)
    {
        var accepted = violations.Count == 0;
        var dir = Path.Combine(dataDir, "extractions", accepted ? "accepted" : "needs-review");
        Directory.CreateDirectory(dir);
        var artifact = new
        {
            source = Path.GetFileName(sourceFile),
            status = accepted ? "accepted" : "needs-review",
            violations,
            extraction = doc,
            cost_usd = cost,
            ts = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(
            Path.Combine(dir, Path.GetFileNameWithoutExtension(sourceFile) + ".json"),
            JsonSerializer.Serialize(artifact, JsonOpts));
        return accepted;
    }
}
