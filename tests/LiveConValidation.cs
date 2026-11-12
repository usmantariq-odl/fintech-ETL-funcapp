using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using FintechAutoSiteConfiguration.FunctionApp.Storage;

// Explicit read-only diagnostic: downloads evidence and validates proposed mappings in memory.
internal static class LiveConValidation
{
    internal static async Task Run(string settingsPath,string remotePath)
    {
        var values=JsonSerializer.Deserialize<List<Setting>>(File.ReadAllText(settingsPath))!;
        var config=new ConfigurationBuilder().AddInMemoryCollection(values.Select(v=>new KeyValuePair<string,string?>(v.name.Replace("__",":"),v.value))).Build();
        using var remote=new SftpRemoteFiles(SftpConnectionSettings.Load(config));
        string path=Path.Combine(Path.GetDirectoryName(settingsPath)!,Path.GetFileName(remotePath));
        if(!File.Exists(path)||new FileInfo(path).Length==0)
        {
            using(var output=File.Create(path+".part"))remote.Download(remotePath,output);
            File.Move(path+".part",path,true);
        }
        var sql=new SQLService(Options.Create(new ApplicationSettings {AzureSql=config["ApplicationSettings:AzureSql"]!}));
        var products=await sql.GetOmniSiteIdAndProducts(1111);
        Console.WriteLine("Customer products: "+JsonSerializer.Serialize(products));
        var document=HelperService.ReadFile(path);
        foreach(var g in HelperService.GetTagByName(document,"grade"))
            Console.WriteLine("Grade: "+JsonSerializer.Serialize(g));
        var request=new ParseDataRequest {FC_SiteId=1111,Fintech_SiteId=products[0].SiteId,Fintech_CustomerId=products[0].CustomerId,filePath=path,document=document,
            Nodes=new ParseDataNodeRequest {Tanks=HelperService.GetTagByName(document,"tank"),Pumps=HelperService.GetTagByName(document,"pump"),Grades=HelperService.GetTagByName(document,"grade")}};
        ConfigurationValidator.Validate(request,products);
        Console.WriteLine("PASS real CON validation; no SQL writes, routing, activation or email.");
    }
    private sealed record Setting(string name,string value);
}
