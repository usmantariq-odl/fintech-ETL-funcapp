using Microsoft.Data.SqlClient;

internal static class SqlConfigurationTransaction
{
    internal static async Task RunAsync(SqlConnection connection, Func<SqlTransaction, Task> action)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await action(transaction);
            await transaction.CommitAsync();
        }
        catch
        {
            // SQL also rolls back an uncommitted transaction on connection loss.
            try { await transaction.RollbackAsync(); }
            catch (Exception rollbackError)
            {
                Console.WriteLine($"Configuration rollback connection unavailable: {rollbackError.GetType().Name}");
            }
            throw;
        }
    }
}
