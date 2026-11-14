using System.Globalization;
using Azure;
using Azure.Communication.Email;
using Azure.Core;
using Azure.Core.Pipeline;

namespace FintechEtl.FunctionApp;

internal sealed record EtlEmailRequest(string From, string[] Recipients, string Subject, string Text, string Html);
internal sealed class EtlEmailEnvelope
{
    public int Version { get; set; } = 2;
    public EtlAlert Alert { get; set; }
    public EtlEmailRequest Request { get; set; }
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public DateTimeOffset? FirstSent { get; set; }
    public string State { get; set; } = "new";
    public DateTimeOffset NextAttempt { get; set; }
    public int Attempts { get; set; }
    public string LastError { get; set; }
}

internal interface IEtlEmailTransport
{
    Task<string> Start(EtlEmailEnvelope item, CancellationToken ct);
    Task<string> Poll(Guid operationId, CancellationToken ct);
}

internal sealed class AcsEtlEmailTransport(string connection, HttpPipelineTransport transport = null) : IEtlEmailTransport
{
    private EmailClient Client(out RetryAfterPolicy retry, EtlEmailEnvelope item = null)
    {
        var options = new EmailClientOptions();
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(12);
        if (transport != null) options.Transport = transport;
        retry = new RetryAfterPolicy();
        options.AddPolicy(retry, HttpPipelinePosition.PerRetry);
        if (item != null) options.AddPolicy(new RepeatableEmailPolicy(item.RequestId, item.FirstSent.Value), HttpPipelinePosition.PerRetry);
        return new EmailClient(connection, options);
    }
    public async Task<string> Start(EtlEmailEnvelope item, CancellationToken ct)
    {
        var r = item.Request;
        var message = new EmailMessage(r.From, new EmailRecipients(r.Recipients.Select(a => new EmailAddress(a)).ToList()),
            new EmailContent(r.Subject) { PlainText = r.Text, Html = r.Html });
        // Return on acceptance. Completion polling belongs to a later durable-outbox step.
        var client = Client(out var retry, item);
        try { return (await client.SendAsync(WaitUntil.Started, message, item.OperationId, ct)).Id; }
        catch (RequestFailedException ex) { retry.Attach(ex); throw; }
    }
    public async Task<string> Poll(Guid operationId, CancellationToken ct)
    {
        var client = Client(out var retry);
        var operation = new EmailSendOperation(operationId.ToString(), client);
        try { await operation.UpdateStatusAsync(ct); }
        catch (RequestFailedException ex) when (ex.Status == 404) { return "notfound"; }
        catch (RequestFailedException) when (operation.HasCompleted && !operation.HasValue) { return "failed"; }
        catch (RequestFailedException ex) { retry.Attach(ex); throw; }
        return operation.HasCompleted ? "succeeded" : "running";
    }
    private sealed class RetryAfterPolicy : HttpPipelineSynchronousPolicy
    {
        private double seconds;
        public override void OnReceivedResponse(HttpMessage message)
        {
            if (!message.Response.Headers.TryGetValue("Retry-After", out var value)) return;
            if (double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) seconds = Math.Max(0, number);
            else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) seconds = Math.Max(0, (at - DateTimeOffset.UtcNow).TotalSeconds);
        }
        internal void Attach(Exception ex) { if (seconds > 0) ex.Data["EtlRetryAfterSeconds"] = seconds; }
    }
    private sealed class RepeatableEmailPolicy(Guid id, DateTimeOffset firstSent) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            if (message.Request.Method != RequestMethod.Post) return;
            // Override the SDK's per-call values with the durable attempt identity.
            message.Request.Headers.SetValue("Repeatability-Request-ID", id.ToString());
            message.Request.Headers.SetValue("Repeatability-First-Sent", firstSent.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}

internal static class EtlEmailDelivery
{
    internal static bool Terminal(string state) => state is "failed" or "review";
    internal static TimeSpan Backoff(int attempts, int status) => TimeSpan.FromMinutes(
        Math.Min(60, Math.Max(status == 429 ? 5 : 1, Math.Pow(2, Math.Min(attempts - 1, 6)))));
    private static TimeSpan Delay(int attempts, Exception ex)
    {
        var delay = Backoff(attempts, Status(ex));
        if (ex.Data["EtlRetryAfterSeconds"] is double seconds && seconds > delay.TotalSeconds) delay = TimeSpan.FromSeconds(seconds);
        return delay;
    }

    // Every external POST has a durable intent before it. After acceptance only GET is allowed.
    internal static async Task Advance(EtlEmailEnvelope item, IEtlEmailTransport transport,
        Func<Task> save, DateTimeOffset now, CancellationToken ct)
    {
        if (item.State == "succeeded" || Terminal(item.State) || item.NextAttempt > now) return;
        if (item.State != "new")
        {
            try
            {
                var status = await transport.Poll(item.OperationId, ct);
                if (status == "succeeded") { item.State = "succeeded"; item.LastError = null; await save(); return; }
                if (status == "failed") { item.State = "failed"; item.LastError = "ACS reported a terminal failure; automatic resubmission disabled."; await save(); return; }
                if (status != "notfound") { item.State = "accepted"; item.NextAttempt = now.AddMinutes(1); await save(); return; }
                if (item.State == "accepted" || !item.FirstSent.HasValue || now - item.FirstSent.Value >= TimeSpan.FromMinutes(4))
                {
                    item.State = "review";
                    item.LastError = "Operation outcome unavailable; unsafe to resubmit. Event retained for operator review.";
                    await save(); return;
                }
                // Unknown POST outcome, still inside ACS's five-minute repeatability window.
                // Reuse identical body, operation ID, repeatability ID AND first-sent timestamp.
            }
            catch (Exception ex)
            {
                item.Attempts++; item.NextAttempt = now + Delay(item.Attempts, ex);
                item.LastError = Describe(ex); await save(); return;
            }
        }
        if (item.State == "new")
        {
            item.RequestId = Guid.NewGuid(); item.FirstSent = now;
        }
        item.State = "submitting"; item.Attempts++;
        item.NextAttempt = now.AddMinutes(1);
        await save(); // If persistence fails, DO NOT call the service.
        try
        {
            var operationId = await transport.Start(item, ct);
            item.OperationId = Guid.Parse(operationId);
            item.State = "accepted"; item.LastError = null;
        }
        catch (Exception ex)
        {
            int status = Status(ex);
            // A 429 on the initial POST is a rejected submission, unlike a 429 while polling.
            if (status == 429) { item.State = "new"; item.FirstSent = null; }
            else if (status == 412) item.State = "review";
            else if (status is >= 400 and < 500 && status is not (408 or 409)) item.State = "failed";
            item.NextAttempt = now + Delay(item.Attempts, ex);
            item.LastError = Describe(ex);
        }
        // Use the caller's independent storage budget even if the ACS request was cancelled.
        await save();
    }
    private static int Status(Exception ex) => ex is RequestFailedException r ? r.Status : 0;
    private static string Describe(Exception ex) => ex is RequestFailedException r
        ? $"{r.GetType().Name}: HTTP {r.Status}, {r.ErrorCode}" : ex.GetType().Name;
}
