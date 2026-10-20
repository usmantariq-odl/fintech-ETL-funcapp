namespace FintechAutoSiteConfiguration.FunctionApp.Storage;

public interface IConfigurationFiles
{
    bool DirectoryExists(string path);
    string[] GetFiles(string directory, string pattern);
    void CreateDirectory(string path);
    void MoveFile(string localSource, string remoteDestination);
    void AppendAllText(string remotePath, string text);
}

public sealed class ConfigurationStorageException(string message, Exception innerException)
    : IOException(message, innerException);

public sealed record RemoteFile(string Name, string FullName, long Length, DateTime LastWriteTimeUtc,
    bool IsDirectory, bool IsRegularFile);

public interface IRemoteFiles : IDisposable
{
    bool Exists(string path);
    RemoteFile Stat(string path);
    RemoteFile[] List(string directory);
    void Download(string path, Stream destination);
    void CreateDirectory(string path);
    void Rename(string source, string destination);
    void AppendAllText(string path, string text);
}
