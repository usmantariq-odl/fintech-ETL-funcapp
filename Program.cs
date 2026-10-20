using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using FintechAutoSiteConfiguration.FunctionApp.Storage;

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
