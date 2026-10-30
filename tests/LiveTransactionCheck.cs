using Microsoft.Data.SqlClient;
using System.Text.Json;

// Opt-in SQL integration check: session-local temporary tables only. It never
// executes the site's replacement SQL or changes any application tables.
internal static class LiveTransactionCheck
{
    internal static async Task RunAsync()
    {
        using var json = JsonDocument.Parse(File.ReadAllText("local.settings.json"));
        var value = json.RootElement.GetProperty("Values").GetProperty("ApplicationSettings__AzureSql").GetString();
        await using var connection = new SqlConnection(value);
        await connection.OpenAsync();
        async Task Execute(string text, SqlTransaction? transaction = null)
        {
            await using var command = new SqlCommand(text, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
        async Task<string> Read()
        {
            await using var command = new SqlCommand("SELECT Name + ':' + CAST(IsActive AS varchar(1)) FROM #AutoSiteRollbackProbe", connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }
        await Execute("CREATE TABLE #AutoSiteRollbackProbe(Name nvarchar(30), IsActive bit); INSERT #AutoSiteRollbackProbe VALUES ('working', 1)");
        try
        {
            await SqlConfigurationTransaction.RunAsync(connection, async transaction =>
            {
                await Execute("DELETE #AutoSiteRollbackProbe; INSERT #AutoSiteRollbackProbe VALUES ('partial replacement', 0)", transaction);
                throw new InvalidOperationException("Expected test failure after replacement started.");
            });
            throw new Exception("Expected replacement failure was swallowed.");
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("Expected test failure")) { }
        if (await Read() != "working:1") throw new Exception("SQL did not roll back configuration and activation together.");
        await SqlConfigurationTransaction.RunAsync(connection, transaction => Execute("UPDATE #AutoSiteRollbackProbe SET Name = 'validated replacement'", transaction));
        if (await Read() != "validated replacement:1") throw new Exception("Valid replacement did not commit.");
        Console.WriteLine("PASS real SQL rollback restores configuration/activation and valid replacement commits; session-local temporary table only.");
    }
}
