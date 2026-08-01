namespace DocExtract.Models;

/// <summary>
/// Outcome of one logical model call. <paramref name="CostUsd"/> is the CLI-reported cost
/// summed over every attempt, so a retried call reports what it actually cost, not what the
/// winning attempt cost. <paramref name="FirstFailure"/> is non-null only when an earlier
/// attempt failed — it survives into the artifact as the record of what was recovered from.
/// </summary>
public sealed record ClaudeResult(bool Ok, string Text, decimal CostUsd, string? Error,
    int Attempts = 1, string? FirstFailure = null)
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

/// <summary>
/// What extract writes per document and eval reads back. Attempts/RetriedAfter are trailing
/// optionals: artifacts written before in-loop retry existed have neither, and deserialize
/// with Attempts = 1 (net8 honours constructor parameter defaults for absent properties).
/// </summary>
public sealed record ExtractionArtifact(
    string Source,
    string Status,
    List<string> Violations,
    ExtractedDoc? Extraction,
    decimal CostUsd,
    string Ts,
    string Model,
    long ElapsedMs,
    int Attempts = 1,
    string? RetriedAfter = null);
