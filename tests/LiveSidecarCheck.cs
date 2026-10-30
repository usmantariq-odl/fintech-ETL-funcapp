using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FintechAutoSiteConfiguration.FunctionApp.Storage;
using Renci.SshNet;

internal static class LiveSidecarCheck
{
    internal static void Run()
    {
        using var json = JsonDocument.Parse(File.ReadAllText("local.settings.json"));
        var values = json.RootElement.GetProperty("Values");
        string Value(string key) => values.GetProperty(key).GetString()!;
        var settings = new SftpConnectionSettings(Value("SftpHost"), int.Parse(Value("SftpPort")),
            Value("ConSftpUsername"), Value("SftpPassword"), Value("SftpHostKeySha256").Split(';'));
        var path = "/Unprocessed/.sidecar-probe-" + Guid.NewGuid().ToString("N") + ".txt";
        using var remote = new SftpRemoteFiles(settings);
        try
        {
            remote.AppendAllText(path, "first error\n");
            remote.AppendAllText(path, "second error\n");
            using var bytes = new MemoryStream();
            remote.Download(path, bytes);
            if (Encoding.UTF8.GetString(bytes.ToArray()) != "first error\nsecond error\n")
                throw new Exception("Sidecar content was missing or overwritten.");
            Console.WriteLine("PASS live SSH.NET sidecar creation, append and exact content; no SQL or email calls.");
        }
        finally
        {
            using var cleanup = new SftpClient(settings.Host, settings.Port, settings.Username, settings.Password);
            cleanup.HostKeyReceived += (_, e) => e.CanTrust = settings.HostKeys.Any(expected =>
                expected.Trim().TrimEnd('=') == "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('='));
            cleanup.Connect();
            if (cleanup.Exists(path)) cleanup.DeleteFile(path);
            Console.WriteLine("Temporary sidecar probe removed.");
        }
    }
}
