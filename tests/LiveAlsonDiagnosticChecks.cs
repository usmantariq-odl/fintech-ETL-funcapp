using System.Data.SqlClient;
using System.Text.Json;
using FintechEtl.FunctionApp.Alson;

internal static class LiveAlsonDiagnosticChecks
{
    internal static void Run()
    {
        using(var j=JsonDocument.Parse(File.ReadAllText("alson.local.json")))
            foreach(var v in j.RootElement.GetProperty("Values").EnumerateObject())Environment.SetEnvironmentVariable(v.Name,v.Value.GetString());
        Environment.SetEnvironmentVariable("AlsonEtlEnabled","true");Environment.SetEnvironmentVariable("AlsonEtlDryRun","true");Environment.SetEnvironmentVariable("AlsonErrorEmailEnabled","false");
        var store=new AlsonStore(AlsonSettings.Load());store.VerifySchema();using var c=store.Open();var batch=Guid.NewGuid();
        object Sql(string text){using var cmd=new SqlCommand(text,c);cmd.Parameters.AddWithValue("@batch",batch);return cmd.ExecuteScalar();}
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS live diagnostics: "+message);}
        string Counts()=>Convert.ToString(Sql("SELECT CONCAT((SELECT COUNT_BIG(*) FROM dbo.Fintech_Transactions WHERE Site_ID IN(1191,1192)),':',(SELECT COUNT_BIG(*) FROM dbo.Fintech_TankLevels WHERE Site_ID IN(1191,1192)))"));
        var before=Counts();store.BeginBatch(batch);
        AlsonStore.Result Save(string raw,string id,string suffix,string time="2026-11-07T12:00:00Z")
        {
            var body=JsonSerializer.SerializeToUtf8Bytes(new{rawFrame=raw,messageId=batch+"-"+id,gatewayTs=time});
            return store.Process("local-validation/diagnostics-"+batch+"-"+suffix,"test",body,AlsonParser.Parse(body),batch);
        }
        try
        {
            var raw="#70,81C3C8CE581,70,P,Q,Alive|";
            Check(Save(raw,"one","one").Rows==2,"one receipt creates both dispenser channel events");
            Check(Save(raw,"two","two","2026-11-07T12:01:00Z").Rows==2,"identical later heartbeat retained as another receipt");
            Check(Save(raw,"one","replay").Outcome=="duplicate","same message under another filename deduplicated");
            try{Save("#70,81C3C8CE581,70,P,Q,Changed|","one","changed");throw new Exception("Changed receipt accepted");}
            catch(InvalidOperationException e)when(e.Message.Contains("reused")){}
            Check(Convert.ToInt32(Sql("SELECT COUNT(*) FROM dbo.Alson_Fintech_DspConnectionEvents e JOIN dbo.Alson_Fintech_DiagnosticReceipts r ON r.ReceiptKey=e.ReceiptKey WHERE r.BatchID=@batch AND e.PumpMappingID IN(SELECT ID FROM dbo.Omni_Fintech_PumpMapping WHERE Omni_SiteID=1192 AND FC_PumpID IN(17,18))"))==4,"P/Q map to existing channels 17/18 without hex guessing");
            Check(Save("#70,81C3C8CE581,70,UNKNOWN,Alive|","unknown","unknown").Rows==1,"unmapped channel retained for investigation");
            Check(Convert.ToInt32(Sql("SELECT COUNT(*) FROM dbo.Alson_Fintech_DspConnectionEvents e JOIN dbo.Alson_Fintech_DiagnosticReceipts r ON r.ReceiptKey=e.ReceiptKey WHERE r.BatchID=@batch AND e.RawDispenserCode='UNKNOWN' AND e.PumpMappingID IS NULL"))==1,"unmapped status does not fabricate equipment mapping");
            Save("#31,81C3C8CBD19,31,0002,A,1,1,10:01:32,2026-11-07|","manual","manual");
            Save("#13,81C3C8CD7C3,con|","atg","atg");
            Save("#27,81C3C8CD7C3,24696,STAT,0|","connected","connected");
            Save("#27,81C3C8CD7C3,24696,STAT,2|","disconnected","disconnected","2026-11-07T12:02:00Z");
            Save("#27,81C3C8CD7C3,24696,STAT,9|","other","other","2026-11-07T12:03:00Z");
            Check(Convert.ToInt32(Sql("SELECT COUNT(*) FROM dbo.Alson_Fintech_ProbeStatusEvents e JOIN dbo.Alson_Fintech_DiagnosticReceipts r ON r.ReceiptKey=e.ReceiptKey WHERE r.BatchID=@batch AND ((e.StatusValue='0' AND e.ConnectionState='Connected') OR (e.StatusValue='2' AND e.ConnectionState='Disconnected') OR (e.StatusValue='9' AND e.ConnectionState IS NULL))"))==3,"probe 0 connected, 2 disconnected; unknown status remains unclassified");
            Check(Convert.ToInt32(Sql("SELECT COUNT(*) FROM dbo.Alson_Fintech_AutoManualEvents e JOIN dbo.Alson_Fintech_DiagnosticReceipts r ON r.ReceiptKey=e.ReceiptKey WHERE r.BatchID=@batch AND e.ModeName='Manual' AND e.SiteID=1191 AND e.PumpMappingID IS NOT NULL"))==1,"manual mode linked to correct master/site/channel");
            Check(Convert.ToInt32(Sql("SELECT COUNT(*) FROM dbo.Alson_Fintech_DiagnosticReceipts WHERE BatchID=@batch"))==8,"receipt mutation and replay leave no extra diagnostic rows");
            Check(Counts()==before,"diagnostics leave transaction and tank-reading counts unchanged");
        }
        finally
        {
            Sql("""
SET XACT_ABORT ON;BEGIN TRANSACTION;
DELETE e FROM dbo.Alson_Fintech_AutoManualEvents e JOIN dbo.Alson_Fintech_DiagnosticReceipts r ON r.ReceiptKey=e.ReceiptKey WHERE r.BatchID=@batch;
DELETE e FROM dbo.Alson_Fintech_DspConnectionEvents e JOIN dbo.Alson_Fintech_DiagnosticReceipts r ON r.ReceiptKey=e.ReceiptKey WHERE r.BatchID=@batch;
DELETE e FROM dbo.Alson_Fintech_AtgConnectionEvents e JOIN dbo.Alson_Fintech_DiagnosticReceipts r ON r.ReceiptKey=e.ReceiptKey WHERE r.BatchID=@batch;
DELETE e FROM dbo.Alson_Fintech_ProbeStatusEvents e JOIN dbo.Alson_Fintech_DiagnosticReceipts r ON r.ReceiptKey=e.ReceiptKey WHERE r.BatchID=@batch;
DELETE dbo.Alson_Fintech_DiagnosticReceipts WHERE BatchID=@batch;
DELETE dbo.Alson_Fintech_Imports WHERE BatchID=@batch;
DELETE dbo.Alson_Fintech_Batches WHERE BatchID=@batch;
COMMIT;SELECT 1;
"""
            );
        }
    }
}

