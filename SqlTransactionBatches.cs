using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Transactions;

namespace CSVReader;

internal sealed record SqlWriteOptions(int BatchSize, int CommandTimeoutSeconds, int CsvTransactionTimeoutSeconds)
{
    public static SqlWriteOptions Load() => new(
        Read("EtlSqlBatchSize", 1000, 1, 5000),
        Read("EtlSqlCommandTimeoutSeconds", 300, 1, 300),
        Read("EtlCsvTransactionTimeoutSeconds", 1200, 30, 1200));

    private static int Read(string name, int fallback, int min, int max)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < min || parsed > max)
            throw new InvalidOperationException($"{name} must be an integer from {min} to {max}.");
        return parsed;
    }
}

// Bounds each TVP/RPC without committing a CSV piecemeal. The caller owns the
// ambient transaction, including its content checkpoint; a failed batch must escape.
internal static class SqlTransactionBatches
{
    public static void Execute(DataTable source, int? processedFileId, SqlWriteOptions options,
        Action<DataTable, int?> writeBatch, Action<int, int> progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (options.BatchSize is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(options));
        for (int offset = 0; offset < source.Rows.Count; offset += options.BatchSize)
        {
            Transaction.Current?.TransactionInformation.Status.ThrowIfAborted();
            using var batch = source.Clone();
            var count = Math.Min(options.BatchSize, source.Rows.Count - offset);
            for (int i = 0; i < count; i++) batch.ImportRow(source.Rows[offset + i]);
            BoundStringMetadata(batch);
            writeBatch(batch, processedFileId);
            progress?.Invoke(offset + count, source.Rows.Count);
        }
    }

    // CSV columns start as strings with MaxLength=-1. Sending short values as
    // nvarchar(max) adds LOB overhead to every row. Use the actual maximum per
    // batch; retain MAX for genuinely long fields and never truncate any value.
    private static void BoundStringMetadata(DataTable table)
    {
        foreach (DataColumn column in table.Columns)
        {
            if (column.DataType != typeof(string)) continue;
            int length = 1;
            foreach (DataRow row in table.Rows)
                if (row[column] is string text) length = Math.Max(length, text.Length);
            column.MaxLength = length <= 4000 ? length : -1;
        }
    }

    public static SqlCommand CreateCommand(SqlConnection connection, DataTable batch, int? processedFileId, SqlWriteOptions options)
    {
        var command = new SqlCommand("dbo.InsertintoFintechTransactions", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = options.CommandTimeoutSeconds
        };
        command.Parameters.Add("@TransactionsTable", SqlDbType.Structured).Value = batch;
        command.Parameters["@TransactionsTable"].TypeName = "dbo.TransactionsTableType";
        command.Parameters.Add("@ProcessedFileID", SqlDbType.Int).Value = processedFileId.HasValue ? processedFileId.Value : DBNull.Value;
        return command;
    }

    private static void ThrowIfAborted(this TransactionStatus status)
    {
        if (status != TransactionStatus.Active) throw new TransactionAbortedException("CSV transaction is no longer active; later SQL batches were not sent.");
    }
}
