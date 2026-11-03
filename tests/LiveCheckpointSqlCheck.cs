using CSVReader;
using System.Data;
using System.Data.SqlClient;
using System.Text.Json;
using System.Transactions;

// Explicitly invoked only; all SQL writes target session-local temporary tables.
internal static class LiveCheckpointSqlCheck
{
    public static void Run()
    {
        var connectionString = Environment.GetEnvironmentVariable("FintechSqlConnectionString");
        if (string.IsNullOrEmpty(connectionString))
        {
            using var local = JsonDocument.Parse(File.ReadAllText("local.settings.json"));
            if (!local.RootElement.GetProperty("Values").TryGetProperty("FintechSqlConnectionString", out var configured))
                throw new InvalidOperationException("Set FintechSqlConnectionString for the temporary-table SQL check; the local settings file does not contain it.");
            connectionString = configured.GetString();
        }
        var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 15 };
        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        var suffix = Guid.NewGuid().ToString("N");
        var table = "#EtlCheckpoint_" + suffix;
        using var schemaStream = typeof(SqlFileCheckpoints).Assembly.GetManifestResourceStream("FintechEtl.FunctionApp.deployment.Create-EtlFileCheckpoints.sql")!;
        using var reader = new StreamReader(schemaStream);
        var schema = reader.ReadToEnd().Replace("dbo.Fintech_EtlFileCheckpoints", table)
            .Replace("OBJECT_ID(N'" + table, "OBJECT_ID(N'tempdb.." + table)
            .Replace("PK_Fintech_EtlFileCheckpoints", "PK_EtlCheckpoint_" + suffix)
            .Replace("FintechEtl:checkpoint-schema", "FintechEtl:temporary-checkpoint-test:" + suffix);
        // Exercise the production schema twice to check idempotency, without creating a persistent table.
        for (int i = 0; i < 2; i++)
        {
            using var create = new SqlCommand(schema, connection);
            create.ExecuteNonQuery();
        }
        const string key = "/Unprocessed/007001/input/archive.tgz!/nested/PSL.csv";
        void Write(string remote, string status, string contentHash = null)
        {
            using var command = new SqlCommand(SqlFileCheckpoints.WriteSql.Replace("dbo.Fintech_EtlFileCheckpoints", table), connection);
            SqlFileCheckpoints.AddKey(command, remote, contentHash);
            command.Parameters.Add("@Status", SqlDbType.VarChar, 50).Value = status;
            command.Parameters.Add("@Error", SqlDbType.NVarChar, -1).Value = DBNull.Value;
            command.ExecuteNonQuery();
        }
        string Read(string remote, string contentHash = null)
        {
            using var command = new SqlCommand(SqlFileCheckpoints.ReadSql.Replace("dbo.Fintech_EtlFileCheckpoints", table), connection);
            SqlFileCheckpoints.AddKey(command, remote, contentHash);
            return command.ExecuteScalar() as string;
        }
        using (var transaction = EtlTransaction.Begin())
        {
            connection.EnlistTransaction(Transaction.Current);
            Write(key, "Retry");
            Write(key, "Processed");
            transaction.Complete();
        }
        if (Read(key.Replace("/Unprocessed", "")) != "Processed") throw new Exception("Committed checkpoint or legacy key lookup failed.");
        using (var transaction = EtlTransaction.Begin())
        {
            connection.EnlistTransaction(Transaction.Current);
            Write(key, "Failed");
            Write(key + "-rollback", "Processed");
            // Deliberately do not complete: neither an update nor an insert may survive.
        }
        if (Read(key) != "Processed" || Read(key + "-rollback") != null) throw new Exception("SQL checkpoint rollback failed.");
        using var count = new SqlCommand("SELECT COUNT(*) FROM " + table, connection);
        if (Convert.ToInt32(count.ExecuteScalar()) != 1) throw new Exception("Checkpoint upsert produced duplicate rows.");
        var firstHash = new string('a', 64); var secondHash = new string('b', 64);
        using (var transaction = EtlTransaction.Begin())
        {
            connection.EnlistTransaction(Transaction.Current);
            Write(key, "Processed", firstHash); Write(key, "Failed", secondHash);
            transaction.Complete();
        }
        if (Read(key, firstHash.ToUpperInvariant()) != "Processed" || Read(key, secondHash) != "Failed" || Read(key) != "Processed")
            throw new Exception("Content versions were conflated with each other or legacy history.");
        using (var transaction = EtlTransaction.Begin())
        {
            connection.EnlistTransaction(Transaction.Current);
            Write(key, "Processed", secondHash);
        }
        if (Read(key, secondHash) != "Failed") throw new Exception("Content checkpoint rollback failed.");
        if (Convert.ToInt32(count.ExecuteScalar()) != 3) throw new Exception("Content checkpoint versions were not stored separately.");
        Console.WriteLine("PASS SQL checkpoint schema/idempotency, content versions, insert/update, canonical lookup, ambient commit and rollback. Only a session-local temporary table was written; no application rows or persistent schema changed.");
    }
}
