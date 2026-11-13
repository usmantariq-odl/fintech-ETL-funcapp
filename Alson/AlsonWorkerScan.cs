using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace FintechEtl.FunctionApp.Alson;

public sealed partial class AlsonEtlTimer
{
    internal async Task ScanWorkers(AlsonSettings settings,AlsonAlerts alerts,string run,CancellationToken ct)
    {
        using var runScope=log.BeginScope(new Dictionary<string,object>{{"AlsonRunId",run}});
        var elapsed=Stopwatch.StartNew();
        var container=new BlobContainerClient(settings.Blob,settings.Container);
        int discoveryBudget=Math.Max(0,Math.Min(10,settings.MaxFiles-settings.Masters.Count));
        var discovery=DiscoverMasters(settings,container,run,ct);
        var plan=AlsonWorkCoordinator.Plan(settings.Masters,[],settings.MaxFiles-discoveryBudget,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60);
        log.LogInformation("Alson run {Run}: starting {Workers} workers; database={Database}, dry run={DryRun}, total file cap={Limit}, master partitions={Plan}. Limits apply across all workers, not per worker.",
            run,Math.Min(settings.WorkerCount,plan.Count),settings.ExpectedDatabase,settings.DryRun,settings.MaxFiles,JsonSerializer.Serialize(plan));
        var results=new ConcurrentBag<Summary>();var failures=new ConcurrentBag<Exception>();
        async Task Process(AlsonWorkPartition partition,int worker,CancellationToken token)
        {
            var remaining=TimeSpan.FromMinutes(4)-elapsed.Elapsed;
            if(remaining<=TimeSpan.Zero)
            {
                log.LogWarning("Alson run {Run}: time budget reached before master {Master}; its files remain for the next invocation",run,partition.Master);
                return;
            }
            using var workerScope=log.BeginScope(new Dictionary<string,object>{{"AlsonWorker",worker},{"AlsonMaster",partition.Master}});
            log.LogInformation("Alson run {Run} worker {Worker}: starting master {Master}, file budget={Limit}",run,worker,partition.Master,partition.MaxFiles);
            try
            {
                var summary=await Scan(settings with {MaxFiles=partition.MaxFiles},alerts,token,
                    runBudget:remaining,inputPrefix:settings.Prefix+partition.Master+"-");
                results.Add(summary);
                log.LogInformation("Alson run {Run} worker {Worker}: master={Master}, batch={Batch}, files={Files}, transactions={Sales}, tank readings={Tanks}, diagnostic rows={Diagnostics}, archived={Archived}, stop={Stop}, outcomes={Outcomes}",
                    run,worker,partition.Master,summary.BatchID,summary.Examined,summary.Sales,summary.TankReadings,summary.DiagnosticEvents,summary.Archived,summary.StopReason,JsonSerializer.Serialize(summary.Outcomes));
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                failures.Add(ex);alerts.Add("master "+partition.Master,ex);
                log.LogError("Alson run {Run} worker {Worker}: master {Master} failed; other master workers continue. {Type}: {Reason}",run,worker,partition.Master,ex.GetType().Name,EtlErrorEmails.Redact(ex.Message));
            }
        }
        await AlsonWorkCoordinator.Execute(plan,settings.WorkerCount,Process,ct);
        int unknownPlanned=0;
        try
        {
            var discovered=await discovery;
            var unknownPlan=AlsonWorkCoordinator.Plan([],discovered.Except(settings.Masters,StringComparer.Ordinal),discoveryBudget,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60);
            unknownPlanned=unknownPlan.Count;
            await AlsonWorkCoordinator.Execute(unknownPlan,settings.WorkerCount,Process,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){failures.Add(ex);alerts.Add("master discovery",ex);}
        var outcomes=results.SelectMany(r=>r.Outcomes).GroupBy(p=>p.Key).ToDictionary(g=>g.Key,g=>g.Sum(p=>p.Value));
        log.LogInformation("Alson run {Run} complete: workers={Workers}, completed masters={Completed}/{Planned}, failed masters={Failures}, files={Files}, transactions={Sales}, tank readings={Tanks}, diagnostic rows={Diagnostics}, archived={Archived}, outcomes={Outcomes}, elapsed={ElapsedSeconds:F1}s",
            run,settings.WorkerCount,results.Count,plan.Count+unknownPlanned,failures.Count,results.Sum(r=>r.Examined),results.Sum(r=>r.Sales),results.Sum(r=>r.TankReadings),results.Sum(r=>r.DiagnosticEvents),results.Sum(r=>r.Archived),JsonSerializer.Serialize(outcomes),elapsed.Elapsed.TotalSeconds);
        if(!failures.IsEmpty)throw new AggregateException("One or more Alson master workers failed; inspect per-master logs and committed audits.",failures);
    }

    private async Task<HashSet<string>> DiscoverMasters(AlsonSettings settings,BlobContainerClient input,string run,CancellationToken ct)
    {
        // HNS/SFTP accounts only support '/' as hierarchy delimiter. A durable flat-list
        // cursor avoids repeatedly inspecting only the alphabetically first master.
        var stateContainer=new BlobContainerClient(settings.Blob,"fintech-alson-alerts");
        var state=stateContainer.GetBlobClient("staging/discovery/master-scan-cursor.json");
        string cursor=null;ETag? etag=null;
        try
        {
            var prior=await state.DownloadContentAsync(ct);etag=prior.Value.Details.ETag;
            cursor=prior.Value.Content.ToObjectFromJson<DiscoveryCursor>().Next;
        }
        catch(RequestFailedException ex) when(ex.Status==404){}
        var masters=new HashSet<string>(StringComparer.Ordinal);int listed=0;string next=null;
        await foreach(var page in input.GetBlobsAsync(prefix:settings.Prefix,cancellationToken:ct).AsPages(cursor,5000))
        {
            next=page.ContinuationToken;listed=page.Values.Count;
            foreach(var item in page.Values)
            {
                string master=item.Name[settings.Prefix.Length..].Split('-')[0];
                if(master.Length is >0 and <=128&&master.All(char.IsAsciiLetterOrDigit))masters.Add(master);
            }
            break;
        }
        // Only remember masters already reached in the page; later pages are visited
        // on later invocations. Dry previews never alter this state.
        if(!settings.DryRun)
        {
            await stateContainer.CreateIfNotExistsAsync(cancellationToken:ct);
            try
            {
                await state.UploadAsync(BinaryData.FromObjectAsJson(new DiscoveryCursor(next)),new BlobUploadOptions
                {Conditions=etag.HasValue?new BlobRequestConditions{IfMatch=etag.Value}:new BlobRequestConditions{IfNoneMatch=ETag.All}},ct);
            }
            catch(RequestFailedException ex) when(ex.Status==412){log.LogInformation("Alson run {Run}: another scanner advanced discovery cursor; its state is preserved",run);}
        }
        log.LogInformation("Alson run {Run}: discovery listed {Listed} names, masters={Masters}, more pages={More}; no file bodies or business data imported by discovery",run,listed,string.Join(',',masters),next!=null);
        return masters;
    }
    private sealed record DiscoveryCursor(string Next);
}
