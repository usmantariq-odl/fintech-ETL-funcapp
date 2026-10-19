using System.Data.SqlClient;

namespace CSVReader;

internal sealed class EtlSettings
{
    internal const string ExpectedSqlCatalog = "FintechStagDB";
    public string Host { get; private init; }
    public int Port { get; private init; }
    public string Username { get; private init; }
    public string Password { get; private init; }
    public string HostKeySha256 { get; private init; }
    public bool DryRun { get; private init; }
    public int WorkerCount { get; private init; }
    public string SqlConnectionString { get; private init; }
    public string MagLinkRoot { get; private init; }
    public string[] ConDestinations { get; private init; }
    public string ConUsername { get; private init; }
    public int MinimumFileAgeSeconds { get; private init; }

    public static EtlSettings Load()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing required app setting {name}.");
        var dryRunValue = Environment.GetEnvironmentVariable("EtlDryRun");
        if (!string.IsNullOrWhiteSpace(dryRunValue) && !bool.TryParse(dryRunValue, out _))
            throw new InvalidOperationException("EtlDryRun must be true or false.");
        var dryRun = bool.TryParse(dryRunValue, out var dry) && dry;
        var sql = Environment.GetEnvironmentVariable("FintechSqlConnectionString") ?? string.Empty;
        if (!dryRun && string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("FintechSqlConnectionString is required for live processing.");
        if (!string.IsNullOrWhiteSpace(sql) && !string.Equals(new SqlConnectionStringBuilder(sql).InitialCatalog, ExpectedSqlCatalog, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"FintechSqlConnectionString must target {ExpectedSqlCatalog}. No files were processed.");
        if (!string.IsNullOrWhiteSpace(sql) && (!new SqlConnectionStringBuilder(sql).Enlist || !new SqlConnectionStringBuilder(sql).Pooling))
            throw new InvalidOperationException("FintechSqlConnectionString must enable Enlist and Pooling for atomic CSV checkpoints.");
        var workerValue = Environment.GetEnvironmentVariable("EtlWorkerCount");
        var workerCount = string.IsNullOrWhiteSpace(workerValue) ? 4
            : int.TryParse(workerValue, out var configuredWorkers) && configuredWorkers is >= 1 and <= 32
                ? configuredWorkers
                : throw new InvalidOperationException("EtlWorkerCount must be an integer from 1 to 32.");
        string RemoteRoot(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            if (!value.StartsWith('/') || value.Contains('\\') || value.Split('/').Any(p => p is "." or ".."))
                throw new InvalidOperationException("SFTP roots must be absolute paths without dot segments or backslashes.");
            if (value.TrimEnd('/').Length == 0) throw new InvalidOperationException("Integration roots must name a folder below the SFTP root.");
            return value.TrimEnd('/');
        }
        var minimumAge = Environment.GetEnvironmentVariable("EtlMinimumFileAgeSeconds");
        var minimumAgeSeconds = string.IsNullOrWhiteSpace(minimumAge) ? 60
            : int.TryParse(minimumAge, out var age) && age >= 0 ? age
            : throw new InvalidOperationException("EtlMinimumFileAgeSeconds must be a nonnegative integer.");
        var conDestinations = (Environment.GetEnvironmentVariable("ConSftpDestinations") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(RemoteRoot).Distinct(StringComparer.Ordinal).ToArray();
        var conUsername = Environment.GetEnvironmentVariable("ConSftpUsername");
        if (conDestinations.Length > 0 && string.IsNullOrWhiteSpace(conUsername))
            throw new InvalidOperationException("ConSftpUsername must explicitly select the CON container when CON destinations are configured.");

        return new EtlSettings
        {
            Host = Environment.GetEnvironmentVariable("SftpHost") ?? "fintechsftpdata.blob.core.windows.net",
            Port = int.TryParse(Environment.GetEnvironmentVariable("SftpPort"), out var port) ? port : 22,
            Username = Environment.GetEnvironmentVariable("SftpUsername") ?? "fintechsftpdata.sftpadmin",
            Password = Required("SftpPassword"),
            HostKeySha256 = Required("SftpHostKeySha256"),
            SqlConnectionString = sql,
            DryRun = dryRun,
            WorkerCount = workerCount,
            MagLinkRoot = RemoteRoot(Environment.GetEnvironmentVariable("MagLinkSftpRoot")),
            ConDestinations = conDestinations,
            ConUsername = conUsername,
            MinimumFileAgeSeconds = minimumAgeSeconds
        };
    }
}
