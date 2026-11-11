using System.Data.SqlClient;
using System.Data;
using System.Text.Json;
using FintechEtl.FunctionApp.Alson;

internal static class LiveAlsonChecks
{
    internal static void Run()
    {
        using(var j=JsonDocument.Parse(File.ReadAllText("alson.local.json")))
            foreach(var v in j.RootElement.GetProperty("Values").EnumerateObject())Environment.SetEnvironmentVariable(v.Name,v.Value.GetString());
        Environment.SetEnvironmentVariable("AlsonEtlEnabled","true");Environment.SetEnvironmentVariable("AlsonEtlDryRun","true");Environment.SetEnvironmentVariable("AlsonErrorEmailEnabled","false");
        var settings=AlsonSettings.Load();var store=new AlsonStore(settings);store.VerifySchema();
        using var c=store.Open();
        object Scalar(string sql,params(string,object)[] values)
        {using var cmd=new SqlCommand(sql,c);foreach(var(k,v)in values)cmd.Parameters.AddWithValue(k,v);return cmd.ExecuteScalar();}
        void Check(bool good,string text){if(!good)throw new Exception(text);Console.WriteLine("PASS live Alson: "+text);}
        var batch=Guid.NewGuid();store.BeginBatch(batch);
        try
        {
            long Before()=>Convert.ToInt64(Scalar("SELECT COUNT_BIG(*) FROM dbo.Fintech_TankLevels WHERE Site_ID IN(1191,1192)"));
            long before=Before();
            string raw="#20,2147483000,81C3C8CD7C3,12:00:00,2026-11-07~24696,1234,5,32.25~NOT_CONFIGURED,2345,6,33.25~1|";
            var body=AlsonChecks.Envelope(raw);var frame=AlsonParser.Parse(body);
            try{store.Process("local-validation/atomic-"+batch,"validation",body,frame,batch);throw new Exception("Missing probe accepted.");}
            catch(AlsonPendingException){}
            Check(Before()==before,"second-probe mapping failure rolls back first target insert");
            Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.Alson_Fintech_Frames WHERE FrameHash=@hash",("@hash",frame.Hash)))==0,"failed multi-probe frame leaves no dedup reservation");
            Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.Alson_Fintech_Rows r JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash WHERE f.BatchID=@batch",("@batch",batch)))==0,"failed frame leaves no output links");
            raw="#20,2147483001,81C3C8CD7C3,12:00:00,2026-11-07~24696,1234,5,32.25~24697,2345,6,100.01~1|";
            body=AlsonChecks.Envelope(raw);frame=AlsonParser.Parse(body);
            try{store.Process("local-validation/temperature-"+batch,"validation",body,frame,batch);throw new Exception("Invalid temperature accepted.");}
            catch(FormatException){}
            Check(Before()==before,"invalid second probe also rolls back the complete frame");
            raw="#20,2147483002,81C3C8CD7C3,12:00:00,2026-11-07~24696,966403078,5,32.25~1|";
            body=AlsonChecks.Envelope(raw);frame=AlsonParser.Parse(body);
            try{store.Process("local-validation/bounds-"+batch,"validation",body,frame,batch);throw new Exception("Extreme reading accepted.");}
            catch(FormatException){}
            Check(Before()==before,"extreme fuel value rejected without business data");
            var count=Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.Alson_Fintech_Frames WHERE CommandCode='00'"));
            if(count>0)
            {
                using(var cmd=new SqlCommand("""
SELECT t.*,f.RawFrame,p.AlsonPumpCode,p.FC_PumpID,p.FC_NozzleID
FROM dbo.Alson_Fintech_Rows r JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash
JOIN dbo.Fintech_Transactions t ON t.ID=r.TargetID AND t.Site_ID=r.SiteID
JOIN dbo.Omni_Fintech_PumpMapping p ON p.ID=r.MappingID
WHERE r.EntityType='TXN' AND r.SiteID IN(1191,1192)
""",c))
                using(var adapter=new SqlDataAdapter(cmd))
                {
                    var table=new DataTable();adapter.Fill(table);
                    Check(table.Rows.Count==100,"exactly 100 sales linked to the new sites");
                    Check(table.AsEnumerable().GroupBy(r=>r.Field<int>("Site_ID")).All(g=>g.Count()==50),"50 sales per site");
                    foreach(DataRow row in table.Rows)
                    {
                        var saleReading=AlsonParser.Parse(AlsonChecks.Envelope((string)row["RawFrame"]));
                        if(Math.Abs(Convert.ToDouble(row["Volume"])-(double)saleReading.Volume.Value)>1e-8||
                           Math.Abs(Convert.ToDouble(row["Money"])-(double)saleReading.Amount.Value)>1e-8||
                           Math.Abs(Convert.ToDouble(row["PPU"])-(double)saleReading.Price.Value)>1e-8||
                           Convert.ToInt32(row["Sale_ID"])!=int.Parse(saleReading.RecordId)||
                           (string)row["AlsonPumpCode"]!=saleReading.Dispenser||
                           Convert.ToInt32(row["Pump_ID"])!=Convert.ToInt32(row["FC_PumpID"])||
                           Convert.ToInt32(row["Hose_ID"])!=saleReading.Nozzle||
                           (DateTime)row["FC_EndDateTime"]!=saleReading.At)
                            throw new Exception("Sale differs from original frame/mapping.");
                        int grade=saleReading.Product switch{"R"=>1,"D"=>2,"H"=>3,_=>throw new Exception("Unknown product")};
                        if(Convert.ToInt32(row["Grade_ID"])!=grade)throw new Exception("Wrong product grade.");
                        foreach(var name in new[]{"Start_Date","Start_Time","FC_StartDateTime","Final_Volume","Initial_Volume","Payment_Type","Sale_Type","Sale_Auth","Authorizer","Preset_Amount","Level","UnitPrice","IsManualSite","ProcessedFileID"})
                            if(!row.IsNull(name))throw new Exception("Unsupported sale field fabricated: "+name);
                    }
                    Check(true,"all 100 sale values, product IDs, source timestamps and unsupported NULL fields verified");
                }
                using(var cmd=new SqlCommand("""
SELECT t.*,f.RawFrame,q.ProbeCode FROM dbo.Alson_Fintech_Rows r
JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash
JOIN dbo.Fintech_TankLevels t ON t.ID=r.TargetID AND t.Site_ID=r.SiteID
JOIN dbo.Alson_Fintech_ProbeConfig q ON q.TankMappingID=r.MappingID
WHERE r.EntityType='ATG' AND r.SiteID IN(1191,1192)
""",c))
                using(var adapter=new SqlDataAdapter(cmd))
                {
                    var table=new DataTable();adapter.Fill(table);
                    Check(table.Rows.Count==100,"exactly 100 tank readings linked to the new sites");
                    foreach(DataRow row in table.Rows)
                    {
                        var tankFrame=AlsonParser.Parse(AlsonChecks.Envelope((string)row["RawFrame"]));
                        var probe=tankFrame.Probes.Single(p=>p.Code==(string)row["ProbeCode"]);
                        if(Math.Abs(Convert.ToDouble(row["Product_Volume"])-(double)probe.Fuel)>1e-8||
                           Math.Abs(Convert.ToDouble(row["Water_Volume"])-(double)probe.Water)>1e-8||
                           Math.Abs(Convert.ToDouble(row["Product_Temperature"])-(double)probe.Temperature)>1e-8||
                           (DateTime)row["FC_TimeStamp"]!=tankFrame.At||Convert.ToInt32(row["Tank_ID"])!=int.Parse(probe.Code))
                            throw new Exception("ATG differs from original frame/configuration.");
                        foreach(var name in new[]{"Product_Height","Water_Height","Product_Number","Product_ID","Last_Trans_ID","Product_Density","Product_Weight","Ullage","ProcessedFileID","Delivered_Volume"})
                            if(!row.IsNull(name))throw new Exception("Unsupported ATG field fabricated: "+name);
                    }
                    Check(true,"all 100 fuel/water volumes, temperatures, probes, timestamps and unsupported NULL fields verified");
                }
                var original=(byte[])Scalar("SELECT TOP(1) i.Envelope FROM dbo.Alson_Fintech_Imports i JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=i.FrameHash WHERE f.CommandCode='00' AND i.Outcome='imported' ORDER BY i.BlobName");
                var sale=AlsonParser.Parse(original);
                var results=Task.WhenAll(Enumerable.Range(0,4).Select(i=>Task.Run(()=>new AlsonStore(settings).Process("local-validation/duplicate-"+batch+"-"+i,"validation",original,sale,batch)))).GetAwaiter().GetResult();
                Check(results.All(r=>r.Outcome=="duplicate"&&r.Rows==0),"four concurrent duplicate names do not insert the sale again");
                Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.Alson_Fintech_Frames WHERE CommandCode='00'"))==count,"duplicate replay does not change the committed frame count");
                var same=store.Process("local-validation/duplicate-"+batch+"-0","validation",original,sale,batch);
                Check(same.Outcome=="already_seen","same source filename is skipped on retry");
                store.RecordFailure("local-validation/duplicate-"+batch+"-0","validation",original,"pending_mapping","Simulated stale failure arriving after success",batch);
                Check((string)Scalar("SELECT Outcome FROM dbo.Alson_Fintech_Imports WHERE SourceKey=@key",("@key",AlsonStore.SourceKey(settings.Container,"local-validation/duplicate-"+batch+"-0")))=="duplicate","late failure cannot downgrade a successful terminal audit");
                using var doc=JsonDocument.Parse(original);var changed=doc.RootElement.GetProperty("rawFrame").GetString()+" ";
                var mutated=AlsonChecks.Envelope(changed);
                try{store.Process("local-validation/duplicate-"+batch+"-0","different",mutated,AlsonParser.Parse(mutated),batch);throw new Exception("Mutation accepted.");}
                catch(InvalidOperationException ex)when(ex.Message.Contains("changed content")){}
                Check(true,"same filename with changed content preserves original audit and fails");
                var validationBody=AlsonChecks.Envelope("#00,2147483003,81C3C8CBD19,0@,1,R,1,2,2,3,12:00:00,2026-11-07,No|");
                var validationFrame=AlsonParser.Parse(validationBody);
                var inserted=store.Process("local-validation/null-default-"+batch,"validation",validationBody,validationFrame,batch);
                Check(inserted.Rows==1&&Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.Fintech_Transactions t JOIN dbo.Alson_Fintech_Rows r ON r.TargetID=t.ID AND r.SiteID=t.Site_ID JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash WHERE f.BatchID=@batch AND r.EntityType='TXN' AND t.IsManualSite IS NULL",("@batch",batch)))==1,"new runtime inserts explicitly keep IsManualSite NULL despite the table default");
            }
        }
        finally
        {
            Scalar("""
SET XACT_ABORT ON;BEGIN TRANSACTION;
DELETE t FROM dbo.Fintech_Transactions t JOIN dbo.Alson_Fintech_Rows r ON r.TargetID=t.ID AND r.SiteID=t.Site_ID
JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash WHERE f.BatchID=@batch AND r.EntityType='TXN';
DELETE dbo.Alson_Fintech_Imports WHERE BatchID=@batch;
DELETE r FROM dbo.Alson_Fintech_Rows r JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash WHERE f.BatchID=@batch;
DELETE dbo.Alson_Fintech_Frames WHERE BatchID=@batch;
DELETE dbo.Alson_Fintech_Batches WHERE BatchID=@batch;
COMMIT TRANSACTION;SELECT 1;
""",("@batch",batch));
        }
    }
}
