using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;

namespace FintechAutoSiteConfiguration.FunctionApp.Storage;

public sealed class SftpRemoteFiles : IRemoteFiles
{
    private readonly SftpClient client;

    public SftpRemoteFiles(SftpConnectionSettings settings)
    {
        client = new SftpClient(settings.Host, settings.Port, settings.Username, settings.Password)
        {
            OperationTimeout = TimeSpan.FromSeconds(60),
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        client.HostKeyReceived += (_, args) =>
        {
            var actual = "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
            args.CanTrust = settings.HostKeys.Any(key => string.Equals(key.TrimEnd('='), actual, StringComparison.Ordinal));
        };
    }

    private SftpClient Connected()
    {
        if (!client.IsConnected) client.Connect();
        return client;
    }

    public bool Exists(string path) => Connected().Exists(path);
    public RemoteFile Stat(string path)
    {
        var attributes = Connected().GetAttributes(path);
        return new(Path.GetFileName(path), path, attributes.Size, attributes.LastWriteTimeUtc,
            attributes.IsDirectory, attributes.IsRegularFile);
    }

    public RemoteFile[] List(string directory) => Connected().ListDirectory(directory)
        .Where(file => file.Name is not "." and not "..")
        .Select(file => new RemoteFile(file.Name, file.FullName, file.Length, file.LastWriteTimeUtc,
            file.IsDirectory, file.IsRegularFile)).ToArray();

    public void Download(string path, Stream destination) => Connected().DownloadFile(path, destination);
    public void CreateDirectory(string path) => Connected().CreateDirectory(path);
    public void Rename(string source, string destination) => Connected().RenameFile(source, destination);
    public void AppendAllText(string path, string text)
    {
        using var stream = Connected().Open(path, FileMode.Append, FileAccess.Write);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    public void Dispose() => client.Dispose();
}
