namespace DocExtract.Models;

/// <summary>Outcome of one headless CLI call. Cost is the CLI-reported total for the call.</summary>
public sealed record ClaudeResult(bool Ok, string Text, decimal CostUsd, string? Error)
{
    public static ClaudeResult Fail(string error) => new(false, "", 0m, error);
}
