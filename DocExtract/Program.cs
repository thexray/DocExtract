using System.Text;
using System.Text.Json;
using DocExtract.Services;
using Microsoft.Extensions.Configuration;

// Config layering: committed defaults → gitignored dev secrets → env vars.
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var dataDir = Path.GetFullPath(config["DataDirectory"] ?? "./data");
Directory.CreateDirectory(dataDir);

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var exit = 0;
try
{
    var ledger = new CostLedger(dataDir);
    var claude = new ClaudeCliService(config, ledger);
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var budget = decimal.TryParse(config["EvalBudgetUsd"], out var b) ? b : 10m;

    switch (verb)
    {
        case "extract" when args.Length >= 2:
        {
            var target = args[1];
            var parallel = Math.Max(1, OptInt("--parallel", 1));
            var tier = Opt("--tier", "extraction");
            var model = tier == "escalation" ? claude.EscalationModel : claude.ExtractionModel;

            string[] files =
                Directory.Exists(target)
                    ? Directory.EnumerateFiles(target)
                        .Where(f => ExtractionService.SupportedExtensions.Contains(
                            Path.GetExtension(f).ToLowerInvariant()))
                        .Order().ToArray()
                : Path.GetExtension(target).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    ? File.ReadAllLines(target).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
                    : [target];
            var limit = OptInt("--limit", files.Length);
            files = files.Take(limit).ToArray();
            if (args.Contains("--skip-existing"))
            {
                var done = ExtractionService.LoadArtifacts(dataDir)
                    .Select(a => Path.GetFileNameWithoutExtension(a.Source))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var before = files.Length;
                files = files.Where(f => !done.Contains(Path.GetFileNameWithoutExtension(f))).ToArray();
                Console.WriteLine($"extract: --skip-existing removed {before - files.Length} already-done docs");
            }
            if (files.Length == 0) { Console.Error.WriteLine($"extract: no supported files in {target}"); exit = 1; break; }

            var svc = new ExtractionService(claude, config, dataDir);
            var gate = new SemaphoreSlim(parallel);
            var console = new object();
            var (ok, review, skipped) = (0, 0, 0);
            var totalCost = 0m;

            var tasks = files.Select(async file =>
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    // Budget guard: the cap is structural, not advisory. Month-to-date spend
                    // at or over the budget stops new calls; in-flight ones finish.
                    if (ledger.MonthToDate() >= budget)
                    {
                        lock (console) { skipped++; }
                        return;
                    }
                    var (accepted, cost) = await svc.ProcessAsync(file, model, cts.Token);
                    lock (console)
                    {
                        totalCost += cost;
                        if (accepted) ok++; else review++;
                        Console.WriteLine($"  {Path.GetFileName(file),-20} {(accepted ? "accepted" : "needs-review"),-12} ${cost:0.0000}");
                    }
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);

            Console.WriteLine($"extract [{model}]: {ok} accepted, {review} needs-review" +
                (skipped > 0 ? $", {skipped} SKIPPED (budget ${budget:0.00} reached)" : "") +
                $", ${totalCost:0.00} this run → {Path.Combine(dataDir, "extractions")}");
            if (skipped > 0) exit = 2;
            break;
        }

        case "extract":
            Console.Error.WriteLine("extract: usage: docextract extract <file|dir|list.txt> [--parallel N] [--tier extraction|escalation] [--limit N] [--skip-existing]");
            exit = 1;
            break;

        case "eval":
            exit = new EvalService(config, dataDir).Run(Opt("--label", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}"), cts.Token);
            break;

        case "report":
        {
            var spent = ledger.MonthToDate();
            Console.WriteLine($"LLM cost month-to-date: ${spent:0.00} of ${budget:0.00} budget");
            var runsPath = Path.Combine(dataDir, "eval_runs.jsonl");
            if (!File.Exists(runsPath)) { Console.WriteLine("no eval runs recorded yet"); break; }

            var table = new StringBuilder();
            table.AppendLine("| Run | Models | Docs | Company | Date | Address | Total | Exact match | Cost | $/doc | Avg s/doc |");
            table.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var line in File.ReadLines(runsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var run = JsonDocument.Parse(line);
                var r = run.RootElement;
                var docs = r.GetProperty("docs").GetInt32();
                var cost = r.GetProperty("cost_usd").GetDecimal();
                var acc = r.GetProperty("accuracy");
                table.AppendLine(
                    $"| {r.GetProperty("label").GetString()} | {r.GetProperty("models").GetString()} | {docs} " +
                    $"| {acc.GetProperty("company").GetDouble():P1} | {acc.GetProperty("date").GetDouble():P1} " +
                    $"| {acc.GetProperty("address").GetDouble():P1} | {acc.GetProperty("total").GetDouble():P1} " +
                    $"| {r.GetProperty("exact_match").GetDouble():P1} | ${cost:0.00} | ${cost / docs:0.000} " +
                    $"| {r.GetProperty("avg_ms").GetInt64() / 1000.0:0.0} |");
            }
            Console.WriteLine(table.ToString());

            // Regenerate the README block between the eval markers, if the README is present.
            var readme = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "README.md");
            readme = Path.GetFullPath(readme);
            if (File.Exists(readme))
            {
                const string begin = "<!-- eval-results:begin -->", end = "<!-- eval-results:end -->";
                var text = File.ReadAllText(readme);
                var (i, j) = (text.IndexOf(begin), text.IndexOf(end));
                if (i >= 0 && j > i)
                {
                    File.WriteAllText(readme, text[..(i + begin.Length)] + "\n" + table + text[j..]);
                    Console.WriteLine($"README results table regenerated: {readme}");
                }
            }
            break;
        }

        case "check":
            Console.WriteLine("DocExtract config check");
            Console.WriteLine($"  DataDirectory     {dataDir}");
            Console.WriteLine($"  ExtractionModel   {claude.ExtractionModel}");
            Console.WriteLine($"  EscalationModel   {claude.EscalationModel}");
            Console.WriteLine($"  EvalBudgetUsd     {config["EvalBudgetUsd"]}");
            Console.WriteLine($"  Sroie:KeysDir     {(Directory.Exists(config["Sroie:KeysDir"] ?? "") ? "ok" : "MISSING")}");
            Console.WriteLine($"  ClaudeCli:Path    {(string.IsNullOrWhiteSpace(config["ClaudeCli:Path"]) ? "(PATH default: claude)" : "set")}");
            break;

        default:
            Console.WriteLine("""
                docextract — LLM document-extraction pipeline with an eval harness

                usage:
                  docextract extract <file|dir|list.txt> [--parallel N] [--tier extraction|escalation] [--limit N]
                  docextract eval [--label NAME]      score artifacts against ground truth
                  docextract report                   eval results table + month-to-date LLM cost
                  docextract check                    config smoke check (key presence only)
                """);
            exit = verb == "help" ? 0 : 1;
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"docextract {verb}: {ex.Message}");
    exit = 1;
}
return exit;

int OptInt(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

string Opt(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
