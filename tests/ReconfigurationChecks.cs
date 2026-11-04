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
        await Test("Validation rejection sends one structured error with the original remote path and no legacy duplicate", async () =>
        {
            var remote = new FakeRemote(); var source = "/Unprocessed/CON-60000520261030120000.csv"; remote.Put(source, "<ROOT><bad>");
            var events = new List<(string Category, string? Site, string? File, string Detail)>(); int legacy = 0;
            await Process(new FakeConfigurationSql(), remote,
                (category, site, file, disposition, detail) => events.Add((category, site, file, detail)), (_, _, _) => legacy++);
            Check(events.Count == 1 && events[0].Category == "Reconfiguration Failed" && events[0].File == source && events[0].Site == "600005" && events[0].Detail.Contains("Exception"), "Error lacks original CON path or actual exception, or was duplicated.");
            Check(legacy == 0 && remote.Exists("/Exception/600005/CON-60000520261030120000.csv"), "Legacy error email was duplicated or exception routing failed.");
        });
        await Test("Invalid CON filename triggers a structured exception-route alert", async () =>
        {
            var remote = new FakeRemote(); remote.Put("/Unprocessed/BAD.csv", "invalid");
            var events = new List<string>();
            await Process(new FakeConfigurationSql(), remote, (category, site, file, disposition, detail) => events.Add(category));
            Check(events.SequenceEqual(new[] { "InvalidFilename" }), "Invalid filename did not produce exactly one error event.");
        });
        await Test("Missing site mapping alerts and retains input without configuration writes", async () =>
        {
            var remote = new FakeRemote(); var source = "/Unprocessed/CON-60000520261030120000.csv"; remote.Put(source, "unused");
            var sql = new FakeConfigurationSql(); sql.Site.FCSiteId = 600006; var events = new List<string>();
            await Process(sql, remote, (category, site, file, disposition, detail) => events.Add(category));
            Check(events.SequenceEqual(new[] { "MissingSiteMapping" }) && remote.Exists(source) && sql.Clears == 0, "Missing mapping did not alert or consumed its input.");
        });
        await Test("Storage failure carries source and site to the single invocation alert boundary", async () =>
        {
            var remote = new FakeRemote { FailRename = true }; var source = "/Unprocessed/CON-60000520261030120000.csv"; remote.Put(source, Request().document.ToString());
            int localAlerts = 0;
            try { await Process(new FakeConfigurationSql(), remote, (_, _, _, _, _) => localAlerts++); throw new Exception("Storage failure was swallowed."); }
            catch (ConfigurationStorageException ex)
            {
                Check((string?)ex.Data["AutoSiteFile"] == source && (string?)ex.Data["AutoSiteSite"] == "600005" && localAlerts == 0,
                    "Invocation error lost the original source context or emitted a duplicate local alert.");
            }
        });
        await Test("Replacement rollback emits its actual diagnostic without changing activation", async () =>
        {
            var remote = new FakeRemote(); remote.Put("/Unprocessed/CON-60000520261030120000.csv", Request().document.ToString());
            var sql = new FakeConfigurationSql { FailAt = "pumpMapping" }; var details = new List<string>();
            await Process(sql, remote, (category, site, file, disposition, detail) => details.Add(detail));
            Check(details.Count == 1 && details[0].Contains("rolled back") && sql.RolledBack && sql.Active, "Rollback error email is missing or working activation changed.");
        });
        foreach (var failure in new[] { "validation", "replacement", "activation" })
        {
            await Test($"Portal-deactivated existing site reactivates preserved configuration after {failure} error", async () =>
            {
                var remote = new FakeRemote(); var name = "CON-60000520261030120000.csv";
                remote.Put("/ConfigurationUpdate/600005/" + name, failure == "validation" ? "<ROOT><bad>" : Request().document.ToString());
                var sql = new FakeConfigurationSql { Active = false, FailAt = failure == "replacement" ? "pumpMapping" : failure == "activation" ? "activation" : "" };
                sql.Site.IsActive = false;
                var notifications = new List<string>();
                await Process(sql, remote, (_, _, _, _, detail) => notifications.Add(detail));
                Check(sql.Active && sql.Site.IsActive == true && sql.Site.Status == "Error" && sql.Restores == 1
                    && sql.Assets.Single().AssetName == "working asset" && remote.Exists("/Exception/600005/" + name), "Failed reconfiguration did not restore the preserved site's activation or archive its CON.");
                Check(notifications.Count == 1 && notifications[0].Contains("IsActive=1") && notifications[0].Contains("Reconfiguration Failed")
                    && sql.Site.Exception!.StartsWith("Reconfiguration Failed:")
                    && remote.Read("/Exception/600005/" + Path.ChangeExtension(name, ".txt")).Contains("Reconfiguration Failed"),
                    "Reconfiguration email, saved error or sidecar omitted context/restored activation.");
            });
        }
        await Test("Rejected first configuration never activates a site with no preserved mappings", async () =>
        {
            var remote = new FakeRemote(); remote.Put("/Unprocessed/CON-60000520261030120000.csv", "<ROOT><bad>");
            var sql = new FakeConfigurationSql { Active = false, TankMapping = "", PumpMapping = "" };
            sql.Site.IsActive = false; sql.Assets.Clear();
            await Process(sql, remote);
            Check(!sql.Active && sql.Site.IsActive == false && sql.Restores == 0 && sql.Site.Status == "Error", "Invalid new site was activated.");
        });
        await Test("Failed activation restoration still routes exception and reports failure accurately", async () =>
        {
            var remote = new FakeRemote(); remote.Put("/Unprocessed/CON-60000520261030120000.csv", "<ROOT><bad>");
            var sql = new FakeConfigurationSql { Active = false, FailAt = "restore" }; sql.Site.IsActive = false;
            var notifications = new List<string>();
            await Process(sql, remote, (_, _, _, _, detail) => notifications.Add(detail));
            Check(!sql.Active && remote.Exists("/Exception/600005/CON-60000520261030120000.csv")
                && notifications.Single().Contains("ACTIVATION RESTORATION FAILED"), "Failed restore was hidden or exception publication skipped.");
        });
        Console.WriteLine($"{count} reconfiguration regression scenarios passed.");
    }

    static async Task Process(FakeConfigurationSql sql, FakeRemote remote,
        Action<string, string?, string?, string, string>? report = null, Action<string, string, string>? legacy = null)
    {
        using var files = new SftpConfigurationFiles(remote);
        var settings = Options.Create(new ApplicationSettings { UnprocessedFolder = "/Unprocessed", ProcessedFolder = "/Processed", ExceptionFolder = "/Exception", DuplicateFolder = "/Duplicate", ConfigurationUpdateFolder = "/ConfigurationUpdate", DiscardedFolder = "/Discarded", EmailRecipients = "nobody@example.invalid" });
        await new ParserService(sql, settings, files, new FileHelperService(files), legacy ?? ((_, _, _) => { })).ProcessFilesAsync(Channel.CreateUnbounded<string>(), report);
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
    public int Transactions, Clears, Activations, Restores;
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
    public override Task<bool> HasPreservedSiteConfiguration(int customerId, int siteId) => Task.FromResult(Assets.Count > 0 && TankMapping.Length > 0 && PumpMapping.Length > 0);
    public override Task<bool> RestorePreservedSiteActivation(int customerId, int siteId)
    {
        Restores++;
        if (FailAt == "restore") throw new InvalidOperationException("Simulated activation restore failure.");
        Active = true; return Task.FromResult(true);
    }
    public override Task<bool> MarkSiteActivated(int customerId, int siteId) { Active = true; Activations++; return Task.FromResult(FailAt != "activation"); }
    public override Task<List<SiteAndProductResponse>> GetOmniSiteIdAndProducts(int fcSiteId) => Task.FromResult(Products);
    public override Task<List<SiteMetaDataResponse>> GetSitesMetaData() => Task.FromResult(new List<SiteMetaDataResponse> { Site });
    public override Task<bool> UpdateSiteStatus(List<SiteMetaDataResponse> sites) => Task.FromResult(true);
}
