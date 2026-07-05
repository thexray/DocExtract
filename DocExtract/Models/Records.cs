namespace DocExtract.Models;

/// <summary>Outcome of one headless CLI call. Cost is the CLI-reported total for the call.</summary>
public sealed record ClaudeResult(bool Ok, string Text, decimal CostUsd, string? Error)
{
    public static ClaudeResult Fail(string error) => new(false, "", 0m, error);
}

/// <summary>A field the model extracted, with its self-reported confidence (0..1).</summary>
public sealed record Field<T>(T? Value, double Confidence);

public sealed record LineItem(string? Description, double? Qty, double? UnitPrice, double? Amount);

/// <summary>
/// The extraction schema. SROIE ground truth covers vendor/date/address/total; line items
/// are a CORD-only eval dimension (kept nullable — absence is not a violation).
/// </summary>
// Numeric fields are Field<double?> deliberately: with an unconstrained generic, T? on a
// value type is not Nullable<T>, so Field<double> rejects the model's legitimate
// "value": null for absent fields (found the hard way in the W1 smoke run).
public sealed record ExtractedDoc(
    Field<string>? Vendor,
    Field<string>? Date,
    Field<string>? Address,
    Field<double?>? Total,
    Field<string>? Currency,
    Field<double?>? Tax,
    List<LineItem>? LineItems);

/// <summary>What extract writes per document and eval reads back.</summary>
public sealed record ExtractionArtifact(
    string Source,
    string Status,
    List<string> Violations,
    ExtractedDoc? Extraction,
    decimal CostUsd,
    string Ts,
    string Model,
    long ElapsedMs);
