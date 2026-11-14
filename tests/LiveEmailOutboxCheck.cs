using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using FintechEtl.FunctionApp;
using Microsoft.Extensions.Logging.Abstractions;

internal static class LiveEmailOutboxCheck
{
    public static async Task Run()
    {
        using var settings=JsonDocument.Parse(File.ReadAllText("local.settings.json"));
        Environment.SetEnvironmentVariable("AzureWebJobsStorage",settings.RootElement.GetProperty("Values").GetProperty("AzureWebJobsStorage").GetString());
        Environment.SetEnvironmentVariable("ACS_EMAIL_CONNECTION_STRING","SIMULATED-TRANSPORT-ONLY");
        Environment.SetEnvironmentVariable("EMAIL_RECIPIENTS","simulated@example.com");
        Environment.SetEnvironmentVariable("EtlDryRun","false");Environment.SetEnvironmentVariable("EtlErrorEmailEnabled","true");
        var scope="FintechEtl-OutboxCheck-"+Guid.NewGuid().ToString("N");
        var root=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope+"/"+(Environment.GetEnvironmentVariable("WEBSITE_SLOT_NAME")??"Production")))).ToLowerInvariant()+"/";
        var prefix=root+"events/";
        var container=new BlobContainerClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"),"fintech-etl-alerts");
        var now=DateTimeOffset.UtcNow;
        EtlErrorEmails Sender(string run,IEtlEmailTransport gateway)=>new(NullLogger.Instance,run,gateway,scope,()=>now);
        async Task<List<string>> Names(){var result=new List<string>();await foreach(var blob in container.GetBlobsAsync(prefix:prefix))result.Add(blob.Name);return result;}
        void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS live outbox: "+message);}
        async Task<EtlEmailEnvelope> Read(string name)=>(await container.GetBlobClient(name).DownloadContentAsync()).Value.Content.ToObjectFromJson<EtlEmailEnvelope>();
        try
        {
            var fake=new FakeEtlEmailTransport();
            using(var first=Sender("first",fake))
            {
                first.Report("Partial","900003","first.tgz","Synthetic check","PSL.csv empty");
                first.Report("NotConfigured","007160","second.zip","Synthetic check","No mapping");
            }
            Check(fake.Starts==2&&(await Names()).Count==2,"separate events submitted once and accepted operation IDs retained");
            now=now.AddMinutes(1);using(var recover=Sender("recover",fake)){}
            Check(fake.Starts==2&&fake.Polls==2&&(await Names()).Count==0,"restart polls existing operations; completed events drained");
            using(var duplicate=Sender("first",fake)){duplicate.Report("Partial","900003","first.tgz","Synthetic check","PSL.csv empty");}
            Check(fake.Starts==2&&(await Names()).Count==0,"same event is suppressed by durable receipt across restart");

            var rejected=new FakeEtlEmailTransport{OnStart=_=>throw new RequestFailedException(429,"Simulated throttle","TooManyRequests",null)};
            using(var failure=Sender("rejected",rejected)){failure.Report("Partial","900003","throttle.tgz","Synthetic","PSL.csv empty");}
            var rejectedName=(await Names()).Single();var state=await Read(rejectedName);
            Check(rejected.Starts==1&&state.State=="new"&&state.NextAttempt>=now.AddMinutes(5),"initial 429 is persisted with backoff");
            now=now.AddMinutes(1);using(var early=Sender("early",rejected)){}
            Check(rejected.Starts==1,"new invocation honors persisted retry time");
            now=state.NextAttempt;rejected.OnStart=null;using(var retry=Sender("retry",rejected)){}
            now=now.AddMinutes(1);using(var finish=Sender("finish",rejected)){}
            Check(rejected.Starts==2&&(await Names()).Count==0,"rejected submission recovers once after throttle");

            var ambiguous=new FakeEtlEmailTransport{OnStart=_=>throw new TaskCanceledException("Accepted but response lost")};
            using(var lost=Sender("lost",ambiguous)){lost.Report("Partial","900003","lost.tgz","Synthetic","PSL.csv empty");}
            var lostName=(await Names()).Single();var lostState=await Read(lostName);
            Check(lostState.State=="submitting","unknown POST outcome retains operation identity");
            now=now.AddMinutes(1);ambiguous.OnPoll=_=>throw new RequestFailedException(429,"Poll throttle","TooManyRequests",null);
            using(var pollThrottled=Sender("poll-throttled",ambiguous)){}
            var throttled=await Read(lostName);now=throttled.NextAttempt;ambiguous.OnPoll=null;
            using(var recovered=Sender("recovered",ambiguous)){}
            Check(ambiguous.Starts==1&&(await Names()).Count==0,"accepted email survives polling 429 without resubmission");
            await container.GetBlobClient(lostName).UploadAsync(BinaryData.FromObjectAsJson(lostState),overwrite:false);
            using(var cleanupRetry=Sender("cleanup-retry",ambiguous)){}
            Check(ambiguous.Starts==1&&(await Names()).Count==0,"receipt protects a restored stale event after cleanup failure");

            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var concurrent=new FakeEtlEmailTransport{OnStart=async e=>{entered.TrySetResult();await release.Task;return e.OperationId.ToString();}};
            var owner=Sender("concurrent",concurrent);
            try
            {
                owner.Report("Partial","900003","concurrent.tgz","Synthetic","PSL.csv empty");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                using(var other=Sender("other-instance",concurrent)){}
            }
            finally{release.TrySetResult();owner.Dispose();}
            Check(concurrent.Starts==1,"two live outbox instances honor blob lease; only one POST");
            now=now.AddMinutes(1);using(var finish=Sender("concurrent-finish",concurrent)){}

            var legacy=new EtlAlert("Partial","900003","legacy.tgz","Retained","PSL.csv","old-run",now);
            await container.GetBlobClient(prefix+"legacy.json").UploadAsync(BinaryData.FromObjectAsJson(legacy),overwrite:false);
            await container.GetBlobClient(prefix+"legacy-retry.json").UploadAsync(BinaryData.FromObjectAsJson(legacy with{Category="FileRetry"}),overwrite:false);
            var legacyTransport=new FakeEtlEmailTransport();using(var recover=Sender("legacy-recovery",legacyTransport)){}
            Check(legacyTransport.Starts==0&&(await Names()).Count==1&&(await Read(prefix+"legacy.json")).State=="review","legacy ambiguous event retained without resend; old routine retry suppressed");
            using(var second=Sender("legacy-again",legacyTransport)){}
            Check(legacyTransport.Starts==0,"review events are not retried every invocation");
            Console.WriteLine("PASS real Azure Blob outbox integration; all ACS calls simulated. No real emails, SQL writes, SFTP operations or production event changes.");
        }
        finally
        {
            await foreach(var blob in container.GetBlobsAsync(prefix:root))await container.GetBlobClient(blob.Name).DeleteIfExistsAsync();
        }
    }
}
