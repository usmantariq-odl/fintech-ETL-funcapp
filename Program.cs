using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FintechAutoSiteConfiguration.FunctionApp;
using System.Text.Json;
using FintechAutoSiteConfiguration.FunctionApp.Storage;

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
    digest.Add(new EtlAlert("ConfigurationRejectedOrFailed (example)", "002010 (example)", "/Unprocessed/CON-00201020261103120000.csv (synthetic)",
        "Example only: rejected CON file moved to /Exception/2010/. No site changes were made for this test.",
        "Synthetic validation example: CON tank product UnknownFuel has no matching customer product. No live data was read and no configuration was changed.",
        "test-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
        "Example database (synthetic)", "fintechsftpdata.blob.core.windows.net"));
    var body = AutoSiteErrorEmails.Render(digest, Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "Fintech AutoSite", true);
    if (args.Contains("--preview-error-email"))
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "autosite-error-email");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Example-Email.html"), body.Html);
        Console.WriteLine("Email preview saved: " + Path.Combine(directory, "Example-Email.html"));
        return;
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var operation = await AutoSiteErrorEmails.Send(Environment.GetEnvironmentVariable("ACS_EMAIL_CONNECTION_STRING"),
        ["usman.tariq@octopusdtl.com"], body.Subject, body.Text, body.Html, timeout.Token);
    Console.WriteLine("Test email accepted by ACS (Succeeded); recipient: usman.tariq@octopusdtl.com; operation: " + operation);
    return;
}

// Read-only connectivity check: never resolves the parser or connects to SQL.
if (args.Contains("--list-input"))
{
    var configuration = new ConfigurationBuilder();
    var localPath = Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json");
    if (File.Exists(localPath))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(localPath));
        configuration.AddInMemoryCollection(document.RootElement.GetProperty("Values").EnumerateObject()
            .Select(property => new KeyValuePair<string, string?>(property.Name.Replace("__", ":"), property.Value.GetString())).ToArray());
    }
    var settings = configuration.AddEnvironmentVariables().Build();
    using var remote = new SftpRemoteFiles(SftpConnectionSettings.Load(settings));
    var input = SftpConnectionSettings.NormalizeRemotePath(settings["ApplicationSettings:UnprocessedFolder"] ?? "/Unprocessed");
    Console.WriteLine("CON container directories: " + string.Join(", ", remote.List("/").Where(file => file.IsDirectory).Select(file => file.Name)));
    if (!remote.Exists(input)) throw new DirectoryNotFoundException("The configured CON input directory does not exist.");
    Console.WriteLine($"CON input {input}: {remote.List(input).Count(file => file.IsRegularFile && file.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))} CSV files.");
    return;
}

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration(configuration => configuration.AddEnvironmentVariables())
    .ConfigureServices((context, services) =>
    {
        services.AddOptions<ApplicationSettings>()
            .Bind(context.Configuration.GetSection("ApplicationSettings"))
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.AzureSql),
                "ApplicationSettings__AzureSql is required.")
            .Validate(settings => new[]
            {
                settings.UnprocessedFolder, settings.ProcessedFolder, settings.ExceptionFolder,
                settings.DuplicateFolder, settings.ConfigurationUpdateFolder, settings.DiscardedFolder
            }.All(path => !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') &&
                !path.Contains('\\') && !path.Split('/').Any(segment => segment is "." or "..") && path.Trim('/').Length > 0),
                "All six ApplicationSettings folders must be absolute SFTP paths inside the CON container.")
            .ValidateOnStart();

        services.AddScoped<ParserService>();
        services.AddScoped<SQLService>();
        services.AddScoped<FileHelperService>();
        services.AddSingleton(_ => SftpConnectionSettings.Load(context.Configuration));
        services.AddScoped<IRemoteFiles, SftpRemoteFiles>();
        services.AddScoped<IConfigurationFiles, SftpConfigurationFiles>();
    })
    .Build();

await host.RunAsync();
