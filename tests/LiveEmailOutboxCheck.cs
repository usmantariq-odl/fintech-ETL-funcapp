using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using FintechEtl.FunctionApp;
using Microsoft.Extensions.Logging.Abstractions;

internal static class LiveEmailOutboxCheck
{
    public static async Task Run()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText("local.settings.json"));
        foreach (var key in new[] { "AzureWebJobsStorage", "ACS_EMAIL_CONNECTION_STRING", "EMAIL_RECIPIENTS", "EMAIL_FROM" })
            Environment.SetEnvironmentVariable(key, settings.RootElement.GetProperty("Values").GetProperty(key).GetString());
        Environment.SetEnvironmentVariable("EtlDryRun", "false");
        Environment.SetEnvironmentVariable("EtlErrorEmailEnabled", "true");
        var scope = "FintechEtl-OutboxCheck-" + Guid.NewGuid().ToString("N");
        var prefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "/" +
            (Environment.GetEnvironmentVariable("WEBSITE_SLOT_NAME") ?? "Production")))).ToLowerInvariant() + "/events/";
        var container = new BlobContainerClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"), "fintech-etl-alerts");
        int sends = 0;
        var immediate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> FakeSend(string[] recipients, string subject, string text, string html, CancellationToken ct)
        {
            Interlocked.Increment(ref sends);
            if (recipients.Length != 5 || !text.Contains("PSL.csv") || !text.Contains("Error/warning events: 1")) throw new Exception("Individual email contents or recipients incorrect.");
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
            using (var first = new EtlErrorEmails(NullLogger.Instance, "outbox-first", FakeSend, scope))
            {
                first.Report("Partial", "007001", "first.tgz", "Synthetic check", "PSL.csv validation example");
                // Proves delivery starts while invocation is still running, without Dispose or an interval tick.
                await immediate.Task.WaitAsync(TimeSpan.FromSeconds(20));
                first.Report("FileRetry", "007007", "second.zip", "Synthetic check", "PSL.csv retry example");
            }
            if (sends != 2 || await Pending() != 0) throw new Exception("Errors were batched, throttled, or not individually acknowledged.");
            Task<string> FailSend(string[] r, string s, string t, string h, CancellationToken ct) => throw new InvalidOperationException("Simulated ACS outage");
            using (var failure = new EtlErrorEmails(NullLogger.Instance, "outbox-failure", FailSend, scope))
                failure.Report("Partial", "007001", "retry.tgz", "Synthetic check", "PSL.csv example");
            if (await Pending() != 1) throw new Exception("ACS failure discarded its individual persisted event.");
            using (var recovery = new EtlErrorEmails(NullLogger.Instance, "outbox-recovery", FakeSend, scope)) { }
            if (sends != 3 || await Pending() != 0) throw new Exception("Later run did not recover the pending event.");
            Console.WriteLine("PASS live individual Blob outbox: immediate send before invocation end, separate messages, persistence, ACS failure retention and next-run recovery. Email sends simulated; no SQL/SFTP data accessed.");
        }
        finally
        {
            await foreach (var blob in container.GetBlobsAsync(prefix: prefix)) await container.GetBlobClient(blob.Name).DeleteIfExistsAsync();
        }
    }
}
