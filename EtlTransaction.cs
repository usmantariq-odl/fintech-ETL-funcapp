using System.Data.SqlClient;
using System.Transactions;

namespace CSVReader;

internal static class EtlTransaction
{
    // Sequential connections with the same connection string reuse the enlisted SQL session.
    // Keep one CSV's writes and its completion checkpoint in the same transaction.
    public static TransactionScope Begin() => new(TransactionScopeOption.Required,
        new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted, Timeout = TimeSpan.FromMinutes(3) },
        TransactionScopeAsyncFlowOption.Enabled);

    public static void VerifyConnectionReuse(string connectionString)
    {
        using var transaction = Begin();
        for (int i = 0; i < 2; i++)
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using var command = new SqlCommand("SELECT 1", connection);
            command.ExecuteScalar();
        }
        if (Transaction.Current.TransactionInformation.DistributedIdentifier != Guid.Empty)
            throw new InvalidOperationException("ETL SQL connections promoted to a distributed transaction.");
        transaction.Complete();
        Console.WriteLine("Sequential SQL connection enlistment verified without distributed transaction promotion (read only).");
    }
}
