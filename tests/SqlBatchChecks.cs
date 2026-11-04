using System.Data;
using System.Data.SqlClient;
using System.Transactions;
using CSVReader;

internal static class SqlBatchChecks
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); }
        void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS SQL batching: " + name); }
        DataTable Data(int rows, int site = 930)
        {
            var t = new DataTable(); t.Columns.Add("Site_ID", typeof(int)); t.Columns.Add("Sale_ID", typeof(string));
            t.Columns.Add("Value", typeof(string));
            for (int i = 0; i < rows; i++) t.Rows.Add(site, i.ToString(), i == 0 ? DBNull.Value : "007001");
            return t;
        }
        var options = new SqlWriteOptions(1000, 120, 600);
        Test("batch boundaries preserve order, identity, values and progress", () =>
        {
            using var source = Data(2501); var sizes = new List<int>(); var progress = new List<int>(); int expected = 0;
            SqlTransactionBatches.Execute(source, 566093, options, (batch, id) =>
            {
                Check(id == 566093, "Archive ID changed between batches."); sizes.Add(batch.Rows.Count);
                foreach (DataRow row in batch.Rows) Check((string)row["Sale_ID"] == (expected++).ToString(), "Lost, reordered or repeated row.");
                Check(batch.Rows[0]["Site_ID"].Equals(930), "Site changed.");
            }, (done, total) => { progress.Add(done); Check(total == 2501, "Wrong total."); });
            Check(sizes.SequenceEqual(new[] {1000,1000,501}) && expected == 2501, "Batch boundary wrong.");
            Check(progress.SequenceEqual(new[] {1000,2000,2501}), "Progress not emitted after each successful batch.");
            Check(source.Columns["Sale_ID"].MaxLength == -1 && source.Rows[0].IsNull("Value"), "Source mutated.");
        });
        Test("failed later batch escapes; no later rows or completion progress", () =>
        {
            using var source = Data(2501); int calls=0, progress=0;
            try
            {
                SqlTransactionBatches.Execute(source, 1, options, (batch,id) => { if (++calls==2) throw new IOException("Injected failure"); }, (_,_)=>progress++);
                throw new Exception("Failure swallowed.");
            }
            catch(IOException) { Check(calls==2 && progress==1, "Failed batch was reported complete or processing continued."); }
        });
        Test("empty and single-row CSVs", () =>
        {
            int count=0; using var empty=Data(0); using var single=Data(1);
            SqlTransactionBatches.Execute(empty, null, options, (_,_)=>count++);
            SqlTransactionBatches.Execute(single, null, options, (b,id)=>{Check(b.Rows.Count==1 && id==null,"Single row/null archive ID changed.");count++;});
            Check(count==1,"Empty table sent SQL.");
        });
        Test("bounded short-string metadata, nulls, Unicode and genuine MAX values", () =>
        {
            using var t=new DataTable();t.Columns.Add("short",typeof(string));t.Columns.Add("long",typeof(string));t.Columns.Add("null",typeof(string));
            var longText=new string('\u0627',5001);t.Rows.Add("007001",longText,DBNull.Value);t.Rows.Add("\u062f\u06cc\u0632\u0644","",DBNull.Value);
            SqlTransactionBatches.Execute(t,1,options,(b,_)=>
            {
                Check(b.Columns[0].MaxLength==6 && b.Columns[1].MaxLength==-1 && b.Columns[2].MaxLength==1,"Incorrect TVP metadata.");
                Check((string)b.Rows[0][1]==longText && (string)b.Rows[1][0]=="\u062f\u06cc\u0632\u0644" && b.Rows[0].IsNull(2),"Value truncated/converted.");
            });
        });
        Test("concurrent sites keep separate batches and parent archive IDs", () =>
        {
            using var start=new Barrier(2);
            Parallel.ForEach(new[]{930,1189},site=>
            {
                using var data=Data(2501,site);int read=0;
                SqlTransactionBatches.Execute(data,site*100,options,(batch,id)=>
                {
                    start.SignalAndWait(TimeSpan.FromSeconds(10));
                    Check(id==site*100 && batch.AsEnumerable().All(r=>(int)r[0]==site),"Cross-site/archive context leaked.");
                    read+=batch.Rows.Count;
                });Check(read==2501,"Concurrent rows lost.");
            });
        });
        Test("command preserves stored procedure, type, ID and bounded timeout", () =>
        {
            using var t=Data(1);using var con=new SqlConnection();using var cmd=SqlTransactionBatches.CreateCommand(con,t,4553444,options);
            Check(cmd.CommandTimeout==120 && cmd.CommandType==CommandType.StoredProcedure && cmd.CommandText=="dbo.InsertintoFintechTransactions","Wrong command/timeout.");
            Check(cmd.Parameters["@TransactionsTable"].TypeName=="dbo.TransactionsTableType" && (int)cmd.Parameters["@ProcessedFileID"].Value==4553444,"Wrong type or parent ID.");
        });
        Test("aborted transaction prevents later batches", () =>
        {
            using var t=Data(2501);int calls=0;bool rejected=false;
            using(var scope=EtlTransaction.Begin())
            {
                try { SqlTransactionBatches.Execute(t,1,options,(_,_)=>{calls++;Transaction.Current.Rollback();}); }
                catch(TransactionAbortedException){rejected=true;}
            }
            Check(rejected && calls==1,"Rows sent after transaction aborted.");
        });
        Test("default and invalid configuration", () =>
        {
            string[] names=["EtlSqlBatchSize","EtlSqlCommandTimeoutSeconds","EtlCsvTransactionTimeoutSeconds"];
            var previous=names.Select(Environment.GetEnvironmentVariable).ToArray();
            try
            {
                foreach(var n in names)Environment.SetEnvironmentVariable(n,null);
                Check(SqlWriteOptions.Load()==options,"Incorrect defaults.");
                foreach(var (name,value) in new[]{(names[0],"0"),(names[0],"5001"),(names[1],"0"),(names[2],"601")})
                {
                    Environment.SetEnvironmentVariable(name,value);bool rejected=false;
                    try { SqlWriteOptions.Load(); }catch(InvalidOperationException){rejected=true;}
                    Check(rejected,"Invalid setting accepted: "+name);Environment.SetEnvironmentVariable(name,null);
                }
            }
            finally { for(int i=0;i<names.Length;i++)Environment.SetEnvironmentVariable(names[i],previous[i]); }
        });
        return passed;
    }
}
