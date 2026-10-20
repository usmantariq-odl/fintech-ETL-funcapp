using System.Data;
using System.Data.SqlClient;

namespace CSVReader;

// Session-owned SQL locks also coordinate separate local/function instances using this version.
internal sealed class SiteLease : IDisposable
{
    private readonly SqlConnection connection;
    private SiteLease(SqlConnection connection) => this.connection = connection;
    public static SiteLease TryAcquire(string connectionString, string site)
        => TryAcquireResource(connectionString, "FintechEtl:site:" + int.Parse(site).ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static SiteLease TryAcquireResource(string connectionString, string resource)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString)
        { Pooling = false, ConnectRetryCount = 0 }.ConnectionString);
        try
        {
            connection.Open();
            using var command = new SqlCommand("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource, 'Exclusive', 'Session', 0; SELECT @result;", connection);
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
            if (Convert.ToInt32(command.ExecuteScalar()) >= 0) return new SiteLease(connection);
            connection.Dispose();
            return null;
        }
        catch { connection.Dispose(); throw; }
    }
    public void Verify()
    {
        // No automatic reconnect: a lost session means its lock is lost too.
        if (connection.State != ConnectionState.Open) throw new RetryableEtlException("Site lock connection was lost.");
        using var command = new SqlCommand("SELECT 1", connection);
        command.ExecuteScalar();
    }
    public void Dispose()
    {
        connection.Dispose();
    }
}
