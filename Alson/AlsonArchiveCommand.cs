using System.Text.Json;
using Azure.Storage.Blobs;

namespace FintechEtl.FunctionApp.Alson;

internal static class AlsonArchiveCommand
{
    // Explicit repair for previously committed inputs. No target-table writes or emails.
    internal static async Task Run(string[] args)
    {
        using(var j=JsonDocument.Parse(await File.ReadAllTextAsync("alson.local.json")))
            foreach(var v in j.RootElement.GetProperty("Values").EnumerateObject())Environment.SetEnvironmentVariable(v.Name,v.Value.GetString());
        Environment.SetEnvironmentVariable("AlsonEtlEnabled","true");
        Environment.SetEnvironmentVariable("AlsonEtlDryRun","true");
        Environment.SetEnvironmentVariable("AlsonErrorEmailEnabled","false");
        var settings=AlsonSettings.Load();var store=new AlsonStore(settings);store.VerifySchema();
        var candidates=store.ArchiveCandidates();var apply=args.Contains("--apply");
        var index=Array.IndexOf(args,"--manifest");
        var path=index>=0&&index+1<args.Length?args[index+1]:Path.Combine("artifacts","alson","archive-move.json");
        var entries=candidates.Select(x=>new Entry(x.BlobName,AlsonArchive.Destination(x.BlobName),Convert.ToHexString(x.EnvelopeHash),x.Master,"planned")).ToList();
        var options=new BlobClientOptions();options.Retry.MaxRetries=2;options.Retry.NetworkTimeout=TimeSpan.FromSeconds(20);
        var archive=new AlsonArchive(new AlsonBlobArchiveStorage(new BlobContainerClient(settings.Blob,settings.Container,options)));
        await Save();
        if(!apply){Console.WriteLine($"Preview: {entries.Count} committed files eligible for Alson/Processed/. No changes.");return;}
        int moved=0,already=0;
        for(int i=0;i<entries.Count;i++)
        {
            try
            {
                var result=await archive.Move(candidates[i].BlobName,candidates[i].EnvelopeHash,CancellationToken.None);
                entries[i]=entries[i] with{State=result.AlreadyArchived?"already_archived":"moved"};
                if(result.AlreadyArchived)already++;else moved++;
                await Save();
                if((i+1)%25==0)Console.WriteLine($"Archive progress: {i+1}/{entries.Count}; moved {moved}, already archived {already}.");
            }
            catch(Exception ex)
            {
                entries[i]=entries[i] with{State="failed",Error=EtlErrorEmails.Redact(ex.Message)};
                await Save();throw;
            }
        }
        Console.WriteLine($"Archive complete: moved {moved}, already archived {already}. Database rows unchanged.");
        async Task Save()
        {
            var full=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp=full+".tmp";
            await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(new{Database=settings.ExpectedDatabase,Container=settings.Container,Apply=apply,Files=entries},new JsonSerializerOptions{WriteIndented=true}));
            File.Move(temp,full,true);
        }
    }
    private sealed record Entry(string Source,string Destination,string EnvelopeSha256,string Master,string State,string Error=null);
}
