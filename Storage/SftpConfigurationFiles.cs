namespace FintechAutoSiteConfiguration.FunctionApp.Storage;

// Downloads are temporary parser inputs; routing renames the original inside the CON container.
public sealed class SftpConfigurationFiles(IRemoteFiles remote) : IConfigurationFiles, IDisposable
{
    private readonly string workDirectory = Path.Combine(Path.GetTempPath(), "FintechAutoSiteConfiguration", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, RemoteFile> downloads = new(StringComparer.Ordinal);

    public bool DirectoryExists(string path) => Storage(() =>
    {
        var remotePath = SftpConnectionSettings.NormalizeRemotePath(path);
        return remote.Exists(remotePath) && remote.Stat(remotePath).IsDirectory;
    });

    public string[] GetFiles(string directory, string pattern) => Storage(() =>
    {
        if (pattern != "*.csv") throw new ArgumentException("Only CON CSV discovery is supported.", nameof(pattern));
        var remoteDirectory = SftpConnectionSettings.NormalizeRemotePath(directory);
        var candidates = remote.List(remoteDirectory).Where(file => file.IsRegularFile &&
            file.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
            !file.Name.StartsWith('.') && !file.Name.Contains(".part-", StringComparison.OrdinalIgnoreCase));
        var localFiles = new List<string>();
        foreach (var file in candidates)
        {
            if (file.Name.Contains('/') || file.Name.Contains('\\') || file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("The input contains an invalid filename.");
            var folder = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var localPath = Path.Combine(folder, file.Name);
            using (var output = File.Create(localPath)) remote.Download(file.FullName, output);
            if (!SameVersion(file, remote.Stat(file.FullName)) || new FileInfo(localPath).Length != file.Length)
                throw new IOException("A CON input changed during download; it remains in its source directory for retry.");
            downloads.Add(localPath, file);
            localFiles.Add(localPath);
        }
        return localFiles.ToArray();
    });

    public void CreateDirectory(string path) => Storage(() =>
    {
        var current = string.Empty;
        foreach (var segment in SftpConnectionSettings.NormalizeRemotePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (!remote.Exists(current)) remote.CreateDirectory(current);
        }
    });

    public void MoveFile(string localSource, string remoteDestination) => Storage(() =>
    {
        if (!downloads.TryGetValue(localSource, out var original))
            throw new IOException("The source was not downloaded by this invocation.");
        var destination = SftpConnectionSettings.NormalizeRemotePath(remoteDestination);
        if (string.Equals(original.FullName, destination, StringComparison.Ordinal)) return;
        if (!SameVersion(original, remote.Stat(original.FullName)))
            throw new IOException("A CON input changed during processing; remote routing was stopped.");
        if (remote.Exists(destination))
        {
            // Preserve an existing archive instead of deleting it to emulate File.Move(overwrite: true).
            var parent = destination[..destination.LastIndexOf('/')];
            destination = parent + "/" + Path.GetFileNameWithoutExtension(destination) + "_" + Guid.NewGuid().ToString("N") + Path.GetExtension(destination);
        }
        try { remote.Rename(original.FullName, destination); }
        catch
        {
            // A server-side rename can complete before the client receives a timeout.
            if (remote.Exists(original.FullName) || !remote.Exists(destination) || remote.Stat(destination).Length != original.Length)
                throw;
        }
        downloads.Remove(localSource);
        File.Delete(localSource);
    });

    public void AppendAllText(string remotePath, string text) => Storage(() =>
        remote.AppendAllText(SftpConnectionSettings.NormalizeRemotePath(remotePath), text));

    private static bool SameVersion(RemoteFile first, RemoteFile second) =>
        first.Length == second.Length && first.LastWriteTimeUtc == second.LastWriteTimeUtc && second.IsRegularFile;

    private static T Storage<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (ConfigurationStorageException) { throw; }
        catch (Exception exception) { throw new ConfigurationStorageException("CON SFTP storage operation failed; check connectivity, permissions and input stability.", exception); }
    }

    private static void Storage(Action operation) => Storage(() => { operation(); return true; });

    public void Dispose()
    {
        try { if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
