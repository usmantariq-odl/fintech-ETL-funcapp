using Microsoft.Extensions.Configuration;

namespace FintechAutoSiteConfiguration.FunctionApp.Storage;

public sealed record SftpConnectionSettings(string Host, int Port, string Username, string Password, string[] HostKeys)
{
    public static SftpConnectionSettings Load(IConfiguration configuration)
    {
        string Required(string name) => !string.IsNullOrWhiteSpace(configuration[name])
            ? configuration[name]! : throw new InvalidOperationException($"Missing required app setting {name}.");
        var port = int.TryParse(configuration["SftpPort"] ?? "22", out var parsedPort) && parsedPort is > 0 and <= 65535
            ? parsedPort : throw new InvalidOperationException("SftpPort must be between 1 and 65535.");
        var keys = Required("SftpHostKeySha256").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0 || keys.Any(key => !key.StartsWith("SHA256:", StringComparison.Ordinal) || !IsSha256(key[7..])))
            throw new InvalidOperationException("SftpHostKeySha256 must contain verified SHA256 fingerprints separated by semicolons.");
        return new(Required("SftpHost"), port, Required("ConSftpUsername"), Required("SftpPassword"), keys);
    }

    public static string NormalizeRemotePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (!normalized.StartsWith('/') || normalized.Length == 0 ||
            normalized.Split('/').Any(segment => segment is "." or "..") || normalized.Any(char.IsControl))
            throw new ArgumentException("SFTP paths must be absolute container paths without dot segments.", nameof(path));
        return normalized;
    }

    private static bool IsSha256(string value)
    {
        try { return Convert.FromBase64String(value.TrimEnd('=') + "=").Length == 32; }
        catch (FormatException) { return false; }
    }
}
