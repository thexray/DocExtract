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

    /// <summary>
    /// Month-to-date split into first attempts and retries. Retries are logged as
    /// <c>{purpose}:retry</c>; keeping their spend visible is the point — a retry rate that
    /// starts costing real money is a prompt problem, and the ledger is where it shows up first.
    /// </summary>
    public (int Calls, int Retries, decimal RetryCostUsd) MonthToDateRetries()
    {
        if (!File.Exists(_path)) return (0, 0, 0m);
        var monthPrefix = DateTime.UtcNow.ToString("yyyy-MM");
        var (calls, retries, retryCost) = (0, 0, 0m);
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("ts").GetString()?.StartsWith(monthPrefix) != true) continue;
            calls++;
            if (root.GetProperty("purpose").GetString()?.EndsWith(":retry") != true) continue;
            retries++;
            retryCost += root.GetProperty("cost_usd").GetDecimal();
        }
        return (calls, retries, retryCost);
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
