using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using FintechAutoSiteConfiguration.FunctionApp;
using Microsoft.Extensions.Logging.Abstractions;

internal static class LiveEmailOutboxCheck
{
    public static async Task Run()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText("local.settings.json"));
        foreach (var key in new[] { "AzureWebJobsStorage", "ACS_EMAIL_CONNECTION_STRING", "EMAIL_RECIPIENTS", "EMAIL_FROM" })
            Environment.SetEnvironmentVariable(key, settings.RootElement.GetProperty("Values").GetProperty(key).GetString());
        Environment.SetEnvironmentVariable("EtlDryRun", "false");
        Environment.SetEnvironmentVariable("AutoSiteErrorEmailEnabled", "true");
        var scope = "FintechAutoSite-OutboxCheck-" + Guid.NewGuid().ToString("N");
        var prefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "/" +
            (Environment.GetEnvironmentVariable("WEBSITE_SLOT_NAME") ?? "Production")))).ToLowerInvariant() + "/events/";
        var container = new BlobContainerClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"), "fintech-autosite-alerts");
        int sends = 0;
        var immediate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> FakeSend(string[] recipients, string subject, string text, string html, CancellationToken ct)
        {
            Interlocked.Increment(ref sends);
            if (recipients.Length != 5 || !text.Contains("CON.csv") || !text.Contains("Error/warning events: 1")) throw new Exception("Individual email contents or recipients incorrect.");
            immediate.TrySetResult();
            return Task.FromResult("SIMULATED-no-email-sent");
        }
        async Task<int> Pending()
        {
            int count = 0;
            await foreach (var blob in container.GetBlobsAsync(prefix: prefix)) count++;
            return count;
        }
        try
        {
            using (var first = new AutoSiteErrorEmails(NullLogger.Instance, "outbox-first", FakeSend, scope))
            {
                first.Report("Partial", "007001", "first.tgz", "Synthetic check", "CON.csv validation example");
                // Proves delivery starts while invocation is still running, without Dispose or an interval tick.
                await immediate.Task.WaitAsync(TimeSpan.FromSeconds(20));
                first.Report("FileRetry", "007007", "second.zip", "Synthetic check", "CON.csv retry example");
            }
            if (sends != 2 || await Pending() != 0) throw new Exception("Errors were batched, throttled, or not individually acknowledged.");
            Task<string> FailSend(string[] r, string s, string t, string h, CancellationToken ct) => throw new InvalidOperationException("Simulated ACS outage");
            using (var failure = new AutoSiteErrorEmails(NullLogger.Instance, "outbox-failure", FailSend, scope))
                failure.Report("Partial", "007001", "retry.tgz", "Synthetic check", "CON.csv example");
            if (await Pending() != 1) throw new Exception("ACS failure discarded its individual persisted event.");
            using (var recovery = new AutoSiteErrorEmails(NullLogger.Instance, "outbox-recovery", FakeSend, scope)) { }
            if (sends != 3 || await Pending() != 0) throw new Exception("Later run did not recover the pending event.");
            using (var missing = new AutoSiteErrorEmails(NullLogger.Instance, "missing-site", FakeSend, scope))
            {
                for (int i = 0; i < 20; i++) missing.Report("MissingSiteMapping", "001057", "CON-001057-" + i, "Retained", "CON.csv missing mapping");
            }
            if (sends != 4) throw new Exception("Repeated files for one missing site sent multiple notifications.");
            // Also enqueue old notification blobs to prove deployment drains the existing backlog safely.
            for (int i = 0; i < 3; i++)
                await container.GetBlobClient(prefix + "old-missing-" + i + ".json").UploadAsync(BinaryData.FromObjectAsJson(
                    new EtlAlert("MissingSiteMapping", "001057", "old-CON.csv", "Retained", "CON.csv missing mapping", "old-run", DateTimeOffset.UtcNow,
                        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ApplicationSettings__AzureSql") ?? "").InitialCatalog)), overwrite: false);
            using (var nextRun = new AutoSiteErrorEmails(NullLogger.Instance, "missing-next-run", FakeSend, scope))
                nextRun.Report("MissingSiteMapping", "001057", "new-CON.csv", "Retained", "CON.csv missing mapping");
            if (sends != 4 || await Pending() != 0) throw new Exception("Suppression did not survive a new invocation or old backlog.");
            using (var secondSite = new AutoSiteErrorEmails(NullLogger.Instance, "different-site", FakeSend, scope))
                secondSite.Report("MissingSiteMapping", "001058", "CON.csv", "Retained", "CON.csv missing mapping");
            if (sends != 5) throw new Exception("Different missing site was incorrectly suppressed.");
            using (var missingFailure = new AutoSiteErrorEmails(NullLogger.Instance, "missing-send-failed", FailSend, scope))
                missingFailure.Report("MissingSiteMapping", "001059", "CON.csv", "Retained", "CON.csv missing mapping");
            using (var missingRecovery = new AutoSiteErrorEmails(NullLogger.Instance, "missing-send-recovery", FakeSend, scope)) { }
            if (sends != 6 || await Pending() != 0) throw new Exception("Failed first send was incorrectly marked as notified.");
            Console.WriteLine("PASS live individual Blob outbox: immediate send before invocation end, separate messages, persistence, ACS failure retention and next-run recovery. Email sends simulated; no SQL/SFTP data accessed.");
        }
        finally
        {
            await foreach (var blob in container.GetBlobsAsync(prefix: prefix[..prefix.IndexOf("/events/", StringComparison.Ordinal)] + "/")) await container.GetBlobClient(blob.Name).DeleteIfExistsAsync();
        }
    }
}
