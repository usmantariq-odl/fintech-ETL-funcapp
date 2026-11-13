using System.Data.SqlClient;
using System.Text.Json;
using FintechEtl.FunctionApp.Alson;

internal static class LiveAlsonFailureAuditChecks
{
    internal static void Run(string settingsPath)
    {
        using(var j=JsonDocument.Parse(File.ReadAllText(settingsPath)))
            foreach(var v in j.RootElement.GetProperty("Values").EnumerateObject())Environment.SetEnvironmentVariable(v.Name,v.Value.GetString());
        Environment.SetEnvironmentVariable("AlsonEtlEnabled","true");Environment.SetEnvironmentVariable("AlsonEtlDryRun","true");Environment.SetEnvironmentVariable("AlsonErrorEmailEnabled","false");
        var store=new AlsonStore(AlsonSettings.Load());store.VerifySchema();
        var batch=Guid.NewGuid();var name="local-validation/failure-audit-"+batch;
        var body=AlsonChecks.Envelope("#20,2147483002,81C3C8CD7C3,12:00:00,2026-11-07~24696,966403078,5,32.25~1|");
        using var c=store.Open();
        int Count(string text)
        {
            using var q=new SqlCommand(text,c);q.Parameters.AddWithValue("@batch",batch);return Convert.ToInt32(q.ExecuteScalar());
        }
        store.BeginBatch(batch);
        try
        {
            try{store.Validate(name,"validation",body,AlsonParser.Parse(body));throw new Exception("Invalid fuel value was accepted.");}
            catch(FormatException ex) when(ex.Message.Contains("physical limits")){}
            store.RecordFailure(name,"validation",body,"invalid","Fuel outside configured physical limits.",batch);
            store.RecordFailure(name,"validation",body,"invalid","Repeated synthetic failure.",batch);
            if(Count("SELECT COUNT(*) FROM dbo.Alson_Fintech_Imports WHERE BatchID=@batch AND Outcome='invalid' AND FrameHash IS NULL AND MasterId='81C3C8CD7C3' AND CommandCode='20'")!=1)
                throw new Exception("Failure audit NULL hash/master identity/retry incorrect.");
            if(Count("SELECT COUNT(*) FROM dbo.Alson_Fintech_Frames WHERE BatchID=@batch")!=0)throw new Exception("Invalid frame reserved a financial hash.");
            Console.WriteLine("PASS live SQL: invalid ATG rejected, NULL binary hash audit committed with master/command, retry audited once, no financial frames or target inserts.");
        }
        finally
        {
            using var q=new SqlCommand("SET XACT_ABORT ON;BEGIN TRANSACTION;DELETE dbo.Alson_Fintech_Imports WHERE BatchID=@batch AND BlobName=@name;DELETE dbo.Alson_Fintech_Batches WHERE BatchID=@batch;COMMIT;",c);
            q.Parameters.AddWithValue("@batch",batch);q.Parameters.AddWithValue("@name",name);q.ExecuteNonQuery();
            Console.WriteLine("Synthetic audit/batch cleaned; no business data, blob moves or mail.");
        }
    }
}
