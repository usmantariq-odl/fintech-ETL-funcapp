using System.Text;
using FintechAutoSiteConfiguration.FunctionApp.Storage;
using Microsoft.Extensions.Options;

if (args.Contains("--live-transaction-check"))
{
    await LiveTransactionCheck.RunAsync();
    return;
}

if (args.Contains("--live-sidecar-check"))
{
    LiveSidecarCheck.Run();
    return;
}

await ReconfigurationChecks.RunAsync();

var scenarios = new (string Name, Action Test)[]
{
    ("Azure-style append creates missing error sidecars and retains previous diagnostics", () =>
    {
        byte[]? content = null;
        Stream Open(string path, FileMode mode)
        {
            if (mode == FileMode.Append && content is null) throw new FileNotFoundException("Azure append requires an existing blob.");
            if (mode == FileMode.CreateNew && content is not null) throw new IOException("An existing log must not be overwritten.");
            var stream = new PersistedStream(bytes => content = bytes);
            if (content is not null) stream.Write(content);
            return stream;
        }
        SftpRemoteFiles.AppendText("/Exception/2008/CON.txt", "first error\n", _ => content is not null, Open);
        SftpRemoteFiles.AppendText("/Exception/2008/CON.txt", "second error\n", _ => content is not null, Open);
        Check(Encoding.UTF8.GetString(content!) == "first error\nsecond error\n", "Diagnostics were missing, overwritten, or contained a BOM.");
    }),
    ("CSV discovery excludes directories and partial uploads", () =>
    {
        var remote = new FakeRemote();
        remote.Put("/Unprocessed/CON-600005-latest.csv", "config");
        remote.Put("/Unprocessed/.hidden.csv", "partial");
        remote.Put("/Unprocessed/CON.part-123.csv", "partial");
        remote.Put("/Unprocessed/readme.txt", "ignore");
        using var files = new SftpConfigurationFiles(remote);
        var local = files.GetFiles("/Unprocessed", "*.csv");
        Check(local.Length == 1 && File.ReadAllText(local[0]) == "config", "Unexpected discovery/download result.");
        Check(remote.Exists("/Unprocessed/CON-600005-latest.csv"), "Discovery consumed the remote input.");
    }),
    ("All standard routes stay in the CON container", () =>
    {
        foreach (var route in new[] { "Processed", "ConfigurationUpdate", "Discarded", "Duplicate" })
        {
            var remote = new FakeRemote();
            remote.Put("/Unprocessed/CON-600005.csv", "config");
            using var files = new SftpConfigurationFiles(remote);
            var helper = new FileHelperService(files);
            var path = files.GetFiles("/Unprocessed", "*.csv").Single();
            var settings = Settings();
            _ = route switch
            {
                "Processed" => helper.MoveFileToProcessed(path, "600005", settings),
                "ConfigurationUpdate" => helper.MoveFileToConfigurationUpdate(path, "600005", settings),
                "Discarded" => helper.MoveFileToDiscarded(path, "600005", settings),
                _ => helper.MoveFileToDuplicate(path, "600005", settings)
            };
            Check(remote.Exists($"/{route}/600005/CON-600005.csv") && !remote.Exists("/Unprocessed/CON-600005.csv"), "Incorrect remote routing.");
            Check(!File.Exists(path), "The local parser input was not released.");
        }
    }),
    ("Exception route writes a remote error sidecar", () =>
    {
        var remote = new FakeRemote();
        remote.Put("/Unprocessed/CON-600005.csv", "config");
        using var files = new SftpConfigurationFiles(remote);
        new FileHelperService(files).MoveFileToException(files.GetFiles("/Unprocessed", "*.csv").Single(), "600005", Settings(), "invalid product");
        Check(remote.Read("/Exception/600005/CON-600005.txt").Contains("invalid product"), "Missing exception sidecar.");
    }),
    ("Approved configuration updates can be requeued and processed", () =>
    {
        var remote = new FakeRemote();
        remote.Put("/ConfigurationUpdate/600005/CON-600005.csv", "approved");
        using var files = new SftpConfigurationFiles(remote);
        Check(files.DirectoryExists("/ConfigurationUpdate/600005"), "Requeue directory is unavailable.");
        var path = files.GetFiles("/ConfigurationUpdate/600005", "*.csv").Single();
        new FileHelperService(files).MoveFileToProcessed(path, "600005", Settings());
        Check(remote.Exists("/Processed/600005/CON-600005.csv") && !remote.Exists("/ConfigurationUpdate/600005/CON-600005.csv"), "Requeue input did not move.");
    }),
    ("Existing archive collisions preserve both deliveries", () =>
    {
        var remote = new FakeRemote();
        remote.Put("/Unprocessed/CON-600005.csv", "new");
        remote.Put("/Processed/600005/CON-600005.csv", "old");
        using var files = new SftpConfigurationFiles(remote);
        new FileHelperService(files).MoveFileToProcessed(files.GetFiles("/Unprocessed", "*.csv").Single(), "600005", Settings());
        Check(remote.Read("/Processed/600005/CON-600005.csv") == "old", "An existing delivery was overwritten.");
        Check(remote.List("/Processed/600005").Count(file => file.IsRegularFile) == 2, "A delivery was lost.");
    }),
    ("Rename failures preserve the source and raise a storage error", () =>
    {
        var remote = new FakeRemote { FailRename = true };
        remote.Put("/Unprocessed/CON-600005.csv", "config");
        using var files = new SftpConfigurationFiles(remote);
        var path = files.GetFiles("/Unprocessed", "*.csv").Single();
        ExpectStorageFailure(() => new FileHelperService(files).MoveFileToProcessed(path, "600005", Settings()));
        Check(remote.Exists("/Unprocessed/CON-600005.csv") && File.Exists(path), "Failed routing consumed its input.");
    }),
    ("Lost rename acknowledgements are recognized", () =>
    {
        var remote = new FakeRemote { LoseRenameAcknowledgement = true };
        remote.Put("/Unprocessed/CON-600005.csv", "config");
        using var files = new SftpConfigurationFiles(remote);
        new FileHelperService(files).MoveFileToProcessed(files.GetFiles("/Unprocessed", "*.csv").Single(), "600005", Settings());
        Check(remote.Exists("/Processed/600005/CON-600005.csv"), "Completed rename was not recognized.");
    }),
    ("Inputs changed during download are retained", () =>
    {
        var remote = new FakeRemote { ChangeDuringDownload = true };
        remote.Put("/Unprocessed/CON-600005.csv", "config");
        using var files = new SftpConfigurationFiles(remote);
        ExpectStorageFailure(() => files.GetFiles("/Unprocessed", "*.csv"));
        Check(remote.Exists("/Unprocessed/CON-600005.csv"), "An unstable input was consumed.");
    }),
    ("Inputs changed after parsing cannot be routed", () =>
    {
        var remote = new FakeRemote();
        remote.Put("/Unprocessed/CON-600005.csv", "config");
        using var files = new SftpConfigurationFiles(remote);
        var path = files.GetFiles("/Unprocessed", "*.csv").Single();
        remote.Put("/Unprocessed/CON-600005.csv", "replacement");
        ExpectStorageFailure(() => new FileHelperService(files).MoveFileToProcessed(path, "600005", Settings()));
        Check(remote.Read("/Unprocessed/CON-600005.csv") == "replacement", "A replacement input was consumed.");
    }),
    ("Path traversal is rejected", () =>
    {
        try { SftpConnectionSettings.NormalizeRemotePath("/Unprocessed/../Processed"); }
        catch (ArgumentException) { return; }
        throw new Exception("A traversal path was accepted.");
    }),
    ("Temporary downloads are removed after the invocation", () =>
    {
        var remote = new FakeRemote();
        remote.Put("/Unprocessed/CON-600005.csv", "config");
        var files = new SftpConfigurationFiles(remote);
        var path = files.GetFiles("/Unprocessed", "*.csv").Single();
        files.Dispose();
        Check(!File.Exists(path) && remote.Exists("/Unprocessed/CON-600005.csv"), "Cleanup affected the remote input.");
    })
};

foreach (var scenario in scenarios) { scenario.Test(); Console.WriteLine("PASS " + scenario.Name); }
Console.WriteLine($"{scenarios.Length} offline storage regression scenarios passed.");

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void ExpectStorageFailure(Action action)
{
    try { action(); }
    catch (ConfigurationStorageException) { return; }
    throw new Exception("Expected an infrastructure storage exception.");
}
static IOptions<ApplicationSettings> Settings() => Options.Create(new ApplicationSettings
{
    UnprocessedFolder = "/Unprocessed", ProcessedFolder = "/Processed", ExceptionFolder = "/Exception",
    DuplicateFolder = "/Duplicate", ConfigurationUpdateFolder = "/ConfigurationUpdate", DiscardedFolder = "/Discarded"
});

sealed class PersistedStream(Action<byte[]> persist) : MemoryStream
{
    protected override void Dispose(bool disposing)
    {
        if (disposing) persist(ToArray());
        base.Dispose(disposing);
    }
}

sealed class FakeRemote : IRemoteFiles
{
    private readonly Dictionary<string, (byte[] Content, DateTime Modified)> files = new(StringComparer.Ordinal);
    private readonly HashSet<string> directories = new(StringComparer.Ordinal) { "/" };
    public bool FailRename { get; init; }
    public bool LoseRenameAcknowledgement { get; init; }
    public bool ChangeDuringDownload { get; init; }
    public void Put(string path, string content)
    {
        CreateDirectory(path[..path.LastIndexOf('/')]);
        files[path] = (Encoding.UTF8.GetBytes(content), DateTime.UtcNow);
    }
    public string Read(string path) => Encoding.UTF8.GetString(files[path].Content);
    public bool Exists(string path) => files.ContainsKey(path) || directories.Contains(path);
    public RemoteFile Stat(string path) => files.TryGetValue(path, out var entry)
        ? new(Path.GetFileName(path), path, entry.Content.Length, entry.Modified, false, true)
        : directories.Contains(path) ? new(Path.GetFileName(path), path, 0, DateTime.UnixEpoch, true, false)
        : throw new FileNotFoundException();
    public RemoteFile[] List(string directory) => directories.Concat(files.Keys)
        .Where(path => path != directory && path[..path.LastIndexOf('/')] == directory).Select(Stat).ToArray();
    public void Download(string path, Stream destination)
    {
        destination.Write(files[path].Content);
        if (ChangeDuringDownload) Put(path, Read(path) + "changed");
    }
    public void CreateDirectory(string path)
    {
        var current = string.Empty;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { current += "/" + segment; directories.Add(current); }
    }
    public void Rename(string source, string destination)
    {
        if (FailRename) throw new IOException("Simulated connection failure.");
        if (files.ContainsKey(destination)) throw new IOException("Destination already exists.");
        files[destination] = files[source];
        files.Remove(source);
        if (LoseRenameAcknowledgement) throw new IOException("Simulated timeout after rename.");
    }
    public void AppendAllText(string path, string text) => Put(path, (files.ContainsKey(path) ? Read(path) : "") + text);
    public void Dispose() { }
}
