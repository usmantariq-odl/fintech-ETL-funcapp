using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FintechEtl.FunctionApp.Alson;

public sealed class AlsonEtlTimer(ILogger<AlsonEtlTimer> log)
{
    internal sealed class Summary
    {
        public Guid BatchID {get;set;}
        public int Examined {get;set;}
        public int Sales {get;set;}
        public int TankReadings {get;set;}
        public int Archived {get;set;}
        public Dictionary<string,int> Outcomes {get;set;}=[];
        public void Count(string outcome)=>Outcomes[outcome]=Outcomes.GetValueOrDefault(outcome)+1;
    }
    [Function("AlsonEtlTimer")]
    public async Task Run([TimerTrigger("0 * * * * *",RunOnStartup=false)]TimerInfo timer,CancellationToken ct)
    {
        var alerts=new AlsonAlerts(log);var run=Guid.NewGuid().ToString("N");
        try
        {
            var settings=AlsonSettings.Load();
            if(!settings.Enabled){log.LogInformation("Alson ETL is disabled.");return;}
            await Scan(settings,alerts,ct);
        }
        catch(Exception ex) when(!ct.IsCancellationRequested){alerts.Add("invocation",ex);throw;}
        finally {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));await alerts.Flush(run,timeout.Token);}
    }
    internal async Task<Summary> Scan(AlsonSettings settings,AlsonAlerts alerts,CancellationToken ct,
        int maxSales=int.MaxValue,int maxAtgFrames=int.MaxValue,string manifestPath=null,
        int salesPerMaster=int.MaxValue,int atgFramesPerMaster=int.MaxValue,TimeSpan? runBudget=null)
    {
        var store=new AlsonStore(settings);store.VerifySchema();
        var summary=new Summary{BatchID=Guid.NewGuid()};
        var terminal=store.TerminalSources();
        if(!settings.DryRun)store.BeginBatch(summary.BatchID);
        var options=new BlobClientOptions();options.Retry.MaxRetries=2;options.Retry.NetworkTimeout=TimeSpan.FromSeconds(20);
        var container=new BlobContainerClient(settings.Blob,settings.Container,options);
        var archive=new AlsonArchive(new AlsonBlobArchiveStorage(container));
        var evidence=new List<object>();int atgFrames=0;var stopwatch=System.Diagnostics.Stopwatch.StartNew();
        var dryHashes=new HashSet<string>(StringComparer.Ordinal);
        var masterSales=new Dictionary<string,int>(StringComparer.Ordinal);
        var masterAtg=new Dictionary<string,int>(StringComparer.Ordinal);
        await foreach(var item in container.GetBlobsAsync(prefix:settings.Prefix,cancellationToken:ct))
        {
            if(stopwatch.Elapsed>(runBudget??TimeSpan.FromMinutes(4)))break;
            if(!item.Name.EndsWith(".json",StringComparison.OrdinalIgnoreCase)||item.Properties.ContentLength>1024*1024)continue;
            // Current gateway names identify business categories. Skip connection/control
            // telemetry cheaply; validate the actual command after downloading eligible data.
            var leaf=item.Name[settings.Prefix.Length..];
            if(!leaf.Contains("-TRANSACTION-",StringComparison.Ordinal)&&!leaf.Contains("-ATG-DATA-",StringComparison.Ordinal)&&!leaf.StartsWith("PY-",StringComparison.Ordinal))continue;
            var source=Convert.ToHexString(AlsonStore.SourceKey(settings.Container,item.Name));var etag=item.Properties.ETag?.ToString()??"";
            var fileMaster=item.Name[settings.Prefix.Length..].Split('-')[0];
            if(terminal.TryGetValue(source,out var prior))
            {
                if(!settings.DryRun&&prior.ArchiveEligible&&settings.Masters.Contains(prior.Master))
                {
                    if(summary.Examined>=settings.MaxFiles)break;
                    await archive.Move(item.Name,prior.EnvelopeHash,ct);
                    summary.Examined++;summary.Archived++;
                    evidence.Add(new{Blob=item.Name,Destination=AlsonArchive.Destination(item.Name),Outcome="archive_resumed"});
                    continue;
                }
                if(prior.ETag==etag)continue;
            }
            if(summary.Sales>=maxSales&&item.Name.Contains("-TRANSACTION-",StringComparison.Ordinal))continue;
            if(atgFrames>=maxAtgFrames&&item.Name.Contains("-ATG-DATA-",StringComparison.Ordinal))continue;
            if(masterSales.GetValueOrDefault(fileMaster)>=salesPerMaster&&item.Name.Contains("-TRANSACTION-",StringComparison.Ordinal))continue;
            if(masterAtg.GetValueOrDefault(fileMaster)>=atgFramesPerMaster&&item.Name.Contains("-ATG-DATA-",StringComparison.Ordinal))continue;
            if(summary.Examined>=settings.MaxFiles)break;
            byte[] body=null;
            try
            {
                body=(await container.GetBlobClient(item.Name).DownloadContentAsync(ct)).Value.Content.ToArray();
                summary.Examined++;
                var frame=AlsonParser.Parse(body);
                if(frame.Command=="00"&&summary.Sales>=maxSales||frame.Command=="20"&&atgFrames>=maxAtgFrames)continue;
                AlsonStore.Result result;
                if(settings.DryRun)
                {
                    // Read-only mapping preview: no inserts, target identity consumption or emails.
                    result=store.Validate(item.Name,etag,body,frame);
                    if(result.Outcome=="imported"&&!dryHashes.Add(Convert.ToHexString(frame.Hash)))result=new("duplicate",0);
                }
                else result=store.Process(item.Name,etag,body,frame,summary.BatchID);
                summary.Count(result.Outcome);
                if(result.Outcome=="imported")
                {
                    if(frame.Command=="00"){summary.Sales+=result.Rows;masterSales[frame.Master]=masterSales.GetValueOrDefault(frame.Master)+result.Rows;}
                    else {summary.TankReadings+=result.Rows;atgFrames++;masterAtg[frame.Master]=masterAtg.GetValueOrDefault(frame.Master)+1;}
                }
                if(!settings.DryRun&&result.Outcome is "imported" or "duplicate")
                {
                    await archive.Move(item.Name,SHA256.HashData(body),ct);
                    summary.Archived++;
                }
                evidence.Add(new{Blob=item.Name,Command=frame.Command,Master=frame.Master,Hash=Convert.ToHexString(frame.Hash),result.Outcome,result.Rows,result.Detail});
                if(summary.Examined%25==0)log.LogInformation("Alson progress: {Examined} files, {Sales} sales, {Tanks} tank readings",summary.Examined,summary.Sales,summary.TankReadings);
            }
            catch(Exception ex) when(ex is FormatException or JsonException or DecoderFallbackException or AlsonPendingException or OverflowException)
            {
                string outcome=ex is AlsonPendingException?"pending_mapping":"invalid";
                summary.Count(outcome);alerts.Add(item.Name,ex);
                evidence.Add(new{Blob=item.Name,Outcome=outcome,Error=EtlErrorEmails.Redact(ex.Message)});
                if(!settings.DryRun&&body!=null)store.RecordFailure(item.Name,etag,body,outcome,EtlErrorEmails.Redact(ex.Message),summary.BatchID);
            }
            if(summary.Sales>=maxSales&&atgFrames>=maxAtgFrames)break;
        }
        log.LogInformation("Alson {Mode} batch {Batch}: examined {Examined}, sales {Sales}, tank rows {Tanks}, archived {Archived}; {Outcomes}",
            settings.DryRun?"DRY RUN":"IMPORT",summary.BatchID,summary.Examined,summary.Sales,summary.TankReadings,summary.Archived,JsonSerializer.Serialize(summary.Outcomes));
        if(manifestPath!=null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
            await File.WriteAllTextAsync(manifestPath,JsonSerializer.Serialize(new{Summary=summary,Files=evidence},new JsonSerializerOptions{WriteIndented=true}),ct);
        }
        return summary;
    }
}
