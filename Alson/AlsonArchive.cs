using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace FintechEtl.FunctionApp.Alson;

internal sealed record AlsonArchiveObject(byte[] Bytes,string ETag,string ContentType="application/json",IDictionary<string,string> Metadata=null);
internal interface IAlsonArchiveStorage
{
    Task<AlsonArchiveObject> Read(string name,CancellationToken ct);
    Task CreateIfMissing(string name,AlsonArchiveObject source,CancellationToken ct);
    Task DeleteIfUnchanged(string name,string etag,CancellationToken ct);
}
internal sealed class AlsonBlobArchiveStorage(BlobContainerClient container):IAlsonArchiveStorage
{
    public async Task<AlsonArchiveObject> Read(string name,CancellationToken ct)
    {
        try
        {
            var data=(await container.GetBlobClient(name).DownloadContentAsync(ct)).Value;
            return new(data.Content.ToArray(),data.Details.ETag.ToString(),data.Details.ContentType,
                new Dictionary<string,string>(data.Details.Metadata));
        }
        catch(RequestFailedException ex)when(ex.Status==404){return null;}
    }
    public async Task CreateIfMissing(string name,AlsonArchiveObject source,CancellationToken ct)
    {
        try
        {
            await container.GetBlobClient(name).UploadAsync(new BinaryData(source.Bytes),new BlobUploadOptions
            {
                Conditions=new BlobRequestConditions{IfNoneMatch=ETag.All},
                HttpHeaders=new BlobHttpHeaders{ContentType=source.ContentType??"application/json"},
                Metadata=source.Metadata
            },ct);
        }
        catch(RequestFailedException ex)when(ex.Status is 409 or 412){/* Existing destination is verified below; never overwrite. */}
    }
    public async Task DeleteIfUnchanged(string name,string etag,CancellationToken ct)
    {
        try{await container.GetBlobClient(name).DeleteAsync(conditions:new BlobRequestConditions{IfMatch=new ETag(etag)},cancellationToken:ct);}
        catch(RequestFailedException ex)when(ex.Status==404){/* Another successful archiver already removed it. */}
    }
}

internal sealed class AlsonArchive(IAlsonArchiveStorage storage)
{
    internal sealed record Result(string Destination,bool AlreadyArchived);
    internal const string Inbox="Alson/inbox/";
    internal const string Processed="Alson/Processed/";
    internal static string Destination(string source)
    {
        if(!source.StartsWith(Inbox,StringComparison.Ordinal)||source.Length==Inbox.Length)
            throw new IOException("Archive input is outside Alson/inbox/.");
        return Processed+source[Inbox.Length..];
    }
    internal async Task<Result> Move(string source,byte[] expectedEnvelopeHash,CancellationToken ct)
    {
        var destination=Destination(source);
        var original=await storage.Read(source,ct);
        if(original==null)
        {
            Verify(await storage.Read(destination,ct),expectedEnvelopeHash,"Processed file missing or different after an earlier move.");
            return new(destination,true);
        }
        Verify(original,expectedEnvelopeHash,"Inbox file changed since its committed SQL audit; source retained.");
        await storage.CreateIfMissing(destination,original,ct);
        Verify(await storage.Read(destination,ct),expectedEnvelopeHash,"Processed file differs from committed original; inbox source retained.");
        // SQL is already committed. Delete only the exact source version that was verified.
        // A copy/delete interruption is resumed through the durable SQL audit on the next scan.
        await storage.DeleteIfUnchanged(source,original.ETag,ct);
        return new(destination,false);
    }
    private static void Verify(AlsonArchiveObject value,byte[] hash,string error)
    {
        if(value==null||!SHA256.HashData(value.Bytes).SequenceEqual(hash))throw new IOException(error);
    }
}
