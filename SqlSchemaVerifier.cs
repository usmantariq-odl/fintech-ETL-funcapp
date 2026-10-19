using System.Data;
using System.Data.SqlClient;

namespace CSVReader;

internal static class SqlSchemaVerifier
{
    public static void Verify(string connectionString)
    {
        string[] tables = ["Fintech_ProcessedFiles", "Fintech_Transactions", "Fintech_transactionsType", "Fintech_TankLevels",
            "Fintech_TankDelivery", "Fintech_Alarms", "FintechMappedCustomers", "Omni_Fintech_SiteMapping", "Omni_Fintech_TankMapping",
            "Omni_Fintech_PumpMapping", "Plant_Site", "Omni_Fintech_Products", "PriceWritebackRequests", "PriceWritebackStatusHistory"];
        string[] procedures = ["InsertintoFintechTransactions", "InsertintoFintechTransactionsType", "InsertintoFintechTankLevels",
            "InsertintoFintechTanksDelivery", "InsertintoFintechAlarms", "Fintech_Regenerated_Files"];
        var missing = new List<string>();
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using (var database = new SqlCommand("SELECT DB_NAME()", connection)) Console.WriteLine("SQL database: " + database.ExecuteScalar());
        foreach (var (names, kind) in new[] { (tables, "U"), (procedures, "P") })
            foreach (var name in names)
            {
                using var command = new SqlCommand("SELECT OBJECT_ID(@Name, @Type)", connection);
                command.Parameters.Add("@Name", SqlDbType.NVarChar, 256).Value = name;
                command.Parameters.Add("@Type", SqlDbType.VarChar, 2).Value = kind;
                if (command.ExecuteScalar() is DBNull) missing.Add(name);
            }
        foreach (var column in new[] { "Path", "FileName", "FileType", "Status", "FileSize", "ErrorMessage", "CreatedOn", "UpdatedOn" })
        {
            using var command = new SqlCommand("SELECT COL_LENGTH('Fintech_ProcessedFiles', @Column)", connection);
            command.Parameters.Add("@Column", SqlDbType.NVarChar, 128).Value = column;
            if (command.ExecuteScalar() is DBNull) missing.Add("Fintech_ProcessedFiles." + column);
        }
        if (missing.Count > 0) throw new InvalidOperationException("Required SQL objects/columns are missing: " + string.Join(", ", missing));
        Console.WriteLine($"Required SQL objects present: {tables.Length} tables, {procedures.Length} procedures, file-tracking columns. No writes performed.");
        EtlTransaction.VerifyConnectionReuse(connectionString);
    }
}
