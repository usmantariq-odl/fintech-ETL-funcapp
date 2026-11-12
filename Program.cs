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
using Microsoft.Extensions.Logging;

var hierarchy = (Hierarchy)LogManager.GetRepository();
hierarchy.Root.AddAppender(new ConsoleAppender { Layout = new PatternLayout("%date %-5level %logger - %message%newline%exception") });
hierarchy.Root.Level = log4net.Core.Level.Info;
hierarchy.Configured = true;

if(args.Contains("--alson-archive-processed"))
{
    await FintechEtl.FunctionApp.Alson.AlsonArchiveCommand.Run(args);
    return;
}

if(args.Contains("--alson-diagnostics-scan"))
{
    using(var j=JsonDocument.Parse(File.ReadAllText("alson.local.json")))
        foreach(var v in j.RootElement.GetProperty("Values").EnumerateObject())Environment.SetEnvironmentVariable(v.Name,v.Value.GetString());
    Environment.SetEnvironmentVariable("AlsonEtlEnabled","true");
    Environment.SetEnvironmentVariable("AlsonEtlDryRun",args.Contains("--apply")?"false":"true");
    Environment.SetEnvironmentVariable("AlsonErrorEmailEnabled","false");
    Environment.SetEnvironmentVariable("AlsonMaxFilesPerRun","100");
    string Option(string name,string fallback){int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
    string inputFile=Option("--inputs",null);
    if(inputFile==null)throw new InvalidOperationException("A reviewed --inputs JSON filename array is required for the local diagnostic test.");
    var inputs=JsonSerializer.Deserialize<string[]>(File.ReadAllText(inputFile))!.ToHashSet(StringComparer.Ordinal);
    if(inputs.Count is <1 or >100||inputs.Any(n=>!n.StartsWith("Alson/inbox/",StringComparison.Ordinal)))throw new InvalidOperationException("Select 1..100 inbox files.");
    using var factory=LoggerFactory.Create(builder=>builder.AddSimpleConsole());
    var logger=factory.CreateLogger<FintechEtl.FunctionApp.Alson.AlsonEtlTimer>();
    var runner=new FintechEtl.FunctionApp.Alson.AlsonEtlTimer(logger);
    var summary=await runner.Scan(FintechEtl.FunctionApp.Alson.AlsonSettings.Load(),new FintechEtl.FunctionApp.Alson.AlsonAlerts(logger),CancellationToken.None,
        manifestPath:Option("--manifest",Path.Combine("artifacts","alson","diagnostics-last-run.json")),runBudget:TimeSpan.FromMinutes(10),diagnosticsOnly:true,selectedInputs:inputs);
    Console.WriteLine(JsonSerializer.Serialize(summary));
    if(summary.Outcomes.GetValueOrDefault("invalid")+summary.Outcomes.GetValueOrDefault("pending_mapping")>0)Environment.ExitCode=2;
    return;
}

// Explicit bounded local Alson runner. Never invokes Dover or sends a local email.
if (args.Contains("--alson-scan"))
{
    var path=Path.Combine(Directory.GetCurrentDirectory(),"alson.local.json");
    using var json=JsonDocument.Parse(File.ReadAllText(path));
    foreach(var v in json.RootElement.GetProperty("Values").EnumerateObject()) Environment.SetEnvironmentVariable(v.Name,v.Value.GetString());
    Environment.SetEnvironmentVariable("AlsonEtlEnabled","true");
    Environment.SetEnvironmentVariable("AlsonErrorEmailEnabled","false");
    Environment.SetEnvironmentVariable("AlsonEtlDryRun",args.Contains("--apply")?"false":"true");
    string Option(string name,string fallback){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
    int sales=int.Parse(Option("--sales","100")),atg=int.Parse(Option("--atg-frames","25"));
    if(sales<0||sales>100||atg<0||atg>25)throw new InvalidOperationException("Local Alson runner is limited to 100 sales / 25 ATG frames.");
    using var loggerFactory=LoggerFactory.Create(builder=>builder.AddSimpleConsole());
    var logger=loggerFactory.CreateLogger<FintechEtl.FunctionApp.Alson.AlsonEtlTimer>();
    var runner=new FintechEtl.FunctionApp.Alson.AlsonEtlTimer(logger);
    var summary=await runner.Scan(FintechEtl.FunctionApp.Alson.AlsonSettings.Load(),new FintechEtl.FunctionApp.Alson.AlsonAlerts(logger),CancellationToken.None,
        sales,atg,Option("--manifest",Path.Combine("artifacts","alson","last-run.json")),50,13,TimeSpan.FromMinutes(15));
    Console.WriteLine(JsonSerializer.Serialize(summary));
    if(summary.Sales!=sales||summary.TankReadings!=atg*4) Environment.ExitCode=2;
    return;
}

// Explicit test sends only to the fixed test recipient; it never processes files or connects to SQL/SFTP.
if (args.Contains("--test-error-email") || args.Contains("--preview-error-email"))
{
    var local = Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json");
    if (File.Exists(local))
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(local));
        foreach (var entry in settings.RootElement.GetProperty("Values").EnumerateObject())
            if (Environment.GetEnvironmentVariable(entry.Name) == null) Environment.SetEnvironmentVariable(entry.Name, entry.Value.GetString());
    }
    var digest = new AlertDigest();
    digest.Add(new EtlAlert("TEST / Partial", "007001 (example)", "/Unprocessed/007001/input/example.tgz (synthetic)",
        "Example only: successful CSVs would go to Processed and failed CSVs to ExceptionFile.",
        "Synthetic validation example: PSL.csv has a missing product mapping. No live data was read or changed.",
        "test-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
        "Example database (synthetic)", "fintechsftpdata.blob.core.windows.net"));
    var body = EtlErrorEmails.Render(digest, Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "Fintech ETL", true);
    if (args.Contains("--preview-error-email"))
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "etl-error-email");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Example-Email.html"), body.Html);
        Console.WriteLine("Email preview saved: " + Path.Combine(directory, "Example-Email.html"));
        return;
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var operation = await EtlErrorEmails.Send(Environment.GetEnvironmentVariable("ACS_EMAIL_CONNECTION_STRING"),
        ["usman.tariq@octopusdtl.com"], body.Subject, body.Text, body.Html, timeout.Token);
    Console.WriteLine("Test email accepted by ACS (Succeeded); recipient: usman.tariq@octopusdtl.com; operation: " + operation);
    return;
}

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
