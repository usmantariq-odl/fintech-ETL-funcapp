using System.Data.SqlClient;

namespace CSVReader;

internal sealed class EtlSettings
{
    public string Host { get; private init; }
    public int Port { get; private init; }
    public string Username { get; private init; }
    public string Password { get; private init; }
    public string HostKeySha256 { get; private init; }
    public bool DryRun { get; private init; }
    public int WorkerCount { get; private init; }
    public string SqlConnectionString { get; private init; }

    public static EtlSettings Load()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing required app setting {name}.");
        var dryRun = bool.TryParse(Environment.GetEnvironmentVariable("EtlDryRun"), out var dry) && dry;
        var sql = Environment.GetEnvironmentVariable("FintechSqlConnectionString") ?? string.Empty;
        if (!dryRun && string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("FintechSqlConnectionString is required for live processing.");
        if (!string.IsNullOrWhiteSpace(sql) && !string.Equals(new SqlConnectionStringBuilder(sql).InitialCatalog, "FintechStressTesting", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FintechSqlConnectionString must target FintechStressTesting. No ZIPs were processed.");
        var workerValue = Environment.GetEnvironmentVariable("EtlWorkerCount");
        var workerCount = string.IsNullOrWhiteSpace(workerValue) ? 4
            : int.TryParse(workerValue, out var configuredWorkers) && configuredWorkers is >= 1 and <= 32
                ? configuredWorkers
                : throw new InvalidOperationException("EtlWorkerCount must be an integer from 1 to 32.");

        return new EtlSettings
        {
            Host = Environment.GetEnvironmentVariable("SftpHost") ?? "fintechsftpdata.blob.core.windows.net",
            Port = int.TryParse(Environment.GetEnvironmentVariable("SftpPort"), out var port) ? port : 22,
            Username = Environment.GetEnvironmentVariable("SftpUsername") ?? "fintechsftpdata.sftpadmin",
            Password = Required("SftpPassword"),
            HostKeySha256 = Required("SftpHostKeySha256"),
            SqlConnectionString = sql,
            DryRun = dryRun,
            WorkerCount = workerCount
        };
    }
}
