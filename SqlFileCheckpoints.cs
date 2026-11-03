using System.Data;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;

namespace CSVReader;

// Auxiliary progress belongs here, never in the archive audit table. Commands enlist
// in the CSV's ambient transaction so data and its success checkpoint commit together.
internal sealed class SqlFileCheckpoints(string connectionString)
{
    internal const string ReadSql = "SELECT Status FROM dbo.Fintech_EtlFileCheckpoints WHERE KeyHash = @KeyHash AND RemoteKey = @RemoteKey";
    internal const string WriteSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        IF EXISTS (SELECT 1 FROM dbo.Fintech_EtlFileCheckpoints WITH (UPDLOCK, HOLDLOCK) WHERE KeyHash = @KeyHash)
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.Fintech_EtlFileCheckpoints WHERE KeyHash = @KeyHash AND RemoteKey <> @RemoteKey)
                THROW 51001, 'ETL checkpoint key hash collision.', 1;
            UPDATE dbo.Fintech_EtlFileCheckpoints
            SET Status = @Status, ErrorMessage = @Error, UpdatedOn = SYSUTCDATETIME()
            WHERE KeyHash = @KeyHash;
        END
        ELSE
            INSERT dbo.Fintech_EtlFileCheckpoints (KeyHash, RemoteKey, Status, ErrorMessage, UpdatedOn)
            VALUES (@KeyHash, @RemoteKey, @Status, @Error, SYSUTCDATETIME());
        COMMIT TRANSACTION;
        """;

    internal static string CanonicalKey(string key)
    {
        var normalized = key.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && SftpLayout.IsSiteId(parts[0]) && parts[1] == "input")
            normalized = SftpLayout.UnprocessedRoot + "/" + normalized.TrimStart('/');
        if (normalized.Length > 1280) throw new InvalidDataException("ETL checkpoint key exceeds 1280 characters.");
        return normalized;
    }

    internal static void AddKey(SqlCommand command, string key, string contentHash = null)
    {
        var canonical = CanonicalKey(key);
        if (contentHash != null && (contentHash.Length != 64 || contentHash.Any(c => !Uri.IsHexDigit(c))))
            throw new InvalidDataException("ETL checkpoint content hash must be a SHA-256 hex digest.");
        // Preserve the schema and readable remote key. A payload version has its own
        // primary key; path-only history can never mark different bytes as complete.
        var identity = contentHash == null ? canonical : canonical + "\0sha256:" + contentHash.ToLowerInvariant();
        command.Parameters.Add("@KeyHash", SqlDbType.Binary, 32).Value = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        command.Parameters.Add("@RemoteKey", SqlDbType.NVarChar, 1280).Value = canonical;
    }

    public string Status(string key, string contentHash = null)
    {
        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand(ReadSql, connection);
        AddKey(command, key, contentHash);
        connection.Open();
        return command.ExecuteScalar() as string;
    }

    public void SetStatus(string key, string status, string error, string contentHash = null)
    {
        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand(WriteSql, connection);
        AddKey(command, key, contentHash);
        command.Parameters.Add("@Status", SqlDbType.VarChar, 50).Value = status;
        command.Parameters.Add("@Error", SqlDbType.NVarChar, -1).Value = (object)error ?? DBNull.Value;
        connection.Open();
        command.ExecuteNonQuery();
    }

    public static void EnsureCreated(string connectionString)
    {
        using var stream = typeof(SqlFileCheckpoints).Assembly.GetManifestResourceStream(
            "FintechEtl.FunctionApp.deployment.Create-EtlFileCheckpoints.sql")
            ?? throw new InvalidOperationException("The ETL checkpoint schema resource is missing.");
        using var reader = new StreamReader(stream);
        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand(reader.ReadToEnd(), connection);
        connection.Open();
        try { command.ExecuteNonQuery(); }
        catch (SqlException ex) when (ex.Number is 229 or 262 or 2760)
        {
            throw new InvalidOperationException("Create dbo.Fintech_EtlFileCheckpoints using deployment/Create-EtlFileCheckpoints.sql and grant the ETL identity SELECT/INSERT/UPDATE on it, then retry.", ex);
        }
    }
}
