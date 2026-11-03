using System.Threading.Channels;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FintechAutoSiteConfiguration.FunctionApp.Storage;

namespace FintechAutoSiteConfiguration.FunctionApp;

public sealed class AutoSiteConfigurationTimer(
    IServiceScopeFactory scopeFactory,
    ILogger<AutoSiteConfigurationTimer> logger)
{
    [Function("AutoSiteConfigurationTimer")]
    public async Task RunAsync(
        [TimerTrigger("0 */5 * * * *", RunOnStartup = false, UseMonitor = true)] TimerInfo timer,
        CancellationToken cancellationToken, FunctionContext? context = null)
    {
        using var alerts = new AutoSiteErrorEmails(logger, context?.InvocationId ?? Guid.NewGuid().ToString("N"));
        try { await RunCoreAsync(timer, cancellationToken, alerts); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "AutoSite invocation failed; inspect CON inputs, outputs and committed configuration before retry");
            alerts.Report(ex is ConfigurationStorageException ? "StorageFailure" : "InvocationFailure",
                ex.Data["AutoSiteSite"] as string, ex.Data["AutoSiteFile"] as string,
                "Scan aborted. Some configuration changes or file moves may already have completed. Verify actual database and storage state before replay.", ex.ToString());
            throw;
        }
    }

    private async Task RunCoreAsync(TimerInfo timer, CancellationToken cancellationToken, AutoSiteErrorEmails alerts)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timer.IsPastDue)
            logger.LogWarning("Auto site configuration timer is running late.");

        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IOptions<ApplicationSettings>>().Value;
        logger.LogInformation("Auto-site configuration SQL database: {Database}",
            new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.AzureSql).InitialCatalog);
        var files = scope.ServiceProvider.GetRequiredService<IConfigurationFiles>();
        if (!files.DirectoryExists(settings.UnprocessedFolder))
            throw new DirectoryNotFoundException(
                $"The configured CON input folder is unavailable: {settings.UnprocessedFolder}. Check the CON SFTP user and container permissions.");

        logger.LogInformation("Auto site configuration scan started at {StartedUtc}.", DateTimeOffset.UtcNow);
        var channel = Channel.CreateUnbounded<string>();
        var parser = scope.ServiceProvider.GetRequiredService<ParserService>();
        await parser.ProcessFilesAsync(channel, alerts.Report);
        logger.LogInformation("Auto site configuration scan completed at {CompletedUtc}.", DateTimeOffset.UtcNow);
    }
}
