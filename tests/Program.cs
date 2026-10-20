using CSVReader;
using log4net;
using System.Formats.Tar;
using System.IO.Compression;
using CSVReader.models;
using System.Transactions;
using FintechEtl.FunctionApp;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--live-provisioning-check"))
{
    await LiveProvisioningCheck.Run();
    return;
}

var log = LogManager.GetLogger("RegressionTests");
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
void Test(string name, Action<string> action)
{
    var root = Path.Combine(Path.GetTempPath(), "FintechEtlRegression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { action(root); passed++; Console.WriteLine($"PASS {name}"); }
    finally { Directory.Delete(root, true); }
}
string Make(string root, string extension, params (string Name, string Text)[] entries)
{
    var source = Path.Combine(root, "source");
    Directory.CreateDirectory(source);
    var files = new List<string>();
    foreach (var entry in entries)
    {
        var path = Path.Combine(source, entry.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, entry.Text);
        files.Add(path);
    }
    var archive = Path.Combine(root, "60000520261019120000" + extension);
    ArchiveFiles.Create(archive, files, source);
    return archive;
}

foreach (var extension in new[] { ".zip", ".tgz", ".tar.gz" })
{
    Test($"{extension}: core and price response order", root =>
    {
        var archive = Make(root, extension, ("TAS.csv", "data"), ("PPD.csv", "data"), ("TDD.csv", "data"), ("TIN.csv", "data"), ("PSL.csv", "data"),
            ("PCH-600005.csv", "data"), ("GRP-600005.csv", "data"), ("PCD-600005.csv", "data"), ("PCA-600005.csv", "data"), ("RESFPR.csv", "data"));
        var backend = new FakeBackend();
        var result = new FtpConnectivity(log, backend).ProcessArchive(archive, "/Unprocessed/600005/input/" + Path.GetFileName(archive), "600005", _ => { });
        Check(result.Result == EtlResult.Processed, "Archive should succeed.");
        Check(backend.Calls.Select(ArchiveFiles.CsvOrder).SequenceEqual(Enumerable.Range(0, 10)), "Processor order differs from main ETL.");
    });
    Test($"{extension}: mixed result, filtered outputs and replay", root =>
    {
        var archive = Make(root, extension, ("nested/PSL.csv", "data"), ("TIN.csv", "data"), ("other.csv", "preserve"));
        var backend = new FakeBackend { Fail = "TIN.csv" };
        var processor = new FtpConnectivity(log, backend);
        var result = processor.ProcessArchive(archive, "/Unprocessed/600005/input/test" + extension, "600005", _ => { });
        Check(result.Result == EtlResult.Partial && result.Outputs.Count == 2, "Partial routing missing.");
        var good = ArchiveFiles.Extract(result.Outputs[0].LocalPath, Path.Combine(root, "good"));
        var bad = ArchiveFiles.Extract(result.Outputs[1].LocalPath, Path.Combine(root, "bad"));
        Check(good.Length == 2 && bad.Length == 1 && Path.GetFileName(bad[0]) == "TIN.csv", "Filtered archive contents wrong.");
        var count = backend.Calls.Count;
        var retryRoot = Path.Combine(root, "retry"); Directory.CreateDirectory(retryRoot);
        var retry = Path.Combine(retryRoot, Path.GetFileName(archive)); File.Copy(archive, retry);
        var replay = processor.ProcessArchive(retry, "/Unprocessed/600005/input/test" + extension, "600005", _ => { });
        Check(replay.Result == EtlResult.Partial && backend.Calls.Count == count, "Move retry reprocessed completed CSVs.");
    });
    Test($"{extension}: regenerated marker and hourly window", root =>
    {
        var archive = Make(root, extension, ("PSL.csv", "regenerated\nsite_id,grade_id\n600005,1\n"));
        var backend = new FakeBackend();
        var result = new FtpConnectivity(log, backend).ProcessArchive(archive, "/Unprocessed/600005/input/regen" + extension, "600005", _ => { });
        Check(result.Result == EtlResult.Regenerated && result.Outputs.Select(o => o.Folder).SequenceEqual(new[] { "RegeneratedFile", "Processed" }), "Regenerated routing missing.");
        Check(backend.Regeneration == new DateTime(2026, 10, 19, 12, 0, 0), "Regenerated timestamp wrong.");
        Check(File.ReadAllText(Path.Combine(root, "extracted", "PSL.csv")).StartsWith("regenerated"), "Source marker mutated.");
    });
    Test($"{extension}: unconfigured CON forwarding", root =>
    {
        var archive = Make(root, extension, ("CON-600005.csv", "config"), ("PSL.csv", "data"));
        var backend = new FakeBackend { Configured = false };
        var forwarded = new List<string>();
        var result = new FtpConnectivity(log, backend).ProcessArchive(archive, "/Unprocessed/600005/input/test", "600005", f => forwarded.Add(f));
        Check(result.Result == EtlResult.NotConfigured && forwarded.Count == 1 && backend.Calls.Count == 0, "Unconfigured behavior wrong.");
    });
}
Test("All failed CSVs route to exception", root =>
{
    var archive = Make(root, ".tgz", ("TIN.csv", "data"));
    var backend = new FakeBackend { Fail = "TIN.csv" };
    var result = new FtpConnectivity(log, backend).ProcessArchive(archive, "/input/allfailed", "600005", _ => { });
    Check(result.Result == EtlResult.Failed && result.Outputs.Single().Folder == "ExceptionFile", "All-failed route wrong.");
});
Test("Missing regenerated timestamp rejected before writes", root =>
{
    var archive = Make(root, ".tgz", ("TIN.csv", "regenerated\ndata"));
    var invalid = Path.Combine(root, "600005bad.tgz"); File.Move(archive, invalid);
    var backend = new FakeBackend();
    Throws<InvalidDataException>(() => new FtpConnectivity(log, backend).ProcessArchive(invalid, "/input/bad", "600005", _ => { }));
    Check(backend.Calls.Count == 0, "Bad regenerated name wrote data.");
});
Test("Regeneration retry skips completed CSVs", root =>
{
    var archive = Make(root, ".zip", ("PSL.csv", "regenerated\ndata"));
    var backend = new FakeBackend { FailRegeneration = true };
    var processor = new FtpConnectivity(log, backend);
    Throws<RetryableEtlException>(() => processor.ProcessArchive(archive, "/input/regen", "600005", _ => { }));
    var count = backend.Calls.Count;
    var retryRoot = Path.Combine(root, "retry"); Directory.CreateDirectory(retryRoot);
    var retry = Path.Combine(retryRoot, Path.GetFileName(archive)); File.Copy(archive, retry);
    backend.FailRegeneration = false;
    var result = processor.ProcessArchive(retry, "/input/regen", "600005", _ => { });
    Check(result.Result == EtlResult.Regenerated && backend.Calls.Count == count, "Retry repeated CSV writes.");
});
Test("CON configuration failure retains input for retry", root =>
{
    var archive = Make(root, ".zip", ("CON-600005.csv", "config"));
    var backend = new FakeBackend();
    Throws<RetryableEtlException>(() => new FtpConnectivity(log, backend).ProcessArchive(archive, "/input/con", "600005", _ => throw new RetryableEtlException("Missing path")));
    Check(backend.Status("/input/con") == "Retry", "CON delivery failure was terminal.");
});
Test("MagLink stock levels and deliveries, retries and inactive site", root =>
{
    var backend = new FakeBackend(); var processor = new FtpConnectivity(log, backend);
    foreach (var name in new[] { "stockLevels.csv", "deliveries.csv" })
    {
        var file = Path.Combine(root, name); File.WriteAllText(file, "data");
        var result = processor.ProcessMagLink(file, "/MagLink/600005/" + name, "600005");
        Check(result.Outputs.Single().Folder == "MagLinkProcessed", "MagLink route wrong.");
        var count = backend.Calls.Count;
        processor.ProcessMagLink(file, "/MagLink/600005/" + name, "600005");
        Check(backend.Calls.Count == count, "MagLink move retry duplicated writes.");
        backend.Configured = false;
        Check(processor.ProcessMagLink(file, "/other/" + name, "600005").Result == EtlResult.NotConfigured, "Inactive MagLink site processed.");
        backend.Configured = true;
    }
});
Test("ZIP traversal, absolute paths, duplicate names and links rejected", root =>
{
    foreach (var name in new[] { "../escape.csv", "/absolute.csv", "C:/absolute.csv", "folder/../../escape.csv", "folder\\..\\escape.csv", "link.csv", "duplicate.csv" })
    {
        var zipPath = Path.Combine(root, Guid.NewGuid() + ".zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(name);
            if (name == "link.csv") entry.ExternalAttributes = 0xA000 << 16;
            using (var writer = new StreamWriter(entry.Open())) writer.Write("data");
            if (name == "duplicate.csv") zip.CreateEntry(name);
        }
        Throws<InvalidDataException>(() => ArchiveFiles.Extract(zipPath, Path.Combine(root, Guid.NewGuid().ToString())));
    }
});
Test("TAR symlinks rejected and standard ./ paths supported", root =>
{
    var link = Path.Combine(root, "link.tgz");
    using (var output = File.Create(link))
    using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
    using (var writer = new TarWriter(gzip))
        writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link.csv") { LinkName = "../outside.csv" });
    Throws<InvalidDataException>(() => ArchiveFiles.Extract(link, Path.Combine(root, "bad")));
    var normal = Path.Combine(root, "normal.tgz");
    using (var output = File.Create(normal))
    using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
    using (var writer = new TarWriter(gzip))
    using (var content = new MemoryStream("data"u8.ToArray()))
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./nested/TIN.csv") { DataStream = content });
    Check(ArchiveFiles.Extract(normal, Path.Combine(root, "good")).Length == 1, "Standard TAR path rejected.");
});
Test("ZIP entry count limit, corrupt ZIP and TGZ", root =>
{
    var path = Path.Combine(root, "many.zip");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        for (int i = 0; i <= ArchiveFiles.MaxEntries; i++) zip.CreateEntry($"{i}.csv");
    Throws<InvalidDataException>(() => ArchiveFiles.Extract(path, Path.Combine(root, "many")));
    foreach (var ext in new[] { ".zip", ".tgz" })
    {
        var corrupt = Path.Combine(root, "corrupt" + ext); File.WriteAllText(corrupt, "not an archive");
        Throws<InvalidDataException>(() => ArchiveFiles.Extract(corrupt, Path.Combine(root, Guid.NewGuid().ToString())));
    }
});
Test("Cross-site payload rejected before DAL access", root =>
{
    var file = Path.Combine(root, "PPD.csv"); File.WriteAllText(file, "site_id,pay_sale_id\n999999,1\n");
    Throws<InvalidDataException>(() => new LegacyEtlBackend(log).ValidateCsv(file, "600005"));
    File.WriteAllText(file, "site_id,pay_sale_id\n600005,1\n");
    new LegacyEtlBackend(log).ValidateCsv(file, "600005");
});
Test("RESFPR rejects cross-site exchange ID before SQL", root =>
{
    var file = Path.Combine(root, "RESFPR.csv"); File.WriteAllText(file, "file_exchange_id,status,fusion_id\n999999_100_42,OK,123\n");
    Throws<InvalidDataException>(() => new PriceWritebackResponseProcessor(log, 600005).ProcessResFpr(file));
});
Test("UTF8 BOM cannot bypass CSV site validation", root =>
{
    var file = Path.Combine(root, "PPD.csv");
    File.WriteAllText(file, "site_id,pay_sale_id\n999999,1\n", new System.Text.UTF8Encoding(true));
    Throws<InvalidDataException>(() => new LegacyEtlBackend(log).ValidateCsv(file, "600005"));
});
Test("Oversized declared ZIP entry rejected before extraction", root =>
{
    var archive = Make(root, ".zip", ("PSL.csv", "data"));
    var bytes = File.ReadAllBytes(archive);
    for (int i = 0; i < bytes.Length - 28; i++)
        if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 1 && bytes[i + 3] == 2)
        {
            BitConverter.GetBytes((int)ArchiveFiles.MaxEntryBytes + 1).CopyTo(bytes, i + 24);
            break;
        }
    File.WriteAllBytes(archive, bytes);
    Throws<InvalidDataException>(() => ArchiveFiles.Extract(archive, Path.Combine(root, "unsafe")));
});
Test("Stable cross-platform SQL file keys and supported names", _ =>
{
    Check(DAL.RemoteFileKey("/Unprocessed/600005/input/a.tgz") == DAL.RemoteFileKey("\\Unprocessed\\600005\\input\\a.tgz"), "OS changes file identity.");
    foreach (var extension in new[] { ".ZIP", ".tgz", ".tar.gz" })
        Check(ArchiveFiles.IsValidName("123", "12320261019120000" + extension), "Variable-length site name rejected.");
    Check(!ArchiveFiles.IsValidName("600005", "other.tgz") && !ArchiveFiles.IsSupported("a.zip.part-1"), "Invalid name accepted.");
    Check(ArchiveFiles.DuplicateName("600005test.tar.gz", 2) == "600005test_2.tar.gz", "Compound archive extension lost on duplicates.");
});
Test("Split archive copy failure never deletes input", _ =>
{
    var calls = new List<string>();
    var outcome = new EtlOutcome(EtlResult.Partial, [new("good.tgz", "Processed"), new("bad.tgz", "ExceptionFile")]);
    Throws<IOException>(() => ArchivePublisher.Publish(outcome, "original.tgz",
        o => { calls.Add(o.Folder); if (o.Folder == "ExceptionFile") throw new IOException("Test upload failure"); },
        o => calls.Add("move"), () => calls.Add("delete")));
    Check(calls.SequenceEqual(new[] { "Processed", "ExceptionFile" }), "Input removed before both copies succeeded.");
    calls.Clear();
    ArchivePublisher.Publish(outcome, "original.tgz", o => calls.Add(o.Folder), o => calls.Add("move"), () => calls.Add("delete"));
    Check(calls.SequenceEqual(new[] { "Processed", "ExceptionFile", "delete" }), "Partial publication order wrong.");
});
Test("Regenerated archive copy completes before input move", _ =>
{
    var outcome = new EtlOutcome(EtlResult.Regenerated, [new("original.zip", "RegeneratedFile"), new("original.zip", "Processed")]);
    var calls = new List<string>();
    ArchivePublisher.Publish(outcome, "original.zip", o => calls.Add("copy:" + o.Folder), o => calls.Add("move:" + o.Folder), () => calls.Add("delete"));
    Check(calls.SequenceEqual(new[] { "copy:RegeneratedFile", "move:Processed" }), "Regenerated publication order wrong.");
});
Test("CSV writes commit on success and roll back on data failure", root =>
{
    var archive = Make(root, ".tgz", ("PSL.csv", "data"), ("TIN.csv", "data"));
    var backend = new FakeBackend { Fail = "TIN.csv" };
    new FtpConnectivity(log, backend).ProcessArchive(archive, "/input/atomic", "600005", _ => { });
    Check(backend.Committed == 1 && backend.RolledBack == 1, "Per-CSV transaction boundaries missing.");
});
Test("MagLink typed rows support large files, quoted products and empty results", _ =>
{
    var stock = new FintechTankLevelsForMagLinkModel { OmniSiteId = 10, ibank = 600005, Tank = 1, OmniAssetId_Tank = 42,
        ProductId = "Dealer's diesel", ProductNumber = 2, QTY = 12.5f, DateTime = new DateTime(2026, 10, 19, 12, 0, 0) };
    var rows = DAL.BuildMagLinkStockTable(Enumerable.Repeat(stock, 2001), 99);
    Check(rows.Rows.Count == 2001 && (string)rows.Rows[0]["Product_ID"] == "Dealer's diesel" && (float)rows.Rows[0]["Product_Volume"] == 12.5f, "Stock bulk rows changed values.");
    var delivery = new FintechTankDeliveryForMagLinkModel { StartDateTime = stock.DateTime, EndDateTime = stock.DateTime.AddHours(1), ProductId = stock.ProductId, ProductNumber = 2 };
    var deliveries = DAL.BuildMagLinkDeliveryTable([delivery], null);
    Check((TimeSpan)deliveries.Rows[0]["time_start"] == TimeSpan.FromHours(12) && deliveries.Rows[0]["ProcessedFileID"] == DBNull.Value, "Delivery mapping wrong.");
    Check(DAL.BuildMagLinkStockTable([], null).Rows.Count == 0, "Empty filtered result not supported.");
});
Test("CON container folder names match the existing consumer layout", _ =>
{
    var store = new FakeConFileStore();
    ConFileDelivery.PrepareFolders(store);
    ConFileDelivery.PrepareFolders(store);
    Check(store.Directories.SetEquals(new[] { "/ConfigurationUpdate", "/Discarded", "/Exception", "/Processed", "/Unprocessed" }), "CON folder structure differs from the provided layout.");
});
Test("Incoming CON files land directly in Unprocessed", root =>
{
    var file = Path.Combine(root, "CON-600005.csv"); File.WriteAllText(file, "configuration");
    var store = new FakeConFileStore();
    var path = ConFileDelivery.Deliver(store, file, "/Unprocessed");
    Check(path == "/Unprocessed/CON-600005.csv" && store.Files[path] == "configuration", "CON went to the wrong folder.");
    Check(store.Files.Keys.All(p => !p.Contains(".part-")), "Temporary delivery file remained after publication.");
    Check(File.Exists(file), "Local source was deleted before the archive pipeline completed.");
});
Test("Duplicate CON files are preserved in duplicate with numbered suffixes", root =>
{
    var file = Path.Combine(root, "CON-600005.csv"); File.WriteAllText(file, "new");
    var store = new FakeConFileStore();
    store.Files["/Unprocessed/CON-600005.csv"] = "original";
    store.Files["/Unprocessed/duplicate/CON-600005.csv"] = "first duplicate";
    var path = ConFileDelivery.Deliver(store, file, "/Unprocessed");
    Check(path == "/Unprocessed/duplicate/CON-600005_1.csv" && store.Files[path] == "new", "Numbered duplicate path wrong.");
    Check(store.Files["/Unprocessed/CON-600005.csv"] == "original", "Original CON was overwritten.");
});
Test("Failed CON publication removes temporary files and retains source", root =>
{
    var file = Path.Combine(root, "CON-600005.csv"); File.WriteAllText(file, "data");
    var store = new FakeConFileStore { FailRename = true };
    Throws<IOException>(() => ConFileDelivery.Deliver(store, file, "/Unprocessed"));
    Check(store.Files.Count == 0 && File.Exists(file), "Failed delivery lost input or exposed a partial file.");
    Throws<InvalidDataException>(() => ConFileDelivery.Deliver(store, Path.Combine(root, "PSL.csv"), "/Unprocessed"));
});
Test("Settings select FintechStagDB and a separate CON container login", _ =>
{
    var overrides = new Dictionary<string, string>
    {
        ["EtlDryRun"] = "true", ["SftpPassword"] = "test", ["SftpHostKeySha256"] = "SHA256:test",
        ["FintechSqlConnectionString"] = "Server=test;Initial Catalog=FintechStagDB;Integrated Security=true;",
        ["ConSftpUsername"] = "fintechsftpdata.con-files.sftpadmin", ["ConSftpDestinations"] = "/Unprocessed",
        ["SftpUsername"] = "fintechsftpdata.sftpadmin", ["MagLinkSftpRoot"] = "", ["EtlWorkerCount"] = "3", ["EtlMinimumFileAgeSeconds"] = "60"
    };
    var saved = overrides.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
    try
    {
        foreach (var item in overrides) Environment.SetEnvironmentVariable(item.Key, item.Value);
        var settings = EtlSettings.Load();
        Check(new System.Data.SqlClient.SqlConnectionStringBuilder(settings.SqlConnectionString).InitialCatalog == "FintechStagDB", "Staging database rejected.");
        Check(settings.Username != settings.ConUsername && settings.ConDestinations.SequenceEqual(new[] { "/Unprocessed" }), "CON container connection mixed with source.");
        Environment.SetEnvironmentVariable("ConSftpUsername", null);
        Throws<InvalidOperationException>(() => EtlSettings.Load());
        Environment.SetEnvironmentVariable("ConSftpUsername", overrides["ConSftpUsername"]);
        Environment.SetEnvironmentVariable("FintechSqlConnectionString", "Server=test;Initial Catalog=FintechStressTesting;Integrated Security=true;");
        Throws<InvalidOperationException>(() => EtlSettings.Load());
    }
    finally { foreach (var item in saved) Environment.SetEnvironmentVariable(item.Key, item.Value); }
});
Test("Site input and output live under Unprocessed", _ =>
{
    Check(SftpLayout.Input("111111") == "/Unprocessed/111111/input", "Input points to the old root layout.");
    Check(SftpLayout.Output("111111") == "/Unprocessed/111111/output", "Output is not a sibling of input.");
    Check(SftpLayout.Input("123") == "/Unprocessed/123/input", "Variable-length site IDs were lost.");
    Check(!SftpLayout.IsSiteId("Processed") && !SftpLayout.IsSiteId("output") && !SftpLayout.IsSiteId("0"), "Non-site folder accepted for scanning.");
    Throws<ArgumentException>(() => SftpLayout.Input("../111111"));
    Throws<ArgumentException>(() => SftpLayout.Output("2147483648"));
});
Test("Moved site checkpoints retain their prior input identity", _ =>
{
    Check(SftpLayout.PreviousInputDirectory("/Unprocessed/111111/input") == "/111111/input", "Previous archive key not retained.");
    Check(SftpLayout.PreviousInputDirectory("/Unprocessed/111111/input/a.zip!/nested") == "/111111/input/a.zip!/nested", "Previous child checkpoint key not retained.");
    Check(SftpLayout.PreviousInputDirectory("/Unprocessed/111111/output") == "/Unprocessed/111111/output", "Output was incorrectly aliased to an input key.");
    Check(SftpLayout.PreviousInputDirectory("/notconfigured/111111") == "/notconfigured/111111", "Exception/archive identity changed.");
});
Test("Successful archives use Processed while error folders stay at root", root =>
{
    var archive = Make(root, ".tgz", ("TIN.csv", "data"));
    var result = new FtpConnectivity(log, new FakeBackend()).ProcessArchive(archive, SftpLayout.Input("600005") + "/test.tgz", "600005", _ => { });
    Check(result.Outputs.Single().Folder == "Processed", "Success still targets ProcessedFile.");
    Check(SftpLayout.ArchiveFolders.SequenceEqual(new[] { "Processed", "ExceptionFile", "invalidFile", "notconfigured", "RegeneratedFile" }), "Other archive folders changed.");
});
Test("Provisioning accepts site strings or integers and validates both fields", _ =>
{
    Check(SftpUserRequest.Parse("{\"siteId\":123,\"username\":\"site123\"}") == new SftpUserRequest("123", "site123"), "Integer site rejected.");
    Check(SftpUserRequest.Parse("{\"siteid\":\"111111\",\"username\":\"user111111\"}").SiteId == "111111", "String site rejected.");
    foreach (var body in new[] { "{}", "[]", "null", "{", "{\"siteId\":\"../12\",\"username\":\"user123\"}",
        "{\"siteId\":\"0123\",\"username\":\"user123\"}", "{\"siteId\":1.2,\"username\":\"user123\"}",
        "{\"siteId\":0,\"username\":\"user123\"}", "{\"siteId\":2147483648,\"username\":\"user123\"}",
        "{\"siteId\":123,\"username\":\"Admin\"}", "{\"siteId\":123,\"username\":\"a/b\"}",
        "{\"siteId\":123,\"username\":\"ab\"}", "{\"siteId\":123,\"username\":\"user123\",\"homeDirectory\":\"/\"}",
        "{\"siteId\":123,\"siteID\":456,\"username\":\"user123\"}", "{\"siteId\":123,\"username\":\"user123\",\"username\":\"other\"}" })
        Throws<ProvisioningException>(() => SftpUserRequest.Parse(body));
});
Test("Provisioning detects prior layout site assignments without prefix collisions", _ =>
{
    Check(AzureLocalSftpUsers.ContainsSite("sftp-data/123/input", "sftp-data/123"), "Prior home missed.");
    Check(AzureLocalSftpUsers.ContainsSite("sftp-data/Unprocessed/123", "sftp-data/Unprocessed/123"), "New home missed.");
    Check(!AzureLocalSftpUsers.ContainsSite("sftp-data/1234", "sftp-data/123"), "Different site rejected.");
});
Test("Provisioning creates ACL folders before enabling password and verifies access", _ =>
{
    var fake = new FakeProvisioning();
    var result = fake.Service.Create(new("123", "user123"), default).GetAwaiter().GetResult();
    Check(result.HomeDirectory == "sftp-data/Unprocessed/123" && result.LoginUsername == "account.user123" && result.Password == "test-password", "Response contract wrong.");
    Check(fake.Calls.SequenceEqual(new[] { "lock", "exists", "assigned", "validate", "lease", "create", "lease", "folders", "lease", "password", "verify", "lease" }), "Provisioning order changed.");
    Check(!fake.Deleted && !fake.RolledBack && fake.Disposed, "Successful provisioning rolled back or leaked resources.");
});
foreach (var rejection in new[] { "exists", "assigned", "validate" })
    Test("Provisioning conflict is read only: " + rejection, _ =>
    {
        var fake = new FakeProvisioning { Fail = rejection };
        Throws<ProvisioningException>(() => fake.Service.Create(new("123", "user123"), default).GetAwaiter().GetResult());
        Check(!fake.Calls.Contains("create") && !fake.Deleted && !fake.RolledBack, "A conflict changed an existing user/site.");
    });
foreach (var failure in new[] { "folders", "password", "verify", "invalid-id" })
    Test("Provisioning removes its new user on failure: " + failure, _ =>
    {
        var fake = new FakeProvisioning { Fail = failure };
        Throws<ProvisioningException>(() => fake.Service.Create(new("123", "user123"), default).GetAwaiter().GetResult());
        Check(fake.Deleted && fake.RolledBack, "Incomplete provisioning was left active.");
        Check(fake.Calls.IndexOf("disable") < fake.Calls.IndexOf("rollback") && fake.Calls.IndexOf("rollback") < fake.Calls.IndexOf("delete"), "Cleanup did not revoke authentication before folder/user deletion.");
    });
Test("Provisioning requires distributed lock and refuses to overwrite existing users", _ =>
{
    var fake = new FakeProvisioning { Fail = "lock" };
    Throws<ProvisioningException>(() => fake.Service.Create(new("123", "user123"), default).GetAwaiter().GetResult());
    Check(!fake.Calls.Contains("exists") && !fake.Calls.Contains("create"), "Provisioning ran without a lock.");
});
Test("Provisioning cleanup failure is explicit and does not delete folders after uncertain credential revocation", _ =>
{
    var fake = new FakeProvisioning { Fail = "verify", FailCleanup = true };
    try { fake.Service.Create(new("123", "user123"), default).GetAwaiter().GetResult(); throw new Exception("Failure expected."); }
    catch (ProvisioningException e) { Check(e.Code == "ProvisioningCleanupFailed", "Manual cleanup requirement hidden."); }
    Check(!fake.RolledBack, "Folders were deleted while login revocation was uncertain.");
});
foreach (var error in new[] { "none", "exists", "verify", "malformed", "oversized" })
    Test("HTTP provisioning response: " + error, _ =>
    {
        var fake = new FakeProvisioning { Fail = error };
        var function = new CreateSftpUser(NullLogger<CreateSftpUser>.Instance, () => fake.Service);
        var body = error == "malformed" ? "{" : error == "oversized" ? new string('x', 4097) : "{\"siteId\":123,\"username\":\"user123\"}";
        var response = function.Run(new TestHttpRequest(body), default).GetAwaiter().GetResult();
        response.Body.Position = 0;
        using var json = JsonDocument.Parse(response.Body);
        Check(response.Headers.GetValues("Cache-Control").Single() == "no-store", "Password response can be cached.");
        var result = json.RootElement;
        if (error == "none")
        {
            Check((int)response.StatusCode == 201 && result.GetProperty("success").GetBoolean(), "Successful response status wrong.");
            Check(result.GetProperty("password").GetString() == "test-password" && result.GetProperty("loginUsername").GetString() == "account.user123", "Login credentials missing.");
        }
        else
        {
            var expected = error is "malformed" ? 400 : error is "oversized" ? 413 : 409;
            Check((int)response.StatusCode == expected && !result.GetProperty("success").GetBoolean(), "Error HTTP status/success flag wrong.");
            Check(result.GetProperty("password").ValueKind == JsonValueKind.Null && result.GetProperty("exception").GetProperty("code").GetString() != null, "Failure leaks a password or omits exception details.");
        }
    });
Console.WriteLine($"{passed} regression scenarios passed. No SQL/SFTP connections or live writes were made.");

sealed class FakeBackend : IEtlBackend
{
    readonly Dictionary<string, int> ids = new();
    readonly Dictionary<int, string> statuses = new();
    public List<string> Calls { get; } = [];
    public bool Configured { get; set; } = true;
    public string Fail { get; set; }
    public bool FailRegeneration { get; set; }
    public DateTime? Regeneration { get; private set; }
    public int Committed { get; private set; }
    public int RolledBack { get; private set; }
    public bool IsConfigured(string site) => Configured;
    public string Status(string key) => ids.TryGetValue(key, out var id) ? statuses.GetValueOrDefault(id) : null;
    public int EnsureRecord(string local, string type, string key)
    {
        if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count + 1;
        return id;
    }
    public void SetStatus(int id, string status, string error = null) => statuses[id] = status;
    public void ValidateCsv(string file, string site) { }
    public void ProcessCsv(string file, string site, int id)
    {
        Calls.Add(file);
        if (Transaction.Current == null) throw new Exception("CSV transaction missing.");
        Transaction.Current.EnlistVolatile(new TestWrite(() => Committed++, () => RolledBack++), EnlistmentOptions.None);
        if (Path.GetFileName(file) == Fail) throw new InvalidDataException("Test data error.");
        if (ArchiveFiles.HasRegeneratedMarker(file)) File.WriteAllText(file, "marker removed by reader");
    }
    public void Regenerate(string site, DateTime timestamp)
    {
        if (FailRegeneration) throw new RetryableEtlException("Test timeout.");
        Regeneration = timestamp;
    }
}

sealed class TestWrite(Action committed, Action rolledBack) : IEnlistmentNotification
{
    public void Prepare(PreparingEnlistment enlistment) => enlistment.Prepared();
    public void Commit(Enlistment enlistment) { committed(); enlistment.Done(); }
    public void Rollback(Enlistment enlistment) { rolledBack(); enlistment.Done(); }
    public void InDoubt(Enlistment enlistment) => enlistment.Done();
}

sealed class FakeConFileStore : IConFileStore
{
    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public bool FailRename { get; set; }
    public bool Exists(string path) => Files.ContainsKey(path) || Directories.Contains(path);
    public void EnsureDirectory(string path) => Directories.Add(path);
    public void Upload(string localFile, string remotePath) => Files.Add(remotePath, File.ReadAllText(localFile));
    public void Rename(string source, string destination)
    {
        if (FailRename) throw new IOException("Test publication failure.");
        Files.Add(destination, Files[source]);
        Files.Remove(source);
    }
    public void Delete(string path) => Files.Remove(path);
}

sealed class FakeProvisioning : ILocalSftpUsers, ISiteSftpFolders, IProvisioningLease
{
    public string Fail { get; set; }
    public bool FailCleanup { get; set; }
    public bool Deleted { get; private set; }
    public bool RolledBack { get; private set; }
    public bool Disposed { get; private set; }
    public List<string> Calls { get; } = [];
    public SftpUserProvisioner Service => new(this, () => this, () => { Step("lock"); return this; }, "account", "account.blob.core.windows.net", 22);
    void Step(string step)
    {
        Calls.Add(step);
        if (Fail == step && step is not "exists" and not "assigned") throw new ProvisioningException(409, "TestFailure", "Expected test failure.");
    }
    public Task<bool> Exists(string username, CancellationToken cancellation) { Step("exists"); return Task.FromResult(Fail == "exists"); }
    public Task<bool> IsSiteAssigned(string site, CancellationToken cancellation) { Step("assigned"); return Task.FromResult(Fail == "assigned"); }
    public Task<LocalSftpUser> Create(string username, string home, CancellationToken cancellation)
    {
        Step("create"); return Task.FromResult(new LocalSftpUser(username, home, Fail == "invalid-id" ? 0 : 2345));
    }
    public Task<string> EnablePassword(LocalSftpUser user, CancellationToken cancellation) { Step("password"); return Task.FromResult("test-password"); }
    public Task Disable(string username, CancellationToken cancellation)
    {
        Step("disable"); if (FailCleanup) throw new IOException("Test cleanup error."); return Task.CompletedTask;
    }
    public Task Delete(string username, CancellationToken cancellation) { Step("delete"); Deleted = true; return Task.CompletedTask; }
    public void ValidateNewSite(string site) => Step("validate");
    public void Create(string site, int ownerId) => Step("folders");
    public void Verify(string site, string username, string password) => Step("verify");
    public void Rollback() { Step("rollback"); RolledBack = true; }
    void IProvisioningLease.Verify() => Step("lease");
    public void Dispose() => Disposed = true;
}
