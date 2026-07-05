namespace DocExtract.Services;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocExtract.Models;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Headless Claude Code CLI wrapper: <c>claude -p --model X --output-format json</c>,
/// prompt via stdin (avoids argument-length limits and the CLI's 3s stdin wait).
/// Billed against the subscription, not per-token API charges. Lifted from Radar;
/// the only deviation is cost logging to the JSONL ledger instead of SQLite.
/// </summary>
public sealed class ClaudeCliService(IConfiguration config, CostLedger ledger)
{
    public string ExtractionModel => config["ClaudeCli:ExtractionModel"] ?? "claude-haiku-4-5";
    public string EscalationModel => config["ClaudeCli:EscalationModel"] ?? "claude-sonnet-5";

    public async Task<ClaudeResult> ExecAsync(string prompt, string model, string purpose,
        CancellationToken ct, string? allowedTools = null)
    {
        var cli = config["ClaudeCli:Path"];
        if (string.IsNullOrWhiteSpace(cli)) cli = "claude";
        var timeout = TimeSpan.FromSeconds(int.TryParse(config["ClaudeCli:DefaultTimeoutSeconds"], out var t) ? t : 600);
        var tools = string.IsNullOrWhiteSpace(allowedTools) ? "" : $" --allowedTools {allowedTools}";

        var psi = new ProcessStartInfo
        {
            // npm shim on Windows resolves via cmd (claude.cmd); direct exec of .ps1 does not work.
            FileName = "cmd.exe",
            Arguments = $"/c {cli} -p --model {model} --output-format json{tools}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return ClaudeResult.Fail("failed to start claude CLI");

        await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return ClaudeResult.Fail($"timed out after {timeout.TotalSeconds:0}s");
        }

        var stdout = await stdoutTask;
        if (proc.ExitCode != 0)
            return ClaudeResult.Fail($"exit {proc.ExitCode}: {(await stderrTask).Trim()}");

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var isError = root.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
            var text = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            var cost = root.TryGetProperty("total_cost_usd", out var c) ? c.GetDecimal() : 0m;
            ledger.Log(purpose, model, cost);
            return isError ? ClaudeResult.Fail($"CLI reported error: {text}") : new ClaudeResult(true, text, cost, null);
        }
        catch (JsonException ex)
        {
            return ClaudeResult.Fail($"unparseable CLI output ({ex.Message}): {stdout[..Math.Min(stdout.Length, 300)]}");
        }
    }

    /// <summary>Extracts the first JSON payload from model text that may carry ``` fences or prose.</summary>
    public static string ExtractJson(string text, char open, char close)
    {
        var start = text.IndexOf(open);
        var end = text.LastIndexOf(close);
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
