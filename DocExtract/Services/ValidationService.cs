namespace DocExtract.Services;

using System.Globalization;
using DocExtract.Models;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Deterministic validation after the LLM: the model never grades its own output. Every
/// rule is a plain predicate; the returned list of violations decides the
/// accepted/needs-review split.
/// </summary>
public static class ValidationService
{
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "d MMM yyyy"];

    public static List<string> Validate(ExtractedDoc doc, IConfiguration config)
    {
        var violations = new List<string>();
        var minConf = config.GetValue("Validation:MinFieldConfidence", 0.6);
        var whitelist = config.GetSection("Validation:CurrencyWhitelist").Get<string[]>() ?? [];

        Require(violations, doc.Vendor, "vendor", minConf, v => !string.IsNullOrWhiteSpace(v));
        Require(violations, doc.Total, "total", minConf, v => v > 0);
        Require(violations, doc.Date, "date", minConf, v =>
            DateTime.TryParseExact(v, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || DateTime.TryParse(v, CultureInfo.InvariantCulture, out _));

        if (doc.Currency?.Value is { } cur)
        {
            if (whitelist.Length > 0 && !whitelist.Contains(cur, StringComparer.OrdinalIgnoreCase))
                violations.Add($"currency '{cur}' not in whitelist");
        }
        else violations.Add("currency missing");

        if (doc.Tax?.Value is { } tax && doc.Total?.Value is { } totalForTax && tax >= totalForTax)
            violations.Add($"tax {tax} >= total {totalForTax}");

        if (doc.LineItems is { Count: > 0 } items && doc.Total?.Value is { } total)
        {
            var sum = items.Sum(i => i.Amount ?? 0);
            var tolAbs = config.GetValue("Validation:TotalToleranceAbs", 0.02);
            var tolPct = config.GetValue("Validation:TotalTolerancePct", 1.0);
            var allowed = Math.Max(tolAbs, total * tolPct / 100.0);
            if (Math.Abs(sum - total) > allowed)
                violations.Add($"line items sum {sum:0.00} differs from total {total:0.00} by more than {allowed:0.00}");
        }

        return violations;
    }

    private static void Require<T>(List<string> violations, Field<T>? field, string name,
        double minConf, Func<T, bool> ok)
    {
        if (field?.Value is not { } value) { violations.Add($"{name} missing"); return; }
        if (!ok(value)) violations.Add($"{name} invalid: '{value}'");
        else if (field.Confidence < minConf)
            violations.Add($"{name} confidence {field.Confidence:0.00} below {minConf:0.00}");
    }
}
