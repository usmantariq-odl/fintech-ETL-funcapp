using System.Data.SqlClient;
using System.Transactions;
using CSVReader;
using Renci.SshNet.Common;

namespace FintechEtl.FunctionApp;

internal static class EtlFailurePolicy
{
    // Deterministic payload failures cannot be repaired by retrying the same CSV.
    internal static bool IsPermanentSqlDataError(int number) => number is
        2628 or 8152 or 245 or 241 or 242 or 8114 or 8115 or 8169 or 515 or 547;

    internal static bool IsRetryableSql(SqlException error) =>
        !error.Errors.Cast<SqlError>().Any(item => IsPermanentSqlDataError(item.Number));

    internal static bool ShouldEmail(Exception error)
    {
        if (error is AggregateException aggregate) return aggregate.Flatten().InnerExceptions.Any(ShouldEmail);
        if (error is SqlException sql) return !IsRetryableSql(sql)
            || sql.Errors.Cast<SqlError>().Any(item => item.Number is 18456 or 18452 or 229 or 916 or 4060 or 102 or 156 or 207 or 208 or 2812 or 201 or 8144 or 8145);
        if (error is SshAuthenticationException or UnauthorizedAccessException) return true;
        if (error is RetryableEtlException or TransactionException)
            return error.InnerException != null && ShouldEmail(error.InnerException);
        if (error is OperationCanceledException or TimeoutException or IOException or SshException) return false;
        return true; // Configuration, validation and unexpected application errors require intervention.
    }
}
