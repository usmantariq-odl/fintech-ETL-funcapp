using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Azure;
using Azure.Communication.Email;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace FintechEtl.FunctionApp;

internal sealed record EtlAlert(string Category, string Site, string File, string Disposition, string Detail,
    string RunId, DateTimeOffset At, string Database = null, string SftpHost = null);
internal sealed class AlertDigest
{
    public long Total { get; set; }
    public Dictionary<string, long> Counts { get; set; } = new();
    public List<EtlAlert> Samples { get; set; } = new();
    public void Add(EtlAlert item)
    {
        Total++;
        Counts[item.Category] = Counts.GetValueOrDefault(item.Category) + 1;
        if (Samples.Count < 50) Samples.Add(item);
    }
}

// Separate persistence and ACS workers: an error wakes the sender immediately, without a digest interval.
internal sealed class EtlErrorEmails : IDisposable
{
    private readonly ILogger log;
    private readonly string runId;
    private readonly string connection;
    private readonly string storage;
    private readonly string app;
    private readonly string prefix;
    private readonly bool enabled;
    private readonly Channel<EtlAlert> incoming = Channel.CreateBounded<EtlAlert>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<string> ready = Channel.CreateBounded<string>(256);
    private readonly CancellationTokenSource stopRecovery = new();
    private readonly CancellationTokenSource stopSending = new();
    private readonly Task[] writers;
    private readonly Task[] senders;
    private readonly Task recovery;
    private readonly Func<string[], string, string, string, CancellationToken, Task<string>> sender;

    public EtlErrorEmails(ILogger log, string runId,
        Func<string[], string, string, string, CancellationToken, Task<string>> sendOverride = null, string stateScope = null)
    {
        this.log = log;
        this.runId = runId;
        app = stateScope ?? Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "FintechEtl-local";
        prefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(app + "/" +
            (Environment.GetEnvironmentVariable("WEBSITE_SLOT_NAME") ?? "Production")))).ToLowerInvariant() + "/events/";
        connection = Environment.GetEnvironmentVariable("ACS_EMAIL_CONNECTION_STRING");
        storage = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        sender = sendOverride ?? ((recipients, subject, text, html, ct) => Send(connection, recipients, subject, text, html, ct));
        enabled = !string.IsNullOrWhiteSpace(connection)
            && !string.Equals(Environment.GetEnvironmentVariable("EtlErrorEmailEnabled"), "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Environment.GetEnvironmentVariable("EtlDryRun"), "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled) log.LogWarning("ETL error email alerts are disabled or ACS_EMAIL_CONNECTION_STRING is missing (dry runs do not email)");
        writers = enabled ? Enumerable.Range(0, 2).Select(_ => Task.Run(Persist)).ToArray() : [];
        senders = enabled ? Enumerable.Range(0, 2).Select(_ => Task.Run(Deliver)).ToArray() : [];
        recovery = enabled ? Task.Run(Recover) : Task.CompletedTask;
    }

    public void Report(string category, string site, string file, string disposition, string detail)
    {
        if (!enabled) return;
        var item = new EtlAlert(category, site, file, disposition, Redact(detail), runId, DateTimeOffset.UtcNow,
            DatabaseName(), Environment.GetEnvironmentVariable("SftpHost"));
        // Bounded backpressure only during a very large error storm; never silently discard events.
        incoming.Writer.WriteAsync(item).AsTask().GetAwaiter().GetResult();
    }

    private BlobContainerClient Container()
    {
        if (string.IsNullOrWhiteSpace(storage)) throw new InvalidOperationException("AzureWebJobsStorage is required for the error-email outbox.");
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 1;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(8);
        return new BlobContainerClient(storage, "fintech-etl-alerts", options);
    }

    private async Task Persist()
    {
        await foreach (var item in incoming.Reader.ReadAllAsync())
        {
            var name = prefix + Guid.NewGuid().ToString("N") + ".json";
            bool saved = false;
            for (var attempt = 1; attempt <= 3 && !saved; attempt++)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var container = Container();
                    await container.CreateIfNotExistsAsync(cancellationToken: timeout.Token);
                    await container.GetBlobClient(name).UploadAsync(BinaryData.FromObjectAsJson(item), overwrite: false, cancellationToken: timeout.Token);
                    saved = true;
                }
                catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode == "BlobAlreadyExists") { saved = true; } // prior attempt may have committed before timeout
                catch (Exception ex)
                {
                    Failure("persist", ex);
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
            if (saved)
            {
                // If the ready queue is full, recovery will discover this durable event later.
                ready.Writer.TryWrite(name);
            }
            else log.LogError("Email event could not be persisted after 3 attempts: {Category}, {Site}, {File}, invocation {RunId}. Use Function logs for this event.", item.Category, item.Site, item.File, item.RunId);
        }
    }

    private async Task Recover()
    {
        bool initial = true;
        try
        {
            do
            {
                try
                {
                    // Even a short healthy invocation must discover previously failed sends before shutdown.
                    using var scanTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var scan = CancellationTokenSource.CreateLinkedTokenSource(scanTimeout.Token,
                        initial ? CancellationToken.None : stopRecovery.Token);
                    var container = Container();
                    await container.CreateIfNotExistsAsync(cancellationToken: scan.Token);
                    await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: scan.Token))
                        if (!ready.Writer.TryWrite(blob.Name)) break;
                }
                catch (OperationCanceledException) when (stopRecovery.IsCancellationRequested) { return; }
                catch (Exception ex) { Failure("recover", ex); }
                initial = false;
                await Task.Delay(TimeSpan.FromMinutes(1), stopRecovery.Token);
            } while (true);
        }
        catch (OperationCanceledException) when (stopRecovery.IsCancellationRequested) { }
    }

    private async Task Deliver()
    {
        try
        {
            await foreach (var name in ready.Reader.ReadAllAsync(stopSending.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopSending.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(40));
                var ct = timeout.Token;
                try
                {
                    var blob = Container().GetBlobClient(name);
                    var lease = blob.GetBlobLeaseClient();
                    try { await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: ct); }
                    catch (RequestFailedException ex) when (ex.Status is 404 or 409) { continue; }
                    try
                    {
                        var item = (await blob.DownloadContentAsync(ct)).Value.Content.ToObjectFromJson<EtlAlert>();
                        var message = new AlertDigest(); message.Add(item);
                        var body = Render(message, app, false);
                        var operation = await sender(Recipients(), body.Subject, body.Text, body.Html, ct);
                        await blob.DeleteAsync(conditions: new BlobRequestConditions { LeaseId = lease.LeaseId }, cancellationToken: ct);
                        log.LogInformation("Individual ETL error email accepted by ACS: operation {OperationId}, category {Category}, site {Site}, file {File}", operation, item.Category, item.Site, item.File);
                    }
                    finally
                    {
                        try { using var release = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await lease.ReleaseAsync(cancellationToken: release.Token); }
                        catch { /* Deleted events need no release; otherwise the finite lease expires. */ }
                    }
                }
                catch (Exception ex) { Failure("send (event retained for retry)", ex); }
            }
        }
        catch (OperationCanceledException) when (stopSending.IsCancellationRequested) { }
    }

    private void Failure(string stage, Exception ex)
        => log.LogError("ETL email {Stage} failed: {Type}, code {Code}. ETL processing continues; consult Function logs.",
            stage, ex.GetType().Name, ex is RequestFailedException request ? request.ErrorCode : "Unavailable");

    private static string DatabaseName()
    {
        try { return new System.Data.SqlClient.SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FintechSqlConnectionString") ?? "").InitialCatalog; }
        catch { return "Unavailable: invalid SQL connection setting"; }
    }

    internal static string[] Recipients() => (Environment.GetEnvironmentVariable("EMAIL_RECIPIENTS") ?? "")
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(address => new MailAddress(address).Address).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string Redact(string value)
    {
        value ??= "No additional exception details were supplied.";
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            var secret = entry.Value as string;
            if (secret?.Length >= 4 && Regex.IsMatch(name, "password|secret|connection.?string|AzureWebJobsStorage|token|accesskey", RegexOptions.IgnoreCase))
            {
                value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                // Also remove individual credentials when an exception quotes only part of a connection string.
                foreach (Match part in Regex.Matches(secret, @"(?:Password|Pwd|AccountKey|AccessKey)\s*=\s*([^;]+)", RegexOptions.IgnoreCase))
                    if (part.Groups[1].Value.Length >= 4) value = value.Replace(part.Groups[1].Value, "[REDACTED]", StringComparison.Ordinal);
            }
        }
        value = Regex.Replace(value, @"(?i)(password|pwd|accesskey|accountkey|sig|code)\s*=\s*[^;\s&]+", "$1=[REDACTED]");
        return value.Length > 6000 ? value[..6000] + "\n[truncated; see application logs]" : value;
    }

    internal static (string Subject, string Text, string Html) Render(AlertDigest digest, string app, bool test)
    {
        var title = test ? "TEST — Fintech ETL error notification" : "Fintech ETL — processing errors require review";
        var text = new StringBuilder().AppendLine(title).AppendLine($"Application: {app}")
            .AppendLine($"Generated (UTC): {DateTimeOffset.UtcNow:O}")
            .AppendLine($"Generated (Pakistan): {DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5)):O}")
            .AppendLine($"Error/warning events: {digest.Total}")
            .AppendLine("Counts: " + string.Join(", ", digest.Counts.Select(p => $"{p.Key}: {p.Value}")))
            .AppendLine($"Diagnostic samples: {digest.Samples.Count} (maximum 50; full details remain in Function logs).")
            .AppendLine("Each error event produces an individual email. A later failed retry of the same file produces another email.");
        if (test) text.AppendLine("This is a synthetic notification test. No production file or database processing failed for this test.");
        foreach (var item in digest.Samples)
            text.AppendLine().AppendLine($"Time (UTC): {item.At:O}").AppendLine($"Run / invocation: {item.RunId}")
                .AppendLine($"SQL database: {item.Database ?? "N/A"}").AppendLine($"SFTP host: {item.SftpHost ?? "N/A"}")
                .AppendLine($"Category: {item.Category}").AppendLine($"Site: {item.Site ?? "N/A"}")
                .AppendLine($"Input: {item.File ?? "N/A"}").AppendLine($"Disposition: {item.Disposition}")
                .AppendLine("Diagnostic details:").AppendLine(Redact(item.Detail));
        text.AppendLine().AppendLine("Investigation: correlate the invocation ID, UTC time, site and input path in Function / Application Insights logs. For SQL exceptions check connectivity, throttling, schema and mappings; for SFTP errors check endpoint access and host-key configuration. Verify committed database rows/checkpoints before any manual replay. This message does not assert a root cause or restore data.");
        var plain = text.ToString();
        var firstEvent = digest.Samples.FirstOrDefault();
        var subject = test ? $"[TEST] {app} — ETL error notification" : $"[ETL ALERT] {app} — {firstEvent?.Category} — site {firstEvent?.Site ?? "N/A"}";
        return (subject, plain, RenderHtml(digest, app, test));
    }

    private static string RenderHtml(AlertDigest digest, string app, bool test)
    {
        static string H(string value) => WebUtility.HtmlEncode(value ?? "N/A");
        static string Time(DateTimeOffset value) => value.ToString("dd MMM yyyy, HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;
        var html = new StringBuilder();
        html.Append("""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="dark"><style>
body{margin:0;padding:0}table{border-collapse:collapse}a{color:#91c9ef}pre{white-space:pre-wrap;word-break:break-word}
@media only screen and (max-width:640px){.pad{padding:22px 16px!important}.file-table,.file-table tbody,.file-table tr{display:block!important;width:100%!important}.file-table td[colspan]{display:block!important;width:auto!important}.file-table thead{display:none!important}.file-table .event-row>td{display:block!important;width:auto!important;border-bottom:0!important;padding:10px 12px!important}.file-table .event-row>td:first-child{padding-top:18px!important}.file-table .event-row>td:last-child{padding-bottom:18px!important}.main-title{font-size:24px!important}.stat{display:block!important;width:auto!important;padding:6px 0!important}}
</style></head><body style="background-color:#2d333a;color:#e1e6ef;font-family:Segoe UI,Arial,sans-serif;font-size:14px;line-height:1.6">
<table role="presentation" width="100%" style="background-color:#2d333a"><tr><td align="center" style="padding:24px 10px">
<table role="presentation" width="100%" style="max-width:1000px;table-layout:fixed;background-color:#252525;border:1px solid #46505d"><tr><td class="pad" style="padding:32px;overflow-wrap:anywhere;word-break:break-word">
""");
        html.Append("<h1 class='main-title' style='font-size:28px;line-height:1.25;font-weight:650;margin:0 0 14px;color:#e5eaf2'>FintechFuel ETL error notification</h1>")
            .Append("<p style='margin:0 0 12px;color:#bdc9da'>Review the processing event, affected input and diagnostic findings for <strong style='color:#e1e6ef'>")
            .Append(H(app)).Append("</strong>.</p><p style='margin:0;color:#aebdd0;font-size:12px'>Generated ")
            .Append(Time(now.ToOffset(TimeSpan.FromHours(5)))).Append(" PKT &nbsp;&middot;&nbsp; ").Append(Time(now)).Append(" UTC</p>");
        if (test) html.Append("<p style='background:#303c4b;border:1px solid #526a87;color:#d9e8f9;padding:12px 16px;margin:20px 0 0'><strong>Test notification</strong> &mdash; All events below are synthetic examples. No live file or database operation failed for this test.</p>");
        html.Append("<table role='presentation' width='100%' style='border-top:1px solid #46505d;border-bottom:1px solid #46505d;margin-top:24px'><tr>")
            .Append("<td class='stat' style='width:34%;padding:16px 0'><strong style='color:#ff9b93'>").Append(digest.Total.ToString("N0")).Append("</strong> error / warning events</td>")
            .Append("<td class='stat' style='width:33%;padding:16px 0'><strong>").Append(digest.Samples.Where(x => x.Site != null).Select(x => x.Site).Distinct().Count()).Append("</strong> sites in diagnostic sample</td>")
            .Append("<td class='stat' style='width:33%;padding:16px 0'><strong>").Append(digest.Samples.Count).Append("</strong> diagnostic details</td></tr></table>")
            .Append("<h2 style='font-size:18px;margin:28px 0 8px;color:#e1e6ef'>Processing summary</h2><p style='font-size:13px;color:#aebdd0;margin:0 0 16px'>Counts represent events, not unique files. A file retried more than once may contribute multiple events.</p>")
            .Append("<table width='100%' style='border:1px solid #46505d;font-size:13px'><thead><tr style='background:#30363d;color:#bdc9da'><th align='left' style='padding:12px 14px'>Event category</th><th align='right' style='padding:12px 14px'>Count</th></tr></thead><tbody>");
        foreach (var pair in digest.Counts.OrderByDescending(x => x.Value))
            html.Append("<tr><td style='padding:10px 14px;border-top:1px solid #46505d'>").Append(H(pair.Key)).Append("</td><td align='right' style='padding:10px 14px;border-top:1px solid #46505d;font-weight:600;color:#ff9b93'>").Append(pair.Value.ToString("N0")).Append("</td></tr>");
        html.Append("</tbody></table><h2 style='font-size:18px;margin:30px 0 8px;color:#e1e6ef'>Affected files &amp; diagnostic details</h2>")
            .Append("<p style='font-size:13px;color:#aebdd0;margin:0 0 16px'>An individual email is sent for each reported error event. Full logs remain in Function / Application Insights. Occurrence times are shown in Pakistan time, with UTC below.</p>")
            .Append("<table class='file-table' width='100%' style='table-layout:fixed;border:1px solid #46505d;font-size:13px'><thead><tr style='background:#30363d;color:#bdc9da'><th align='left' style='width:36%;padding:12px'>Site / input</th><th align='left' style='width:39%;padding:12px'>Result / action</th><th align='left' style='width:25%;padding:12px'>Occurrence</th></tr></thead><tbody>");
        foreach (var item in digest.Samples)
        {
            html.Append("<tr class='event-row'><td valign='top' style='padding:16px 12px;border-top:1px solid #46505d;overflow-wrap:anywhere;word-break:break-word'><strong style='color:#e1e6ef'>Site ")
                .Append(H(item.Site)).Append("</strong><br><span style='font-size:12px;color:#bdc9da'>").Append(H(item.File)).Append("</span></td>")
                .Append("<td valign='top' style='padding:16px 12px;border-top:1px solid #46505d'><strong style='color:#ff9b93'>").Append(H(item.Category)).Append("</strong><br>")
                .Append(H(item.Disposition)).Append("</td><td valign='top' style='padding:16px 12px;border-top:1px solid #46505d;color:#bdc9da'>")
                .Append(Time(item.At.ToOffset(TimeSpan.FromHours(5)))).Append(" PKT<br><span style='font-size:11px;color:#aebdd0'>").Append(Time(item.At)).Append(" UTC</span></td></tr>")
                .Append("<tr><td colspan='3' style='padding:0 12px 20px'><table role='presentation' width='100%' style='background:#20252b;border:1px solid #414a56'><tr><td style='padding:14px'>")
                .Append("<strong style='font-size:12px;color:#bdc9da'>Diagnostic finding</strong><pre style='margin:8px 0 14px;font-family:Consolas,Courier New,monospace;font-size:12px;line-height:1.6;color:#e1e6ef'>")
                .Append(H(Redact(item.Detail))).Append("</pre><p style='margin:0;font-size:11px;color:#aebdd0;overflow-wrap:anywhere;word-break:break-word'><strong>Invocation:</strong> ")
                .Append(H(item.RunId)).Append("<br><strong>SQL database:</strong> ").Append(H(item.Database)).Append(" &nbsp;&middot;&nbsp; <strong>SFTP:</strong> ").Append(H(item.SftpHost))
                .Append("</p></td></tr></table></td></tr>");
        }
        html.Append("""
</tbody></table><h2 style="font-size:18px;margin:30px 0 8px;color:#e1e6ef">Recommended investigation</h2>
<ol style="padding-left:20px;margin:0 0 24px;color:#bdc9da;font-size:13px"><li style="margin-bottom:8px">Find the invocation and input path in Function / Application Insights logs using the occurrence time above.</li><li style="margin-bottom:8px">Review the recorded exception: SQL connectivity, throttling, schema or mapping errors; SFTP connectivity and host-key failures; or file / CSV validation findings.</li><li>Check committed database rows, checkpoints and output folders before replaying a file. This notification reports observed errors; it does not confirm a root cause or restore data.</li></ol>
<p style="border-top:1px solid #46505d;padding-top:18px;margin:0;color:#aebdd0;font-size:11px">FintechFuel &middot; Automated ETL monitoring &middot; Do not reply<br>Credentials are redacted. Diagnostic details may be truncated; use the application logs for the complete exception.</p>
</td></tr></table></td></tr></table></body></html>
""");
        return html.ToString();
    }

    internal static async Task<string> Send(string connection, string[] recipients, string subject, string text, string html, CancellationToken ct)
    {
        if (recipients.Length == 0) throw new InvalidOperationException("EMAIL_RECIPIENTS is empty.");
        var options = new EmailClientOptions();
        options.Retry.MaxRetries = 0; // A timeout can follow acceptance; do not blindly resubmit within this send.
        var client = new EmailClient(connection, options);
        var message = new EmailMessage(Environment.GetEnvironmentVariable("EMAIL_FROM") ?? "fintechfuel@octopusdtl.com",
            new EmailRecipients(recipients.Select(address => new EmailAddress(address)).ToList()),
            new EmailContent(subject) { PlainText = text, Html = html });
        var operation = await client.SendAsync(WaitUntil.Completed, message, ct);
        if (operation.Value.Status != EmailSendStatus.Succeeded) throw new InvalidOperationException("ACS did not report Succeeded.");
        return operation.Id;
    }

    public void Dispose()
    {
        incoming.Writer.TryComplete();
        Task.WhenAll(writers).GetAwaiter().GetResult(); // Persist every queued event before returning.
        stopRecovery.Cancel();
        recovery.GetAwaiter().GetResult();
        ready.Writer.TryComplete();
        var deliveries = Task.WhenAll(senders);
        try { deliveries.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        {
            stopSending.Cancel();
            deliveries.GetAwaiter().GetResult();
            log.LogWarning("Email shutdown drain budget reached; remaining persisted events will be retried by the next invocation.");
        }
        stopRecovery.Dispose();
        stopSending.Dispose();
    }
}
