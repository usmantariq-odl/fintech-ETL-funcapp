using System.Data;
using System.Data.SqlClient;
using System.Text.Json;
using FintechEtl.FunctionApp.Alson;

internal static class LiveAlsonTransactionProductChecks
{
    internal static void Run(string settingsPath)
    {
        using(var j=JsonDocument.Parse(File.ReadAllText(settingsPath)))
            foreach(var v in j.RootElement.GetProperty("Values").EnumerateObject())Environment.SetEnvironmentVariable(v.Name,v.Value.GetString());
        Environment.SetEnvironmentVariable("AlsonEtlEnabled","true");Environment.SetEnvironmentVariable("AlsonEtlDryRun","true");Environment.SetEnvironmentVariable("AlsonErrorEmailEnabled","false");
        var store=new AlsonStore(AlsonSettings.Load());using var c=store.Open();
        using var config=new SqlCommand("""
SELECT m.*,p.product_id ExpectedProductNumber,pm.RawCode,pm.AlsonGradeID
FROM dbo.Omni_Fintech_PumpMapping m
JOIN dbo.Omni_Fintech_TankMapping t ON t.Omni_SiteID=m.Omni_SiteID AND t.Omni_AssetID=m.Omni_TankID AND t.FC_TankID=m.FC_TankID AND t.FC_SiteID=m.FC_SiteID
JOIN dbo.Omni_Fintech_Products p ON p.OmniCustomerID_FK=227 AND p.product_id=t.ProdID_FK
JOIN dbo.Alson_Fintech_ProductMap pm ON pm.CustomerID=227 AND pm.OmniSiteID=m.Omni_SiteID AND pm.ProductRowID=p.Id
WHERE m.Omni_SiteID IN(1191,1192) AND m.MasterId IN('81C3C8CBD19','81C3C8CE581')
ORDER BY m.Omni_SiteID,m.FC_PumpID,m.FC_NozzleID
""",c);
        using var da=new SqlDataAdapter(config);var all=new DataTable();da.Fill(all);
        if(all.Rows.Count!=24)throw new Exception("Expected 24 uniquely mapped channels.");
        int nativeDifferences=0;
        foreach(DataRow row in all.Rows)
        {
            using var query=new SqlCommand(AlsonStore.TransactionPumpConfigurationSql,c);
            foreach(var (name,value) in new (string,object)[]{("@master",row["MasterId"]),("@code",row["AlsonPumpCode"]),("@nozzle",row["FC_NozzleID"]),("@site",row["Omni_SiteID"]),("@fc",row["FC_SiteID"]),("@customer",227)})query.Parameters.AddWithValue(name,value);
            using var adapter=new SqlDataAdapter(query);var pump=new DataTable();adapter.Fill(pump);
            var source=new DataTable();source.Columns.Add("CanonicalProductID",typeof(int));source.Columns.Add("AlsonGradeID",typeof(int));
            var sourceRow=source.Rows.Add(row["ExpectedProductNumber"],row["AlsonGradeID"]);
            if(pump.Rows.Count!=1||AlsonStore.TransactionProductNumber(pump.Rows[0],sourceRow)!=Convert.ToInt32(row["ExpectedProductNumber"]))throw new Exception("Wrong mapped transaction grade.");
            if(Convert.ToInt32(row["ExpectedProductNumber"])!=Convert.ToInt32(row["AlsonGradeID"]))nativeDifferences++;
            string raw=$"#00,{2147000000+Convert.ToInt32(row["ID"])},{row["MasterId"]},{row["AlsonPumpCode"]},{row["FC_NozzleID"]},{row["RawCode"]},1,2,2,3,12:00:00,2026-11-13,No|";
            var body=AlsonChecks.Envelope(raw);var result=store.Validate("local-validation/transaction-product-"+Guid.NewGuid(),"validation",body,AlsonParser.Parse(body));
            if(result.Outcome!="imported"||result.Rows!=1)throw new Exception("Transaction validation did not accept correct mapping.");
            Console.WriteLine($"PASS live: site {row["Omni_SiteID"]}, channel {row["AlsonPumpCode"]}, Fintech product {row["ExpectedProductNumber"]} (native {row["AlsonGradeID"]}).");
        }
        if(nativeDifferences!=22)throw new Exception("Expected 22 native/shared channel differences.");
        var conflict=AlsonChecks.Envelope("#00,2147481999,81C3C8CBD19,0@,1,H,1,2,2,3,12:00:00,2026-11-13,No|");
        bool rejected=false;try{store.Validate("local-validation/conflict-"+Guid.NewGuid(),"validation",conflict,AlsonParser.Parse(conflict));}catch(AlsonPendingException){rejected=true;}
        if(!rejected)throw new Exception("Contradictory raw product accepted.");
        Console.WriteLine("PASS: 24 production lookups and complete sale validations; 22 native-grade differences normalized; contradictory product rejected. No writes, archives or emails.");
    }
}

