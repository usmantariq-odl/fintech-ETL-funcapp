namespace FintechEtl.FunctionApp.Alson;

internal sealed record AlsonWorkPartition(string Master,int MaxFiles);

internal static class AlsonWorkCoordinator
{
    // One bounded scan per master; reserve discovery slots without consuming the
    // whole budget on unknown-site backlogs. Rotate when the cap is below master count.
    internal static IReadOnlyList<AlsonWorkPartition> Plan(IEnumerable<string> allowed,IEnumerable<string> discovered,int maxFiles,long cycle)
    {
        var known=allowed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unknown=discovered.Except(known,StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var all=known.Concat(unknown).ToArray();
        if(all.Length==0)return [];
        if(maxFiles<all.Length)
            return Enumerable.Range(0,maxFiles).Select(i=>new AlsonWorkPartition(all[(int)((cycle+i)%all.Length)],1)).ToArray();
        var result=new List<AlsonWorkPartition>();
        int available=maxFiles-unknown.Length;
        for(int i=0;i<known.Length;i++)result.Add(new(known[i],available/known.Length+(i<available%known.Length?1:0)));
        foreach(var master in unknown)result.Add(new(master,1));
        return result;
    }

    internal static async Task Execute(IReadOnlyList<AlsonWorkPartition> plan,int workers,
        Func<AlsonWorkPartition,int,CancellationToken,Task> process,CancellationToken ct)
    {
        int next=-1;
        var jobs=Enumerable.Range(1,Math.Min(workers,plan.Count)).Select(worker=>Task.Run(async()=>
        {
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                int index=Interlocked.Increment(ref next);if(index>=plan.Count)return;
                await process(plan[index],worker,ct);
            }
        },ct));
        await Task.WhenAll(jobs);
    }
}
