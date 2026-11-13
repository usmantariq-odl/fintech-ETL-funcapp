using System.Data;
using System.Data.SqlClient;
using System.Text.Json;
using FintechEtl.FunctionApp.Alson;

internal static class LiveAlsonTankProductChecks
{
    internal static void Run(string settingsPath)
    {
        using(var j=JsonDocument.Parse(File.ReadAllText(settingsPath)))
            foreach(var v in j.RootElement.GetProperty("Values").EnumerateObject())Environment.SetEnvironmentVariable(v.Name,v.Value.GetString());
        Environment.SetEnvironmentVariable("AlsonEtlEnabled","true");Environment.SetEnvironmentVariable("AlsonEtlDryRun","true");Environment.SetEnvironmentVariable("AlsonErrorEmailEnabled","false");
        var store=new AlsonStore(AlsonSettings.Load());using var c=store.Open();
        var sites=new[]{(1191,560257,"81C3C8CD7C3",new[]{"24696","24699","24697","83939"},new[]{1,1,1,2}),
            (1192,560256,"81C3C8CD824",new[]{"00002","00009","00004","00007"},new[]{1,3,1,2})};
        foreach(var (site,fc,master,probes,expected) in sites)
        {
            for(int i=0;i<probes.Length;i++)
            {
                using var q=new SqlCommand(AlsonStore.TankConfigurationSql,c);
                q.Parameters.AddWithValue("@customer",227);q.Parameters.AddWithValue("@site",site);q.Parameters.AddWithValue("@fc",fc);
                q.Parameters.AddWithValue("@master",master);q.Parameters.AddWithValue("@probe",probes[i]);
                using var da=new SqlDataAdapter(q);var t=new DataTable();da.Fill(t);
                if(t.Rows.Count!=1||AlsonStore.TankProductNumber(t.Rows[0])!=expected[i])throw new Exception("Incorrect production tank product lookup.");
                Console.WriteLine($"PASS live lookup: site {site}, tank {i+1}, shared product {expected[i]}.");
            }
            // Distinct local frame; Validate rolls its SQL transaction back and does not reserve a frame or insert data.
            string raw=$"#20,2147482998,{master},12:00:00,2026-11-12"+string.Concat(probes.Select(p=>$"~{p},100,0,30"))+"~1|";
            var body=AlsonChecks.Envelope(raw);var result=store.Validate("local-validation/tank-product-"+Guid.NewGuid(),"validation",body,AlsonParser.Parse(body));
            if(result.Outcome!="imported"||result.Rows!=4)throw new Exception("Production ATG validation failed.");
        }
        Console.WriteLine("PASS: eight real staging tank lookups and two complete ATG validations. No business rows, audits, blobs or emails written.");
    }
}
