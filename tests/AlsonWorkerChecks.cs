using System.Collections.Concurrent;
using FintechEtl.FunctionApp.Alson;

internal static class AlsonWorkerChecks
{
    internal static int Run()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        var allowed=new[]{"DSP-MAKKAH","ATG-MAKKAH","DSP-MANZOOR","ATG-MANZOOR"};
        var plan=AlsonWorkCoordinator.Plan(allowed,[..allowed,"NEW"],100,0);
        Check(plan.Sum(p=>p.MaxFiles)==100&&plan.Count==5,"Worker budgets must share one invocation cap.");
        Check(plan.Single(p=>p.Master=="NEW").MaxFiles==1,"Unknown backlog consumed financial budget.");
        Check(allowed.All(m=>plan.Any(p=>p.Master==m&&p.MaxFiles>=24)),"A configured master was starved.");
        Console.WriteLine("PASS Alson worker plan shares file budget fairly across DSP/ATG masters and bounds unknown discovery.");
        var reached=new HashSet<string>();
        for(int cycle=0;cycle<5;cycle++)
        {
            var small=AlsonWorkCoordinator.Plan(allowed,[..allowed,"NEW"],2,cycle);
            Check(small.Sum(p=>p.MaxFiles)==2&&small.Select(p=>p.Master).Distinct().Count()==2,"Small cap exceeded or repeated a partition.");
            foreach(var p in small)reached.Add(p.Master);
        }
        Check(reached.Count==5,"Small caps permanently starve later masters.");
        Console.WriteLine("PASS Alson small file caps rotate all masters without exceeding the cap.");
        int active=0,peak=0;
        var seen=new ConcurrentDictionary<string,int>();
        AlsonWorkCoordinator.Execute(Enumerable.Range(0,12).Select(i=>new AlsonWorkPartition(i.ToString(),1)).ToArray(),3,async(p,w,ct)=>
        {
            int current=Interlocked.Increment(ref active),old;
            do {old=Volatile.Read(ref peak);if(old>=current)break;}while(Interlocked.CompareExchange(ref peak,current,old)!=old);
            seen.AddOrUpdate(p.Master,1,(_,n)=>n+1);
            await Task.Delay(20,ct);
            Interlocked.Decrement(ref active);
        },CancellationToken.None).GetAwaiter().GetResult();
        Check(peak>1&&peak<=3&&seen.Count==12&&seen.Values.All(n=>n==1),"Workers did not overlap, exceeded concurrency or repeated/lost partitions.");
        Console.WriteLine("PASS Alson workers overlap within the configured bound and process each partition once.");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try
        {
            AlsonWorkCoordinator.Execute(plan,4,(_,_,_)=>throw new Exception("Cancelled work started"),cancel.Token).GetAwaiter().GetResult();
            throw new Exception("Cancelled worker run was accepted.");
        }
        catch(OperationCanceledException){}
        Console.WriteLine("PASS Alson workers respect cancellation before starting work.");
        return 4;
    }
}
