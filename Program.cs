using log4net;
using log4net.Appender;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using FintechEtl.FunctionApp;
using CSVReader;
using System.Data.SqlClient;

var hierarchy = (Hierarchy)LogManager.GetRepository();
hierarchy.Root.AddAppender(new ConsoleAppender { Layout = new PatternLayout("%date %-5level %logger - %message%newline%exception") });
hierarchy.Root.Level = log4net.Core.Level.Info;
hierarchy.Configured = true;

if (args.Contains("--verify-sql"))
{
    var local = Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json");
    using var json = JsonDocument.Parse(File.ReadAllText(local));
    foreach (var value in json.RootElement.GetProperty("Values").EnumerateObject())
        Environment.SetEnvironmentVariable(value.Name, value.Value.GetString());
    var settings = EtlSettings.Load();
    using var connection = new SqlConnection(settings.SqlConnectionString);
    connection.Open();
    using var command = new SqlCommand(@"SELECT DB_NAME(),
        (SELECT COUNT(*) FROM sys.tables WHERE name IN ('Fintech_ProcessedFiles','Fintech_Transactions','Fintech_TankLevels','Fintech_TankDelivery','FintechMappedCustomers','Omni_Fintech_SiteMapping','Omni_Fintech_TankMapping','Plant_Site','Omni_Fintech_Products')),
        (SELECT COUNT(*) FROM sys.procedures WHERE name IN ('InsertintoFintechTransactions','InsertintoFintechTransactionsType','InsertintoFintechTankLevels','InsertintoFintechTanksDelivery','InsertintoFintechAlarms','Fintech_Regenerated_Files')),
        (SELECT COUNT(*) FROM Omni_Fintech_SiteMapping WHERE FC_SiteID BETWEEN 600000 AND 600050)", connection);
    using var result = command.ExecuteReader();
    result.Read();
    Console.WriteLine($"SQL database: {result.GetString(0)}; required tables: {result.GetInt32(1)}/9; stored procedures: {result.GetInt32(2)}/6; simulator site mappings: {result.GetInt32(3)}");
    result.Close();
    var mapping = new OmniFintechMapping(LogManager.GetLogger(typeof(OmniFintechMapping)));
    Console.WriteLine($"Existing ETL mapping loaded; FC site 600005 maps to Omni site {mapping.getOmniSiteID(600005)}.");
    return;
}

// Local one-shot execution uses the same ETL method as the timer.
if (args.Contains("--scan-once") || args.Contains("--process-once"))
{
    var processOnce = args.Contains("--process-once");
    var local = Path.Combine(AppContext.BaseDirectory, "local.settings.json");
    if (!File.Exists(local)) local = Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json");
    using var json = JsonDocument.Parse(File.ReadAllText(local));
    foreach (var value in json.RootElement.GetProperty("Values").EnumerateObject())
        Environment.SetEnvironmentVariable(value.Name, value.Value.GetString());
    if (!processOnce && !string.Equals(Environment.GetEnvironmentVariable("EtlDryRun"), "true", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("--scan-once requires EtlDryRun=true.");
    if (processOnce) Environment.SetEnvironmentVariable("EtlDryRun", "false");
    Environment.SetEnvironmentVariable("EtlLocalRun", "true");
    var siteIndex = Array.IndexOf(args, "--site");
    if (siteIndex >= 0 && siteIndex + 1 < args.Length)
        Environment.SetEnvironmentVariable("EtlLocalSiteFilter", args[siteIndex + 1]);
    var siteMinIndex = Array.IndexOf(args, "--site-min");
    if (siteMinIndex >= 0 && siteMinIndex + 1 < args.Length)
        Environment.SetEnvironmentVariable("EtlLocalSiteMin", args[siteMinIndex + 1]);
    var siteMaxIndex = Array.IndexOf(args, "--site-max");
    if (siteMaxIndex >= 0 && siteMaxIndex + 1 < args.Length)
        Environment.SetEnvironmentVariable("EtlLocalSiteMax", args[siteMaxIndex + 1]);
    var maxIndex = Array.IndexOf(args, "--max-files");
    if (maxIndex >= 0 && maxIndex + 1 < args.Length)
        Environment.SetEnvironmentVariable("EtlLocalMaxFiles", args[maxIndex + 1]);
    var runner = new EtlTimer(NullLogger<EtlTimer>.Instance);
    runner.Run(null);
    if (runner.LastRunFailed > 0) Environment.ExitCode = 1;
    return;
}

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .Build();
host.Run();
