using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Transactions;
using CSVReader;
using log4net;

// Explicit opt-in: original fixture + deployed procedure conversion body, but every
// INSERT and checkpoint write targets session-local temporary tables only.
internal static class LiveSqlBatchCheck
{
    public static void Run(string[] args)
    {
        var index=Array.IndexOf(args,"--fixture");
        if(index<0 || index+1>=args.Length) throw new ArgumentException("Supply --fixture <archive.zip>.");
        var root=Path.Combine(Path.GetTempPath(),"EtlBatchCheck",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try { RunCore(args[index+1],root); }
        finally { Directory.Delete(root,true); }
    }

    private static void RunCore(string fixture,string root)
    {
        var options=SqlWriteOptions.Load();var watch=Stopwatch.StartNew();
        var log=LogManager.GetLogger("LiveSqlBatchCheck");
        var csv=ArchiveFiles.Extract(fixture,root).Single(f=>Path.GetFileName(f).StartsWith("PSL-",StringComparison.OrdinalIgnoreCase));
        using var data=new ReadTransactionTankData(log).GetDataTableFromCsv(csv,true);
        var map=new OmniFintechMapping(log);var dal=new DAL(log);
        int fc=int.Parse((string)data.Rows[0][0],CultureInfo.InvariantCulture);
        int site=checked((int)map.getOmniSiteID(fc));
        if(site<=0)throw new Exception("Fixture site does not have an active mapping.");
        data.Columns.Add("Article",typeof(string));data.Columns.Add("OmniSite_ID",typeof(int));
        data.Columns.Add("FC_StartDateTime",typeof(DateTime));data.Columns.Add("FC_EndDateTime",typeof(DateTime));data.Columns.Add("createdon",typeof(DateTime));
        decimal expectedVolume=0;var created=DateTime.UtcNow.AddHours(5);
        foreach(DataRow row in data.Rows)
        {
            if(int.Parse((string)row[0],CultureInfo.InvariantCulture)!=fc)throw new Exception("Mixed-site fixture.");
            row["Article"]=(object)map.getArticleName(site,int.Parse((string)row[6],CultureInfo.InvariantCulture))??DBNull.Value;
            row["OmniSite_ID"]=site;
            row["FC_StartDateTime"]=dal.getConvertedDateTime((string)row[14],(string)row[15]);
            row["FC_EndDateTime"]=dal.getConvertedDateTime((string)row[2],(string)row[3]);row["createdon"]=created;
            expectedVolume+=decimal.Parse((string)row[7],CultureInfo.InvariantCulture);
        }
        data.Columns["OmniSite_ID"].ColumnName="Site_ID";data.Columns["site_id"].ColumnName="FC_SiteID";
        data.Columns["Site_ID"].SetOrdinal(0);data.Columns["FC_SiteID"].SetOrdinal(20);
        Console.WriteLine($"Fixture prepared: {data.Rows.Count} rows; site {fc}/{site}; {watch.Elapsed.TotalSeconds:F1}s.");

        using var connection=new SqlConnection(Environment.GetEnvironmentVariable("FintechSqlConnectionString"));connection.Open();
        void Execute(string text){using var cmd=new SqlCommand(text,connection){CommandTimeout=120};cmd.ExecuteNonQuery();}
        object Scalar(string text){using var cmd=new SqlCommand(text,connection){CommandTimeout=120};return cmd.ExecuteScalar();}
        var definition=(string)Scalar("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.InsertintoFintechTransactions'))");
        var start=Regex.Match(definition,@"\bAS\s+BEGIN\b",RegexOptions.IgnoreCase);
        if(!start.Success)throw new Exception("Unexpected stored procedure shape.");
        var body=definition[(start.Index+2)..];
        var writes=Regex.Matches(body,@"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE|EXEC(?:UTE)?)\b",RegexOptions.IgnoreCase);
        if(writes.Count!=1 || !Regex.IsMatch(body,@"INSERT\s+INTO\s+Fintech_Transactions\b",RegexOptions.IgnoreCase))
            throw new Exception("Procedure has unreviewed side effects; temporary-table check refused.");
        body=Regex.Replace(body,@"INSERT\s+INTO\s+Fintech_Transactions\b","INSERT INTO #EtlBatchRows",RegexOptions.IgnoreCase);
        Execute("SELECT TOP(0) * INTO #EtlBatchRows FROM dbo.Fintech_Transactions; CREATE TABLE #EtlBatchCheckpoint(KeyHash binary(32) NOT NULL PRIMARY KEY,RemoteKey nvarchar(1280) NOT NULL,Status varchar(50),ErrorMessage nvarchar(max),UpdatedOn datetime2);");
        // SELECT INTO copies types/nullability, but not defaults used by the real
        // insert (for example IsSampled). Recreate them on the TEMP table only.
        var defaults=new List<(string Column,string Definition)>();
        using(var cmd=new SqlCommand("SELECT c.name,d.definition FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID('dbo.Fintech_Transactions')",connection))
        using(var reader=cmd.ExecuteReader())while(reader.Read())defaults.Add((reader.GetString(0),reader.GetString(1)));
        foreach(var item in defaults)Execute("ALTER TABLE #EtlBatchRows ADD DEFAULT "+item.Definition+" FOR ["+item.Column.Replace("]","]]")+"];");
        const string key="/temporary-batch-check/PSL.csv";var hash=new string('a',64);
        void Checkpoint()
        {
            using var cmd=new SqlCommand(SqlFileCheckpoints.WriteSql.Replace("dbo.Fintech_EtlFileCheckpoints","#EtlBatchCheckpoint"),connection);
            SqlFileCheckpoints.AddKey(cmd,key,hash);cmd.Parameters.AddWithValue("@Status","Processed");cmd.Parameters.AddWithValue("@Error",DBNull.Value);cmd.ExecuteNonQuery();
        }
        int calls=0;
        void Send(DataTable batch,int? parent)
        {
            using var cmd=SqlTransactionBatches.CreateCommand(connection,batch,parent,options);
            cmd.CommandType=CommandType.Text;cmd.CommandText=body;
            cmd.ExecuteNonQuery();calls++;
        }
        // Force a real SQL error after earlier batches have inserted. Both the rows
        // and any completion checkpoint must roll back before the successful replay.
        using var small=data.Clone();for(int i=0;i<Math.Min(data.Rows.Count,options.BatchSize*2+1);i++)small.ImportRow(data.Rows[i]);
        bool failed=false;
        using(var scope=EtlTransaction.Begin())
        {
            connection.EnlistTransaction(Transaction.Current);
            try
            {
                SqlTransactionBatches.Execute(small,4553444,options,(batch,id)=>
                {
                    if(calls==1)Execute("THROW 51000, 'Injected later-batch SQL failure', 1;");
                    Send(batch,id);
                });Checkpoint();scope.Complete();
            }
            catch(SqlException ex)when(ex.Number==51000){failed=true;}
        }
        if(!failed || Convert.ToInt64(Scalar("SELECT COUNT_BIG(*) FROM #EtlBatchRows"))!=0 || Convert.ToInt32(Scalar("SELECT COUNT(*) FROM #EtlBatchCheckpoint"))!=0)
            throw new Exception("Failed CSV leaked rows/checkpoint.");
        Console.WriteLine("PASS real SQL later-batch failure rolls back earlier batches and leaves no completion checkpoint.");

        calls=0;var insertion=Stopwatch.StartNew();
        using(var scope=EtlTransaction.Begin())
        {
            connection.EnlistTransaction(Transaction.Current);
            SqlTransactionBatches.Execute(data,4553444,options,Send,(done,total)=>
            {if(calls==1 || calls%50==0 || done==total)Console.WriteLine($"TEMP SQL progress {done}/{total}, batches={calls}, elapsed={insertion.Elapsed.TotalSeconds:F1}s");});
            Checkpoint();scope.Complete();
        }
        var rows=Convert.ToInt64(Scalar("SELECT COUNT_BIG(*) FROM #EtlBatchRows"));
        var volume=Convert.ToDecimal(Scalar("SELECT SUM(CAST(Volume AS decimal(28,6))) FROM #EtlBatchRows"));
        if(rows!=data.Rows.Count || Math.Abs(volume-expectedVolume)>0.01m || Convert.ToInt64(Scalar($"SELECT COUNT_BIG(*) FROM #EtlBatchRows WHERE Site_ID<>{site} OR FC_SiteID<>{fc} OR ProcessedFileID<>4553444 OR ProcessedFileID IS NULL"))!=0 || Convert.ToInt32(Scalar("SELECT COUNT(*) FROM #EtlBatchCheckpoint WHERE Status='Processed'"))!=1)
            throw new Exception("Committed batch rows, values, parent references or checkpoint do not match fixture.");
        Console.WriteLine($"PASS full fixture: {rows} rows, {calls} batches, {volume} litres, {insertion.Elapsed.TotalSeconds:F1}s SQL, correct site/archive ID and committed checkpoint. Only session-local temporary tables written; application data/schema unchanged.");
    }
}
