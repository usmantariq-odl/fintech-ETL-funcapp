using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FintechEtl.FunctionApp.Alson;

public sealed partial class AlsonEtlTimer(ILogger<AlsonEtlTimer> log)
{
    internal sealed class Summary
    {
        public Guid BatchID {get;set;}
        public int Examined {get;set;}
        public int Sales {get;set;}
        public int TankReadings {get;set;}
        public int Archived {get;set;}
        public int DiagnosticEvents {get;set;}
        public int Listed {get;set;}
        public int Skipped {get;set;}
        public string StopReason {get;set;}="prefix_exhausted";
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
            await ScanWorkers(settings,alerts,run,ct);
        }
        catch(Exception ex) when(!ct.IsCancellationRequested){alerts.Add("invocation",ex);throw;}
        finally {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));await alerts.Flush(run,timeout.Token);}
    }
    internal async Task<Summary> Scan(AlsonSettings settings,AlsonAlerts alerts,CancellationToken ct,
        int maxSales=int.MaxValue,int maxAtgFrames=int.MaxValue,string manifestPath=null,
        int salesPerMaster=int.MaxValue,int atgFramesPerMaster=int.MaxValue,TimeSpan? runBudget=null,bool diagnosticsOnly=false,IReadOnlySet<string> selectedInputs=null,string inputPrefix=null)
    {
        var store=new AlsonStore(settings);store.VerifySchema();
        var summary=new Summary{BatchID=Guid.NewGuid()};
        using var batchScope=log.BeginScope(new Dictionary<string,object>{{"AlsonBatchId",summary.BatchID}});
        log.LogInformation("Alson batch {Batch} starting: mode={Mode}, database={Database}, prefix={Prefix}, file limit={Limit}",
            summary.BatchID,settings.DryRun?"DRY RUN":"IMPORT",settings.ExpectedDatabase,inputPrefix??settings.Prefix,settings.MaxFiles);
        var terminal=store.TerminalSources();
        // Rediscover excluded audit rows too: they may predate the notification feature.
        foreach(var prior in terminal.Values)
            if(prior.Outcome=="excluded"&&!string.IsNullOrWhiteSpace(prior.Master)&&!settings.Masters.Contains(prior.Master))
                alerts.UnknownMaster(prior.Master,"Previously excluded inbox file");
        var masterConfiguration=new Dictionary<string,bool>(StringComparer.Ordinal);
        if(!settings.DryRun)store.BeginBatch(summary.BatchID);
        var options=new BlobClientOptions();options.Retry.MaxRetries=2;options.Retry.NetworkTimeout=TimeSpan.FromSeconds(20);
        var container=new BlobContainerClient(settings.Blob,settings.Container,options);
        var archive=new AlsonArchive(new AlsonBlobArchiveStorage(container));
        var evidence=new List<object>();int atgFrames=0;var stopwatch=System.Diagnostics.Stopwatch.StartNew();
        var dryHashes=new HashSet<string>(StringComparer.Ordinal);
        var masterSales=new Dictionary<string,int>(StringComparer.Ordinal);
        var masterAtg=new Dictionary<string,int>(StringComparer.Ordinal);
        int diagnosticCandidates=0;int diagnosticBudget=diagnosticsOnly?settings.MaxFiles:Math.Max(1,settings.MaxFiles/2);
        async IAsyncEnumerable<BlobItem> Inputs()
        {
            if(selectedInputs==null)
            {
                await foreach(var blob in container.GetBlobsAsync(prefix:inputPrefix??settings.Prefix,cancellationToken:ct))yield return blob;
            }
            else foreach(var name in selectedInputs.Order(StringComparer.Ordinal))
                await foreach(var blob in container.GetBlobsAsync(prefix:name,cancellationToken:ct))
                    if(blob.Name==name)yield return blob;
        }
        await foreach(var item in Inputs())
        {
            summary.Listed++;
            if(selectedInputs!=null&&!selectedInputs.Contains(item.Name))continue;
            if(stopwatch.Elapsed>(runBudget??TimeSpan.FromMinutes(4))){summary.StopReason="time_budget";break;}
            if(!item.Name.EndsWith(".json",StringComparison.OrdinalIgnoreCase)||item.Properties.ContentLength>1024*1024)continue;
            // Reserve part of each scan for financial data, so heartbeat backlogs cannot
            // consume the entire batch. Verify actual command after reading eligible JSON.
            var leaf=item.Name[settings.Prefix.Length..];
            bool diagnosticName=new[]{"-AUTO-MANUAL-","-DSP-CONN-","-ATG-CONN-","-PROBE-"}.Any(x=>leaf.Contains(x,StringComparison.Ordinal));
            if(diagnosticsOnly&&!diagnosticName&&!leaf.StartsWith("PY-",StringComparison.Ordinal))continue;
            if(!diagnosticName&&!leaf.Contains("-TRANSACTION-",StringComparison.Ordinal)&&!leaf.Contains("-ATG-DATA-",StringComparison.Ordinal)&&!leaf.StartsWith("PY-",StringComparison.Ordinal))continue;
            if(diagnosticName&&diagnosticCandidates>=diagnosticBudget)continue;
            var source=Convert.ToHexString(AlsonStore.SourceKey(settings.Container,item.Name));var etag=item.Properties.ETag?.ToString()??"";
            var fileMaster=item.Name[settings.Prefix.Length..].Split('-')[0];
            if(terminal.TryGetValue(source,out var prior))
            {
                if(!settings.DryRun&&prior.ArchiveEligible&&settings.Masters.Contains(prior.Master))
                {
                    if(summary.Examined>=settings.MaxFiles){summary.StopReason="file_limit";break;}
                    await archive.Move(item.Name,prior.EnvelopeHash,ct);
                    summary.Examined++;summary.Archived++;
                    if(diagnosticName)diagnosticCandidates++;
                    evidence.Add(new{Blob=item.Name,Destination=AlsonArchive.Destination(item.Name),Outcome="archive_resumed"});
                    log.LogInformation("Alson batch {Batch}: resumed archive for {File} to {Destination}",summary.BatchID,item.Name,AlsonArchive.Destination(item.Name));
                    continue;
                }
                if(prior.ETag==etag){summary.Skipped++;continue;}
            }
            if(summary.Sales>=maxSales&&item.Name.Contains("-TRANSACTION-",StringComparison.Ordinal))continue;
            if(atgFrames>=maxAtgFrames&&item.Name.Contains("-ATG-DATA-",StringComparison.Ordinal))continue;
            if(masterSales.GetValueOrDefault(fileMaster)>=salesPerMaster&&item.Name.Contains("-TRANSACTION-",StringComparison.Ordinal))continue;
            if(masterAtg.GetValueOrDefault(fileMaster)>=atgFramesPerMaster&&item.Name.Contains("-ATG-DATA-",StringComparison.Ordinal))continue;
            if(summary.Examined>=settings.MaxFiles){summary.StopReason="file_limit";break;}
            byte[] body=null;
            try
            {
                try{body=(await container.GetBlobClient(item.Name).DownloadContentAsync(ct)).Value.Content.ToArray();}
                catch(RequestFailedException ex) when(ex.Status==404)
                {
                    // A concurrent live importer/archive may remove a listed source
                    // before download. Nothing was inserted by this worker.
                    summary.Skipped++;
                    log.LogDebug("Alson batch {Batch}: source disappeared after listing; skipping {File}",summary.BatchID,item.Name);
                    continue;
                }
                summary.Examined++;
                var frame=AlsonParser.Parse(body);
                if(!settings.Masters.Contains(frame.Master))
                {
                    if(!masterConfiguration.ContainsKey("unknown/"+frame.Master))
                    {
                        masterConfiguration["unknown/"+frame.Master]=false;
                        log.LogWarning("Alson batch {Batch}: master {Master} is not allowed; data stays in inbox. Persistent discovery notice will suppress repeat emails. Example {File}",summary.BatchID,frame.Master,item.Name);
                    }
                    alerts.UnknownMaster(frame.Master,item.Name);
                }
                else
                {
                    string type=frame.Command is "00" or "31" or "70"?"DSP":"ATG";
                    string configKey=frame.Master+"/"+type;
                    if(!masterConfiguration.TryGetValue(configKey,out var configured))
                        masterConfiguration[configKey]=configured=store.MasterConfigured(frame.Master,type);
                    if(!configured)
                    {
                        alerts.UnknownMaster(frame.Master,item.Name);
                        summary.Count("pending_mapping");
                        log.LogWarning("Alson batch {Batch}: pending_mapping master={Master}, file={File}; active site/master mapping missing or ambiguous; original retained",summary.BatchID,frame.Master,item.Name);
                        if(!settings.DryRun)store.RecordFailure(item.Name,etag,body,"pending_mapping","Master/site configuration missing or ambiguous.",summary.BatchID);
                        continue;
                    }
                }
                if(diagnosticsOnly&&frame.Diagnostic==null)continue;
                if(frame.Diagnostic!=null)diagnosticCandidates++;
                if(frame.Command=="00"&&summary.Sales>=maxSales||frame.Command=="20"&&atgFrames>=maxAtgFrames)continue;
                AlsonStore.Result result;
                if(settings.DryRun)
                {
                    // Read-only mapping preview: no inserts, target identity consumption or emails.
                    result=store.Validate(item.Name,etag,body,frame);
                    var identity=frame.Diagnostic==null?frame.Hash:AlsonStore.DiagnosticReceiptKey(frame,AlsonStore.SourceKey(settings.Container,item.Name));
                    if(result.Outcome=="imported"&&!dryHashes.Add(Convert.ToHexString(identity)))result=new("duplicate",0);
                }
                else result=store.Process(item.Name,etag,body,frame,summary.BatchID);
                summary.Count(result.Outcome);
                log.LogDebug("Alson batch {Batch}: master={Master}, command={Command}, file={File}, outcome={Outcome}, inserted rows={Rows}",summary.BatchID,frame.Master,frame.Command,item.Name,result.Outcome,result.Rows);
                if(result.Outcome=="imported")
                {
                    if(frame.Command=="00"){summary.Sales+=result.Rows;masterSales[frame.Master]=masterSales.GetValueOrDefault(frame.Master)+result.Rows;}
                    else if(frame.Command=="20"){summary.TankReadings+=result.Rows;atgFrames++;masterAtg[frame.Master]=masterAtg.GetValueOrDefault(frame.Master)+1;}
                    else summary.DiagnosticEvents+=result.Rows;
                }
                if(!settings.DryRun&&result.Outcome is "imported" or "duplicate")
                {
                    await archive.Move(item.Name,SHA256.HashData(body),ct);
                    summary.Archived++;
                }
                evidence.Add(new{Blob=item.Name,Command=frame.Command,Master=frame.Master,Hash=Convert.ToHexString(frame.Hash),result.Outcome,result.Rows,result.Detail});
                if(summary.Examined%10==0)log.LogInformation("Alson batch {Batch} progress: files={Examined}/{Limit}, transactions={Sales}, tank readings={Tanks}, diagnostic rows={Diagnostics}, archived={Archived}, outcomes={Outcomes}, elapsed={ElapsedSeconds:F1}s",
                    summary.BatchID,summary.Examined,settings.MaxFiles,summary.Sales,summary.TankReadings,summary.DiagnosticEvents,summary.Archived,JsonSerializer.Serialize(summary.Outcomes),stopwatch.Elapsed.TotalSeconds);
            }
            catch(Exception ex) when(ex is FormatException or JsonException or DecoderFallbackException or AlsonPendingException or OverflowException)
            {
                string outcome=ex is AlsonPendingException?"pending_mapping":"invalid";
                summary.Count(outcome);alerts.Add(item.Name,ex);
                log.LogWarning("Alson batch {Batch}: file={File}, outcome={Outcome}; original retained. {Reason}",summary.BatchID,item.Name,outcome,EtlErrorEmails.Redact(ex.Message));
                evidence.Add(new{Blob=item.Name,Outcome=outcome,Error=EtlErrorEmails.Redact(ex.Message)});
                if(!settings.DryRun&&body!=null)store.RecordFailure(item.Name,etag,body,outcome,EtlErrorEmails.Redact(ex.Message),summary.BatchID);
            }
            if(summary.Sales>=maxSales&&atgFrames>=maxAtgFrames){summary.StopReason="local_test_limit";break;}
        }
        log.LogInformation("Alson {Mode} batch {Batch}: examined {Examined}, sales {Sales}, tank rows {Tanks}, diagnostic events {Diagnostics}, archived {Archived}; {Outcomes}",
            settings.DryRun?"DRY RUN":"IMPORT",summary.BatchID,summary.Examined,summary.Sales,summary.TankReadings,summary.DiagnosticEvents,summary.Archived,JsonSerializer.Serialize(summary.Outcomes));
        log.LogInformation("Alson batch {Batch} finished: stop={StopReason}, listed={Listed}, unchanged audit skips={Skipped}, elapsed={ElapsedSeconds:F1}s. A file-limit stop may leave backlog; listed is not a full inbox count.",
            summary.BatchID,summary.StopReason,summary.Listed,summary.Skipped,stopwatch.Elapsed.TotalSeconds);
        if(manifestPath!=null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
            await File.WriteAllTextAsync(manifestPath,JsonSerializer.Serialize(new{Summary=summary,Files=evidence},new JsonSerializerOptions{WriteIndented=true}),ct);
        }
        return summary;
    }
}
