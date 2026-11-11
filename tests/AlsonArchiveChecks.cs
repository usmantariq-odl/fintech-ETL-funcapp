using System.Security.Cryptography;
using System.Text;
using FintechEtl.FunctionApp.Alson;

internal static class AlsonArchiveChecks
{
    private sealed class Memory:IAlsonArchiveStorage
    {
        internal Dictionary<string,AlsonArchiveObject> Objects=[];
        internal int Deletes;internal bool FailDelete;internal bool ChangeBeforeDelete;
        public Task<AlsonArchiveObject> Read(string name,CancellationToken ct)=>Task.FromResult(Objects.GetValueOrDefault(name));
        public Task CreateIfMissing(string name,AlsonArchiveObject source,CancellationToken ct)
        {Objects.TryAdd(name,source with{Bytes=source.Bytes.ToArray(),ETag="destination"});return Task.CompletedTask;}
        public Task DeleteIfUnchanged(string name,string etag,CancellationToken ct)
        {
            if(ChangeBeforeDelete)Objects[name]=Objects[name] with{ETag="changed",Bytes=Encoding.UTF8.GetBytes("new source")};
            if(FailDelete)throw new IOException("Injected failure after copy.");
            if(Objects.TryGetValue(name,out var source)&&source.ETag!=etag)throw new IOException("Source ETag changed.");
            Objects.Remove(name);Deletes++;return Task.CompletedTask;
        }
    }
    internal static int Run()
    {
        int passed=0;const string src="Alson/inbox/MASTER-TRANSACTION-1.json",dest="Alson/Processed/MASTER-TRANSACTION-1.json";
        var body=Encoding.UTF8.GetBytes("{\"rawFrame\":\"original bytes\"}\r\n");var hash=SHA256.HashData(body);
        Memory New()=>new(){Objects={[src]=new(body.ToArray(),"original")}};
        void Check(bool b,string s){if(!b)throw new Exception(s);}
        void Test(string s,Action action){action();passed++;Console.WriteLine("PASS Alson archive: "+s);}
        void Move(Memory m)=>new AlsonArchive(m).Move(src,hash,CancellationToken.None).GetAwaiter().GetResult();
        void Fails(Memory m){try{Move(m);}catch(IOException){return;}throw new Exception("Unsafe archive accepted.");}
        Test("exact original bytes and filename move to case-sensitive Processed path",()=>{var m=New();Move(m);Check(!m.Objects.ContainsKey(src)&&m.Objects[dest].Bytes.SequenceEqual(body),"Move changed bytes or left source.");});
        Test("delete interruption resumes without overwrite or lost bytes",()=>{var m=New();m.FailDelete=true;Fails(m);Check(m.Objects.ContainsKey(src)&&m.Objects[dest].Bytes.SequenceEqual(body),"Failed move lost source.");m.FailDelete=false;Move(m);Check(!m.Objects.ContainsKey(src),"Resume did not finish.");});
        Test("existing different destination is preserved and source is retained",()=>{var m=New();m.Objects[dest]=new(Encoding.UTF8.GetBytes("different"),"existing");Fails(m);Check(m.Objects.ContainsKey(src)&&m.Deletes==0&&Encoding.UTF8.GetString(m.Objects[dest].Bytes)=="different","Collision overwrote or deleted data.");});
        Test("changed inbox body cannot be archived against old SQL hash",()=>{var m=New();m.Objects[src]=new(Encoding.UTF8.GetBytes("different"),"new");Fails(m);Check(!m.Objects.ContainsKey(dest)&&m.Deletes==0,"Changed source moved.");});
        Test("ETag change between verification and delete retains new source",()=>{var m=New();m.ChangeBeforeDelete=true;Fails(m);Check(m.Objects.ContainsKey(src)&&m.Deletes==0,"Changed source deleted.");});
        Test("already moved input is idempotent when destination hash matches",()=>{var m=New();Move(m);var r=new AlsonArchive(m).Move(src,hash,CancellationToken.None).GetAwaiter().GetResult();Check(r.AlreadyArchived,"Completed move was not recognized.");});
        Test("both missing is a failure rather than false success",()=>{var m=new Memory();Fails(m);});
        Test("outbox and unrelated prefixes cannot be archived",()=>{try{AlsonArchive.Destination("Alson/outbox/Unprocessed/price.json");}catch(IOException){return;}throw new Exception("Command path accepted.");});
        return passed;
    }
}
