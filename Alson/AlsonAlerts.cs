using System.Net;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace FintechEtl.FunctionApp.Alson;

// Alson has its own enable switch, recipients, staging label and durable outbox.
// Never changes Dover settings or sends email from a dry/local validation run.
internal sealed class AlsonAlerts(ILogger log)
{
    private readonly List<string> errors=[];
    private readonly Dictionary<string,string> unknownMasters=new(StringComparer.Ordinal);
    internal void UnknownMaster(string master,string source)=>unknownMasters.TryAdd(master,source);
    internal static string MasterNoticeKey(string master)=>"staging/new-master/"+
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(master)))+".json";
    internal static (string Subject,string Text,string Html) RenderMaster(string master,string source)
    {
        string text="New unconfigured Alson master found: "+master+
            "\nDatabase: FintechStagDB_NewArchitecture\nExample: "+source+
            "\nData is not imported. Original files remain in Alson/inbox. Configure the site/master and mappings, allow the master, and review excluded audits before replay."+
            "\nThis notification is recorded per master and suppressed on subsequent runs.";
        return ("[ETL NOTICE] Alson STAGING - new unconfigured master found",text,
            "<h2>New Alson master</h2><pre>"+WebUtility.HtmlEncode(text)+"</pre>");
    }
    internal void Add(string source,Exception error)
    {
        log.LogError("Alson {Source}: {Type}: {Message}",source,error.GetType().Name,EtlErrorEmails.Redact(error.Message));
        if(errors.Count<30)errors.Add(source+": "+EtlErrorEmails.Redact(error.ToString()));
    }
    internal static (string Subject,string Text,string Html) Render(IEnumerable<string> details,string run)
    {
        var text="Alson ETL STAGING processing errors\nDatabase: FintechStagDB_NewArchitecture\nRun: "+run+
            "\nOriginal inbox files are retained. Check audit outcomes and committed rows before replay.\n\n"+
            string.Join("\n\n",details.Select(EtlErrorEmails.Redact));
        return ("[ETL ALERT] Alson STAGING - processing errors",text,"<h2>Alson ETL - Staging</h2><pre style='white-space:pre-wrap'>"+WebUtility.HtmlEncode(text)+"</pre>");
    }
    internal async Task Flush(string run,CancellationToken ct)
    {
        if(!string.Equals(Environment.GetEnvironmentVariable("AlsonEtlEnabled"),"true",StringComparison.OrdinalIgnoreCase)||
            !string.Equals(Environment.GetEnvironmentVariable("AlsonErrorEmailEnabled"),"true",StringComparison.OrdinalIgnoreCase)||
            !string.Equals(Environment.GetEnvironmentVariable("AlsonEtlDryRun"),"false",StringComparison.OrdinalIgnoreCase))return;
        try
        {
            var storage=Environment.GetEnvironmentVariable("AlsonBlobConnectionString");
            var acs=Environment.GetEnvironmentVariable("ACS_EMAIL_CONNECTION_STRING");
            var recipients=(Environment.GetEnvironmentVariable("AlsonEmailRecipients")??"").Split([',',';'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            if(string.IsNullOrWhiteSpace(storage)||string.IsNullOrWhiteSpace(acs)||recipients.Length==0)
                throw new InvalidOperationException("Alson email settings incomplete; inspect Function logs.");
            var options=new BlobClientOptions();options.Retry.MaxRetries=1;options.Retry.NetworkTimeout=TimeSpan.FromSeconds(10);
            var container=new BlobContainerClient(storage,"fintech-alson-alerts",options);
            await container.CreateIfNotExistsAsync(cancellationToken:ct);
            foreach(var (master,source) in unknownMasters)
            {
                var notice=container.GetBlobClient(MasterNoticeKey(master));
                try {await notice.UploadAsync(BinaryData.FromObjectAsJson(new Alert(run,[],master,source)),overwrite:false,cancellationToken:ct);}
                catch(RequestFailedException ex) when(ex.Status==409) { /* Persistent per-master notification already queued or sent. */ }
            }
            if(errors.Count>0)
            {
                var now=DateTime.UtcNow;var bucket=new DateTime(now.Year,now.Month,now.Day,now.Hour,(now.Minute/15)*15,0,DateTimeKind.Utc);
                var blob=container.GetBlobClient("staging/"+bucket.ToString("yyyyMMddHHmm")+".json");
                try { await blob.UploadAsync(BinaryData.FromObjectAsJson(new Alert(run,errors.ToArray())),overwrite:false,cancellationToken:ct); }
                catch(RequestFailedException ex) when(ex.Status==409) { /* One digest per 15-minute bucket; details remain in SQL/logs. */ }
            }
            int sent=0;
            await foreach(var item in container.GetBlobsAsync(BlobTraits.Metadata,prefix:"staging/",cancellationToken:ct))
            {
                if(item.Metadata.TryGetValue("sent",out var status)&&status=="true")continue;
                if(sent++>=20)break;
                var blob=container.GetBlobClient(item.Name);var lease=blob.GetBlobLeaseClient();string leaseId;
                try {leaseId=(await lease.AcquireAsync(TimeSpan.FromSeconds(60),cancellationToken:ct)).Value.LeaseId;}
                catch(RequestFailedException ex) when(ex.Status is 404 or 409){continue;}
                try
                {
                    var props=(await blob.GetPropertiesAsync(cancellationToken:ct)).Value;
                    if(props.Metadata.TryGetValue("sent",out status)&&status=="true")continue;
                    var alert=(await blob.DownloadContentAsync(ct)).Value.Content.ToObjectFromJson<Alert>();
                    var message=alert.Master==null?Render(alert.Errors,alert.Run):RenderMaster(alert.Master,alert.Source);
                    await EtlErrorEmails.Send(acs,recipients,message.Subject,message.Text,message.Html,ct);
                    await blob.SetMetadataAsync(new Dictionary<string,string>{{"sent","true"}},new BlobRequestConditions{LeaseId=leaseId},ct);
                }
                finally {try{await lease.ReleaseAsync(cancellationToken:ct);}catch(RequestFailedException){}}
            }
        }
        catch(Exception ex) when(!ct.IsCancellationRequested)
        {log.LogError("Alson error-email outbox failure: {Type}: {Message}. Pending persisted alerts remain retryable.",ex.GetType().Name,EtlErrorEmails.Redact(ex.Message));}
    }
    private sealed record Alert(string Run,string[] Errors,string Master=null,string Source=null);
}
