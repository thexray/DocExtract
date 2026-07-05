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
        case "extract":
            Console.Error.WriteLine("extract: not implemented yet (W1)");
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
