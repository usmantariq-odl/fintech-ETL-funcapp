using System.Threading.Channels;
using System.Xml.Linq;
using FintechAutoSiteConfiguration.FunctionApp.Storage;
using Microsoft.Extensions.Options;

internal static class ReconfigurationChecks
{
    internal static async Task RunAsync()
    {
        int count = 0;
        async Task Test(string name, Func<Task> test)
        {
            await test(); count++; Console.WriteLine("PASS " + name);
        }
        await Test("Destructive SQL cleanup is prohibited outside a replacement transaction", async () =>
        {
            var sql = new SQLService(Options.Create(new ApplicationSettings()));
            await Reject(() => sql.ClearSiteConfiguration(7, 42));
        });
        foreach (var defect in new[] { "empty", "product", "tank", "capacity", "nozzle", "grade", "duplicate", "quantity", "suction" })
        {
            await Test($"Invalid {defect} configuration preserves working assets and activation before any SQL write", async () =>
            {
                var request = Request();
                switch (defect)
                {
                    case "empty": request.document.Root!.RemoveNodes(); break;
                    case "product": request.document.Descendants("name").Single().Value = "UnknownFuel"; break;
                    case "tank": request.document.Descendants("suction_high").Single().Value = "99-Diesel"; break;
                    case "capacity": request.document.Descendants("capacity").Single().Value = "0"; break;
                    case "nozzle": request.document.Descendants("real_id").Single().Remove(); break;
                    case "grade": request.document.Descendants("hose1").Single().Element("grade")!.Value = "UnknownFuel"; break;
                    case "duplicate": request.document.Descendants("tank").Single().Add(new XElement(request.document.Descendants("tank_def").Single())); break;
                    case "quantity": request.document.Descendants("hoses_qty").Single().Value = "2"; break;
                    case "suction": request.document.Descendants("suction_high").Single().Value = "1-UnknownFuel"; break;
                }
                PopulateNodes(request);
                var sql = new FakeConfigurationSql();
                await Reject(() => new SiteConfigurationWriter(sql).ReplaceAsync(request, sql.Products, true));
                Check(sql.Transactions == 0 && sql.Clears == 0 && sql.Active && sql.Assets.Single().AssetName == "working asset", "Invalid input mutated working configuration.");
            });
        }
        foreach (var failure in new[] { "clear", "area", "assets", "sqlException", "areaMapping", "readAssets", "tankMapping", "pumpMapping", "activation" })
        {
            await Test($"Failure at {failure} rolls back assets, mappings and activation", async () =>
            {
                var sql = new FakeConfigurationSql { FailAt = failure };
                await Reject(() => new SiteConfigurationWriter(sql).ReplaceAsync(Request(), sql.Products, true));
                Check(sql.RolledBack && !sql.Committed && sql.Active && sql.Assets.Single().AssetName == "working asset" && sql.TankMapping == "working tanks" && sql.PumpMapping == "working pumps", "Failed replacement did not restore the snapshot.");
            });
        }
        await Test("Valid replacement commits all mappings and preserves existing activation when activation is not requested", async () =>
        {
            var sql = new FakeConfigurationSql();
            await new SiteConfigurationWriter(sql).ReplaceAsync(Request(), sql.Products, false);
            Check(sql.Committed && !sql.RolledBack && sql.Active && sql.Activations == 0 && sql.Assets.Count == 3 && sql.TankMapping == "new tanks" && sql.PumpMapping == "new pumps", "Valid replacement was incomplete or changed activation.");
        });
        await Test("New site activation commits only with the complete replacement", async () =>
        {
            var sql = new FakeConfigurationSql { Active = false };
            await new SiteConfigurationWriter(sql).ReplaceAsync(Request(), sql.Products, true);
            Check(sql.Committed && sql.Active && sql.Activations == 1, "New site was not activated with its mappings.");
        });
        await Test("Failed activation of a new site rolls back to its previous inactive state", async () =>
        {
            var sql = new FakeConfigurationSql { Active = false, FailAt = "activation" };
            await Reject(() => new SiteConfigurationWriter(sql).ReplaceAsync(Request(), sql.Products, true));
            Check(sql.RolledBack && !sql.Active && sql.Assets.Single().AssetName == "working asset", "Failed activation leaked a new active state.");
        });
        await Test("SFTP failure after SQL commit preserves the committed configuration and input for retry", async () =>
        {
            var remote = new FakeRemote { FailRename = true };
            remote.Put("/Unprocessed/CON-60000520261030120000.csv", Request().document.ToString());
            var sql = new FakeConfigurationSql();
            try { await Process(sql, remote); }
            catch (ConfigurationStorageException) { }
            Check(sql.Committed && !sql.RolledBack && sql.Clears == 1 && sql.Active && sql.Assets.Count == 3 && remote.Exists("/Unprocessed/CON-60000520261030120000.csv"), "Storage failure deleted committed configuration or consumed its input.");
        });
        await Test("Malformed XML is rejected without clearing the current site", async () =>
        {
            var remote = new FakeRemote(); remote.Put("/Unprocessed/CON-60000520261030120000.csv", "<ROOT><bad>");
            var sql = new FakeConfigurationSql();
            await Process(sql, remote);
            Check(sql.Site.Status == "Error" && sql.Clears == 0 && sql.Active && sql.Assets.Single().AssetName == "working asset", "Malformed input cleared a working site.");
            Check(remote.Read("/Exception/600005/CON-60000520261030120000.txt").Length > 0, "Validation finding was not exposed in the sidecar.");
        });
        await Test("Actual parser validation error does not invoke destructive SQL cleanup", async () =>
        {
            var remote = new FakeRemote(); var request = Request(); request.document.Descendants("name").Single().Value = "UnknownFuel";
            remote.Put("/Unprocessed/CON-60000520261030120000.csv", request.document.ToString());
            var sql = new FakeConfigurationSql(); await Process(sql, remote);
            Check(sql.Site.Status == "Error" && sql.Clears == 0 && sql.Active && sql.Assets.Single().AssetName == "working asset", "Parser error handler deleted configuration.");
            Check(sql.Site.Exception?.Contains("product", StringComparison.OrdinalIgnoreCase) == true, "Missing validation explanation.");
        });
        await Test("Approved active-site update is processed without requiring deactivation", async () =>
        {
            var remote = new FakeRemote(); remote.Put("/ConfigurationUpdate/600005/CON-60000520261030120000.csv", Request().document.ToString());
            var sql = new FakeConfigurationSql(); await Process(sql, remote);
            Check(sql.Committed && sql.Active && sql.Site.Status == "Activated" && remote.Exists("/Processed/600005/CON-60000520261030120000.csv"), "Approved active update was parked instead of processed.");
        });
        await Test("Unapproved active-site updates retain the existing ConfigurationUpdate approval route", async () =>
        {
            var remote = new FakeRemote(); remote.Put("/Unprocessed/CON-60000520261030120000.csv", Request().document.ToString());
            var sql = new FakeConfigurationSql(); sql.Site.Status = "Activated"; await Process(sql, remote);
            Check(sql.Transactions == 0 && sql.Clears == 0 && sql.Active && remote.Exists("/ConfigurationUpdate/600005/CON-60000520261030120000.csv"), "Unapproved update replaced configuration.");
        });
        await Test("Replacement failure through the parser preserves configuration and records a useful error", async () =>
        {
            var remote = new FakeRemote(); remote.Put("/Unprocessed/CON-60000520261030120000.csv", Request().document.ToString());
            var sql = new FakeConfigurationSql { FailAt = "pumpMapping" }; await Process(sql, remote);
            Check(sql.RolledBack && sql.Clears == 1 && sql.Active && sql.Site.Status == "Error" && sql.Assets.Single().AssetName == "working asset", "Parser performed cleanup after rollback.");
        });
        await Test("Malformed subsequent filename cannot change the previous site's status or configuration", async () =>
        {
            var remote = new FakeRemote(); remote.Put("/Unprocessed/CON-60000520261030120000.csv", Request().document.ToString()); remote.Put("/Unprocessed/BAD.csv", "broken");
            var sql = new FakeConfigurationSql(); await Process(sql, remote);
            Check(sql.Committed && sql.Clears == 1 && sql.Active && sql.Site.Status == "Activated", "Malformed next input reused the previous site.");
        });
        Console.WriteLine($"{count} reconfiguration regression scenarios passed.");
    }

    static async Task Process(FakeConfigurationSql sql, FakeRemote remote)
    {
        using var files = new SftpConfigurationFiles(remote);
        var settings = Options.Create(new ApplicationSettings { UnprocessedFolder = "/Unprocessed", ProcessedFolder = "/Processed", ExceptionFolder = "/Exception", DuplicateFolder = "/Duplicate", ConfigurationUpdateFolder = "/ConfigurationUpdate", DiscardedFolder = "/Discarded", EmailRecipients = "nobody@example.invalid" });
        await new ParserService(sql, settings, files, new FileHelperService(files), (_, _, _) => { }).ProcessFilesAsync(Channel.CreateUnbounded<string>());
    }
    static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception) { return; }
        throw new Exception("Invalid configuration or simulated replacement failure was accepted.");
    }
    static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    internal static ParseDataRequest Request()
    {
        var request = new ParseDataRequest { FC_SiteId = 600005, Fintech_SiteId = 42, Fintech_CustomerId = 7, Fintech_SiteName = "Test Site", filePath = "CON-60000520261030120000.csv", document = XDocument.Parse("""
<ROOT><grade><grade_def ID="Diesel"><name>Diesel</name><number>2</number></grade_def></grade>
<tank><tank_def ID="1"><product>Diesel</product><capacity>20000</capacity></tank_def></tank>
<pump><pump_def ID="1"><hose/><hoses_qty>1</hoses_qty><hose1><grade>Diesel</grade><real_id>1</real_id><suction_high>1-Diesel</suction_high></hose1></pump_def></pump></ROOT>
""") };
        PopulateNodes(request); return request;
    }
    static void PopulateNodes(ParseDataRequest request) => request.Nodes = new ParseDataNodeRequest { Tanks = HelperService.GetTagByName(request.document, "tank"), Pumps = HelperService.GetTagByName(request.document, "pump"), Grades = HelperService.GetTagByName(request.document, "grade") };
}

internal sealed class FakeConfigurationSql() : SQLService(Options.Create(new ApplicationSettings()))
{
    public readonly List<SiteAndProductResponse> Products = [new() { SiteId = 42, CustomerId = 7, ProductId = 2, ProductName = "Diesel" }];
    public SiteMetaDataResponse Site = new() { FCSiteId = 600005, FintechSiteId = 42, FintechCustomerId = 7, FintechSiteName = "Test Site", IsActive = true, IsConfigurationAllowed = true, IsActivationAllowed = true, Status = "In Progress" };
    public List<AssetResponse> Assets = [new() { AssetId = 100, AssetName = "working asset", AssetType = "24" }];
    public string TankMapping = "working tanks", PumpMapping = "working pumps", FailAt = "";
    public bool Active = true, Committed, RolledBack;
    public int Transactions, Clears, Activations;
    public override async Task ExecuteConfigurationTransactionAsync(int siteId, Func<Task> replace)
    {
        Transactions++; var assets = Assets.ToList(); var tank = TankMapping; var pump = PumpMapping; bool active = Active;
        try { await replace(); Committed = true; }
        catch { Assets = assets; TankMapping = tank; PumpMapping = pump; Active = active; RolledBack = true; throw; }
    }
    public override Task<bool> ClearSiteConfiguration(int customerId, int siteId) { Clears++; Assets.Clear(); TankMapping = PumpMapping = ""; if (FailAt == "clear") throw new InvalidOperationException("Simulated SQL cleanup failure."); return Task.FromResult(true); }
    public override Task<int> CreateArea(int siteId, string siteName) => Task.FromResult(FailAt == "area" ? 0 : 1);
    public override Task<bool> CreateAssets(List<CreateAssetRequest> assets)
    {
        Assets = assets.Select((a, i) => new AssetResponse { AssetId = i + 1, AssetName = a.Asset_Name, AssetType = a.AssetType }).ToList();
        if (FailAt == "sqlException") throw new InvalidOperationException("Simulated SQL write failure.");
        return Task.FromResult(FailAt != "assets");
    }
    public override Task<bool> InsertIntoMappingTable(int areaId, int siteId) => Task.FromResult(FailAt != "areaMapping");
    public override Task<List<AssetResponse>> GetAssetsBySiteId(int siteId) => Task.FromResult(FailAt == "readAssets" ? new List<AssetResponse>() : Assets);
    public override Task<bool> SaveTankMapping(List<CreateTankMappingRequest> mappings, int siteId) { TankMapping = "new tanks"; return Task.FromResult(FailAt != "tankMapping"); }
    public override Task<bool> SavePumpMapping(List<CreatePumpMappingRequest> mappings, int siteId) { PumpMapping = "new pumps"; return Task.FromResult(FailAt != "pumpMapping"); }
    public override Task<bool> MarkSiteActivated(int customerId, int siteId) { Active = true; Activations++; return Task.FromResult(FailAt != "activation"); }
    public override Task<List<SiteAndProductResponse>> GetOmniSiteIdAndProducts(int fcSiteId) => Task.FromResult(Products);
    public override Task<List<SiteMetaDataResponse>> GetSitesMetaData() => Task.FromResult(new List<SiteMetaDataResponse> { Site });
    public override Task<bool> UpdateSiteStatus(List<SiteMetaDataResponse> sites) => Task.FromResult(true);
}
