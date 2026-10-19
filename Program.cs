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

if (args.Contains("--prepare-con"))
{
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json")));
    foreach (var value in json.RootElement.GetProperty("Values").EnumerateObject())
        Environment.SetEnvironmentVariable(value.Name, value.Value.GetString());
    // Provision directories only; never processes archives or writes SQL data.
    EtlTimer.PrepareConFolders(EtlSettings.Load());
    return;
}

if (args.Contains("--verify-sql"))
{
    var local = Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json");
    using var json = JsonDocument.Parse(File.ReadAllText(local));
    foreach (var value in json.RootElement.GetProperty("Values").EnumerateObject())
        Environment.SetEnvironmentVariable(value.Name, value.Value.GetString());
    var settings = EtlSettings.Load();
    SqlSchemaVerifier.Verify(settings.SqlConnectionString);
    var mapping = new OmniFintechMapping(LogManager.GetLogger(typeof(OmniFintechMapping)));
    Console.WriteLine($"Existing ETL mapping loaded; FC site 600005 maps to Omni site {mapping.getOmniSiteID(600005)}.");
    return;
}

// Local one-shot execution uses the same ETL method as the timer.
if (args.Contains("--scan-once") || args.Contains("--process-once"))
{
    var processOnce = args.Contains("--process-once");
    var local = Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json");
    if (!File.Exists(local)) local = Path.Combine(AppContext.BaseDirectory, "local.settings.json");
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
