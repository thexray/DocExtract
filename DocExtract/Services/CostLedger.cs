namespace DocExtract.Services;

using System.Text.Json;

/// <summary>
/// Append-only JSONL cost ledger (lean re-take of Radar's SQLite llm_calls table — this
/// project's storage is JSONL by decision). One line per LLM call; month-to-date sums are
/// computed by scanning, which is fine at eval-run volumes.
/// </summary>
public sealed class CostLedger(string dataDir)
{
    private readonly string _path = Path.Combine(dataDir, "llm_calls.jsonl");
    private static readonly object Gate = new();

    public void Log(string purpose, string model, decimal costUsd)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            purpose,
            model,
            cost_usd = costUsd,
        });
        lock (Gate) File.AppendAllText(_path, line + Environment.NewLine);
    }

    public decimal MonthToDate()
    {
        if (!File.Exists(_path)) return 0m;
        var monthPrefix = DateTime.UtcNow.ToString("yyyy-MM");
        decimal sum = 0m;
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("ts").GetString()?.StartsWith(monthPrefix) == true)
                sum += doc.RootElement.GetProperty("cost_usd").GetDecimal();
        }
        return sum;
    }
}
