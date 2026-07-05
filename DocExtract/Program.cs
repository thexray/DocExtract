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

    switch (verb)
    {
        case "extract" when args.Length >= 2:
        {
            var target = args[1];
            var files = Directory.Exists(target)
                ? Directory.EnumerateFiles(target)
                    .Where(f => ExtractionService.SupportedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .Order().ToArray()
                : [target];
            if (files.Length == 0) { Console.Error.WriteLine($"extract: no supported files in {target}"); exit = 1; break; }

            var svc = new ExtractionService(claude, config, dataDir);
            var (ok, review, totalCost) = (0, 0, 0m);
            foreach (var file in files)
            {
                var (accepted, cost) = await svc.ProcessAsync(file, cts.Token);
                totalCost += cost;
                if (accepted) ok++; else review++;
                Console.WriteLine($"  {Path.GetFileName(file),-20} {(accepted ? "accepted" : "needs-review"),-12} ${cost:0.0000}");
            }
            Console.WriteLine($"extract: {ok} accepted, {review} needs-review, ${totalCost:0.00} total → {Path.Combine(dataDir, "extractions")}");
            break;
        }

        case "extract":
            Console.Error.WriteLine("extract: usage: docextract extract <file|dir>");
            exit = 1;
            break;

        case "eval":
            Console.Error.WriteLine("eval: not implemented yet (W2)");
            exit = 1;
            break;

        case "report":
        {
            var budget = decimal.TryParse(config["EvalBudgetUsd"], out var b) ? b : 10m;
            var spent = ledger.MonthToDate();
            Console.WriteLine($"LLM cost month-to-date: ${spent:0.00} of ${budget:0.00} budget");
            Console.WriteLine("eval results table: not implemented yet (W2)");
            break;
        }

        case "check":
            Console.WriteLine("DocExtract config check");
            Console.WriteLine($"  DataDirectory     {dataDir}");
            Console.WriteLine($"  ExtractionModel   {claude.ExtractionModel}");
            Console.WriteLine($"  EscalationModel   {claude.EscalationModel}");
            Console.WriteLine($"  EvalBudgetUsd     {config["EvalBudgetUsd"]}");
            Console.WriteLine($"  ClaudeCli:Path    {(string.IsNullOrWhiteSpace(config["ClaudeCli:Path"]) ? "(PATH default: claude)" : "set")}");
            break;

        default:
            Console.WriteLine("""
                docextract — LLM document-extraction pipeline with an eval harness

                usage:
                  docextract extract <path|dir>   image/PDF → typed JSON, validated,
                                                  split into accepted/ and needs-review/
                  docextract eval                 score extractions against ground truth
                  docextract report               eval results table + month-to-date LLM cost
                  docextract check                config smoke check (key presence only)
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
