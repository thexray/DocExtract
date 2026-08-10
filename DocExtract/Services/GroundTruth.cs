namespace DocExtract.Services;

using System.Globalization;
using System.Text;

/// <summary>
/// Comparators for SROIE ground-truth strings, shared by the extraction eval and the
/// retrieval slice. They live in one place on purpose: the retrieval question set is derived
/// from the same ground truth the extraction eval scores against, so if the two ever
/// disagreed about what "same company" or "same date" means, the retrieval numbers would be
/// measuring the disagreement rather than the retriever. Three comparator bugs in the W2 eval
/// were found by eyeballing mismatches; there is no appetite for a second copy that can
/// re-acquire them.
/// </summary>
internal static class GroundTruth
{
    // SROIE keys keep whatever format the receipt printed, day-first (Malaysia): two-digit
    // years and single-digit day/month appear alongside the long forms.
    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy",
        "dd/MM/yy", "d/M/yy", "dd-MM-yy", "d-M-yy", "dd.MM.yy",
        "yyyy-MM-dd", "dd MMM yyyy", "d MMM yyyy", "dd MMM yy",
        // Compact all-digit keys ("20180304", "25032018"). Without these a correct reading
        // scores as a miss purely on format, which is the one thing this comparator exists to
        // prevent. The two forms cannot collide on real receipt dates: a yyyyMMdd string for a
        // 20xx year reads as day 20, month 18 under ddMMyyyy, and a ddMMyyyy string puts the
        // "20" of the year in the month position — each is invalid under the other.
        "yyyyMMdd", "ddMMyyyy",
    ];

    public static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var d2) ? d2 : null;

    /// <summary>GT amounts carry currency marks the invariant parser rejects ("$8.20",
    /// "RM 8.20") — strip to the numeric core before comparing.</summary>
    public static decimal? ParseAmount(string s)
    {
        var numeric = new string(s.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        return decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Case/whitespace/punctuation-insensitive: OCR-adjacent strings should tie.</summary>
    public static string Normalize(string s)
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
