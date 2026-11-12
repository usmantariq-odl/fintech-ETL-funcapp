using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FintechEtl.FunctionApp.Alson;

internal sealed partial class AlsonStore(AlsonSettings settings)
{
    private readonly Dictionary<string,DataRow> previewConfiguration=new(StringComparer.Ordinal);
    internal sealed record Result(string Outcome, int Rows, string Detail = null);
    internal SqlConnection Open()
    {
        var c = new SqlConnection(settings.Sql); c.Open();
        using var check = new SqlCommand("SELECT DB_NAME()", c);
        if ((string)check.ExecuteScalar() != "FintechStagDB_NewArchitecture" || c.Database != settings.ExpectedDatabase)
        { c.Dispose(); throw new InvalidOperationException("Unexpected Alson SQL database."); }
        return c;
    }
    internal void VerifySchema()
    {
        using var c = Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID('dbo') AND name IN ('Alson_Fintech_Batches','Alson_Fintech_ProductMap','Alson_Fintech_ProbeConfig','Alson_Fintech_Frames','Alson_Fintech_Rows','Alson_Fintech_Imports','Alson_Fintech_DiagnosticReceipts','Alson_Fintech_AutoManualEvents','Alson_Fintech_DspConnectionEvents','Alson_Fintech_AtgConnectionEvents','Alson_Fintech_ProbeStatusEvents') AND COL_LENGTH('dbo.Alson_Fintech_ProductMap','OmniSiteID') IS NOT NULL AND COL_LENGTH('dbo.Alson_Fintech_Imports','MasterId') IS NOT NULL", c);
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 11) throw new InvalidOperationException("Apply reviewed Alson onboarding and diagnostic migrations before enabling this trigger.");
    }
    internal void BeginBatch(Guid batch)
    {
        using var c = Open(); using var cmd = new SqlCommand("INSERT dbo.Alson_Fintech_Batches(BatchID,Kind) VALUES(@id,'import')",c);
        cmd.Parameters.AddWithValue("@id",batch); cmd.ExecuteNonQuery();
    }
    internal sealed record TerminalSource(string ETag,string Outcome,byte[] EnvelopeHash,string Master)
    {
        internal bool ArchiveEligible=>Outcome is "imported" or "duplicate";
    }
    internal Dictionary<string,TerminalSource> TerminalSources()
    {
        using var c=Open(); using var cmd=new SqlCommand("SELECT i.SourceKey,i.SourceETag,i.Outcome,i.EnvelopeHash,COALESCE(i.MasterId,f.MasterId) FROM dbo.Alson_Fintech_Imports i LEFT JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=i.FrameHash WHERE i.Outcome IN ('imported','duplicate','unsupported','excluded','invalid')",c);
        using var r=cmd.ExecuteReader(); var found=new Dictionary<string,TerminalSource>(StringComparer.Ordinal);
        while(r.Read())found[Convert.ToHexString((byte[])r[0])]=new(r.IsDBNull(1)?"":r.GetString(1),r.GetString(2),(byte[])r[3],r.IsDBNull(4)?"":r.GetString(4));
        return found;
    }
    internal sealed record ArchiveCandidate(string BlobName,byte[] EnvelopeHash,string Master);
    internal List<ArchiveCandidate> ArchiveCandidates()
    {
        using var c=Open();using var cmd=new SqlCommand("SELECT i.BlobName,i.EnvelopeHash,COALESCE(i.MasterId,f.MasterId) FROM dbo.Alson_Fintech_Imports i LEFT JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=i.FrameHash WHERE i.Outcome IN ('imported','duplicate') ORDER BY i.BlobName",c);
        using var r=cmd.ExecuteReader();var list=new List<ArchiveCandidate>();
        while(r.Read())if(!r.IsDBNull(2)&&settings.Masters.Contains(r.GetString(2))&&r.GetString(0).StartsWith(AlsonArchive.Inbox,StringComparison.Ordinal))
            list.Add(new(r.GetString(0),(byte[])r[1],r.GetString(2)));
        return list;
    }
    internal static byte[] SourceKey(string container,string blob)=>SHA256.HashData(Encoding.UTF8.GetBytes(container+"/"+blob));
    internal Result Validate(string name,string etag,byte[] envelope,AlsonFrame frame)
        =>Process(name,etag,envelope,frame,Guid.Empty,commit:false);
    private static SqlCommand Command(SqlConnection c,SqlTransaction tx,string text,params (string,object)[] values)
    {
        var cmd=new SqlCommand(text,c,tx){CommandTimeout=60};
        foreach(var (name,value) in values) cmd.Parameters.AddWithValue(name,value??DBNull.Value);
        return cmd;
    }
    private static DataRow One(SqlConnection c,SqlTransaction tx,string text,params (string,object)[] values)
    {
        using var cmd=Command(c,tx,text,values);using var adapter=new SqlDataAdapter(cmd); var t=new DataTable();adapter.Fill(t);
        if(t.Rows.Count!=1)throw new AlsonPendingException($"Expected exactly one configuration row; found {t.Rows.Count}.");
        return t.Rows[0];
    }
    private static object Value(DataRow r,string name)=>r[name] is DBNull?null:r[name];
    private DataRow Configuration(SqlConnection c,SqlTransaction tx,bool preview,string text,params(string,object)[] values)
    {
        var key=text+string.Join('|',values.Select(v=>v.Item1+"="+Convert.ToString(v.Item2,CultureInfo.InvariantCulture)));
        if(preview&&previewConfiguration.TryGetValue(key,out var row))return row;
        row=One(c,tx,text,values);if(preview)previewConfiguration[key]=row;return row;
    }
    internal Result Process(string name,string etag,byte[] envelope,AlsonFrame frame,Guid batch,bool commit=true)
    {
        using var c=Open();using var tx=c.BeginTransaction();
        if(commit)using(var lifecycle=Command(c,tx,"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='AlsonFintech:Lifecycle',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000; SELECT @r"))
            if(Convert.ToInt32(lifecycle.ExecuteScalar())<0)throw new TimeoutException("Alson lifecycle lock unavailable.");
        var key=SourceKey(settings.Container,name);var envelopeHash=SHA256.HashData(envelope);
        if(commit)using(var l=Command(c,tx,"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; SELECT @r",("@key","Alson:source:"+Convert.ToHexString(key))))
            if(Convert.ToInt32(l.ExecuteScalar())<0)throw new TimeoutException("Alson source lock unavailable.");
        using(var old=Command(c,tx,"SELECT EnvelopeHash,Outcome FROM dbo.Alson_Fintech_Imports "+(commit?"WITH(UPDLOCK,HOLDLOCK) ":"")+"WHERE SourceKey=@key",("@key",key)))
        using(var r=old.ExecuteReader())
        {
            if(r.Read())
            {
                if(!((byte[])r[0]).SequenceEqual(envelopeHash))throw new InvalidOperationException("Existing source blob changed content; original audit preserved.");
                if(r.GetString(1) is "imported" or "duplicate" or "unsupported" or "excluded" or "invalid")return new("already_seen",0);
            }
        }
        if(frame.Diagnostic!=null)return ProcessDiagnostic(c,tx,key,name,etag,envelopeHash,envelope,frame,batch,commit);
        if(frame.Command is not("00" or "20"))return Finish("unsupported",0,null);
        if(!settings.Masters.Contains(frame.Master)||frame.At<settings.NotBefore)return Finish("excluded",0,null);
        if(commit)using(var l=Command(c,tx,"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; SELECT @r",("@key","Alson:frame:"+Convert.ToHexString(frame.Hash))))
            if(Convert.ToInt32(l.ExecuteScalar())<0)throw new TimeoutException("Alson frame lock unavailable.");
        using(var duplicate=Command(c,tx,"SELECT COUNT(*) FROM dbo.Alson_Fintech_Frames WHERE FrameHash=@hash",("@hash",frame.Hash)))
            if(Convert.ToInt32(duplicate.ExecuteScalar())>0)return Finish("duplicate",0,null);
        var site=Configuration(c,tx,!commit,"""
SELECT s.Site_Id,s.Customer_Id_FK,m.FC_SiteID FROM dbo.Omni_Fintech_AlsonSiteMaster a
JOIN dbo.Plant_Site s ON s.Site_Id=a.Omni_SiteID
JOIN dbo.Omni_Fintech_SiteMapping m ON m.Omni_SiteID=s.Site_Id
WHERE a.MasterId COLLATE Latin1_General_100_BIN2=@master AND a.DeviceType=@type
AND a.IsActive=1 AND s.IsActive=1 AND s.DeviceTypeId=3
""",("@master",frame.Master),("@type",frame.Command=="00"?"DSP":"ATG"));
        int siteId=Convert.ToInt32(site["Site_Id"]);int fcSite=Convert.ToInt32(site["FC_SiteID"]);
        if(commit)using(var insert=Command(c,tx,"INSERT dbo.Alson_Fintech_Frames(FrameHash,MasterId,CommandCode,RecordID,SourceAt,RawFrame,RawBytes,BatchID,GatewayReceivedAt) VALUES(@hash,@master,@cmd,@record,@at,@raw,@bytes,@batch,@received)",
            ("@hash",frame.Hash),("@master",frame.Master),("@cmd",frame.Command),("@record",frame.RecordId),("@at",frame.At),("@raw",frame.Raw),("@bytes",frame.Bytes),("@batch",batch),("@received",frame.GatewayReceivedAt)))insert.ExecuteNonQuery();
        int count=0;string detail=null;
        if(frame.Command=="00")
        {
            var pump=Configuration(c,tx,!commit,"""
SELECT m.ID,m.FC_PumpID,m.FC_NozzleID,t.ProdID_FK TankProductID FROM dbo.Omni_Fintech_PumpMapping m
LEFT JOIN dbo.Omni_Fintech_TankMapping t ON t.Omni_SiteID=m.Omni_SiteID AND t.Omni_AssetID=m.Omni_TankID AND t.FC_TankID=m.FC_TankID
WHERE m.MasterId COLLATE Latin1_General_100_BIN2=@master AND m.AlsonPumpCode COLLATE Latin1_General_100_BIN2=@code
AND m.FC_NozzleID=@nozzle AND m.Omni_SiteID=@site AND m.FC_SiteID=@fc
""",("@master",frame.Master),("@code",frame.Dispenser),("@nozzle",frame.Nozzle),("@site",siteId),("@fc",fcSite));
            var product=Configuration(c,tx,!commit,"""
SELECT m.AlsonGradeID,p.product_name,p.product_id CanonicalProductID FROM dbo.Alson_Fintech_ProductMap m
JOIN dbo.Omni_Fintech_Products p ON p.Id=m.ProductRowID AND p.OmniCustomerID_FK=m.CustomerID
WHERE m.CustomerID=@customer AND m.OmniSiteID=@site AND m.RawCode=@code
""",("@customer",site["Customer_Id_FK"]),("@site",siteId),("@code",frame.Product));
            if(Value(pump,"FC_PumpID")==null||Value(pump,"FC_NozzleID")==null)throw new AlsonPendingException("Missing numeric pump/nozzle mapping.");
            if(Value(product,"AlsonGradeID")==null||Convert.ToInt32(product["AlsonGradeID"])<=0)
                throw new AlsonPendingException("Alson product code has no configured AlsonGradeID.");
            if(Value(pump,"TankProductID")!=null&&Convert.ToInt32(pump["TankProductID"])!=Convert.ToInt32(product["CanonicalProductID"]))
                throw new AlsonPendingException("Sale product disagrees with the nozzle's configured tank product.");
            using var sale=Command(c,tx,"""
INSERT dbo.Fintech_Transactions(Site_ID,FC_SiteID,Sale_ID,Pump_ID,Hose_ID,Grade_ID,Article,Volume,Money,PPU,End_Date,End_Time,FC_EndDateTime,CreatedOn,CreatedBy,IsManualSite,Final_Volume)
OUTPUT INSERTED.ID VALUES(@site,@fc,@sale,@pump,@hose,@grade,@article,@volume,@money,@ppu,@date,@time,@at,SYSUTCDATETIME(),'AlsonETL',NULL,@final)
""",("@site",siteId),("@fc",fcSite),("@sale",int.Parse(frame.RecordId,CultureInfo.InvariantCulture)),("@pump",pump["FC_PumpID"]),("@hose",pump["FC_NozzleID"]),
                ("@grade",product["AlsonGradeID"]),("@article",product["product_name"]),("@volume",(double)frame.Volume.Value),("@money",(double)frame.Amount.Value),("@ppu",(double)frame.Price.Value),
                ("@date",frame.At.Date),("@time",frame.At.TimeOfDay),("@at",frame.At),("@final",(double)frame.Totalizer.Value));
            if(commit)Link("TXN",Convert.ToInt32(pump["ID"]),Convert.ToInt32(sale.ExecuteScalar()));count=1;
        }
        else foreach(var probe in frame.Probes)
        {
            var tank=Configuration(c,tx,!commit,"""
SELECT q.*,m.FC_TankID,m.Omni_AssetID,m.ProdID_FK,p.product_name,pm.AlsonGradeID SourceProductNumber FROM dbo.Alson_Fintech_ProbeConfig q
JOIN dbo.Omni_Fintech_TankMapping m ON m.ID=q.TankMappingID
LEFT JOIN dbo.Omni_Fintech_Products p ON p.product_id=m.ProdID_FK AND p.OmniCustomerID_FK=@customer
LEFT JOIN dbo.Alson_Fintech_ProductMap pm ON pm.ProductRowID=p.Id AND pm.CustomerID=@customer AND pm.OmniSiteID=m.Omni_SiteID
WHERE q.MasterId=@master AND q.ProbeCode=@probe AND m.MasterId COLLATE Latin1_General_100_BIN2=@master
AND m.Omni_SiteID=@site AND m.FC_SiteID=@fc
""",("@customer",site["Customer_Id_FK"]),("@master",frame.Master),("@probe",probe.Code),("@site",siteId),("@fc",fcSite));
            if(probe.Position>0&&Convert.ToInt32(tank["FC_TankID"])!=probe.Position)
                throw new AlsonPendingException("ATG probe order differs from its confirmed physical-tank mapping; review configuration.");
            if(probe.Temperature<Convert.ToDecimal(tank["MinTemperature"])||probe.Temperature>Convert.ToDecimal(tank["MaxTemperature"]))throw new FormatException("Temperature outside configured physical limits.");
            object fuel=Measurement(tank,"Fuel",probe.Fuel);object water=Measurement(tank,"Water",probe.Water);
            string fuelKind=Value(tank,"FuelKind") as string;string waterKind=Value(tank,"WaterKind") as string;
            if(fuel==null||water==null)detail="Fuel/water units unconfirmed: quantities retained in raw audit; unconfirmed target measurements remain NULL.";
            using var atg=Command(c,tx,"""
INSERT dbo.Fintech_TankLevels(Site_ID,FC_SiteID,Tank_ID,Omni_AssetID,Product_Number,Product_ID,Product_Height,Product_Volume,Water_Height,Water_Volume,Product_Temperature,Date_Last_Read,Time_Last_Read,FC_TimeStamp,Time_Stamp,CreatedOn,CreatedBy)
OUTPUT INSERTED.ID VALUES(@site,@fc,@tank,@asset,@product,@name,@fh,@fv,@wh,@wv,@temperature,@date,@time,@at,@at,SYSUTCDATETIME(),'AlsonETL')
""",("@site",siteId),("@fc",fcSite),("@tank",tank["FC_TankID"]),("@asset",tank["Omni_AssetID"]),("@product",Value(tank,"SourceProductNumber")),("@name",Value(tank,"product_name")),
                ("@fh",fuelKind=="HEIGHT"?fuel:null),("@fv",fuelKind=="VOLUME"?fuel:null),("@wh",waterKind=="HEIGHT"?water:null),("@wv",waterKind=="VOLUME"?water:null),
                ("@temperature",(double)probe.Temperature),("@date",frame.At.Date),("@time",frame.At.TimeOfDay),("@at",frame.At));
            if(commit)Link("ATG",Convert.ToInt32(tank["TankMappingID"]),Convert.ToInt32(atg.ExecuteScalar()));count++;
        }
        return Finish("imported",count,detail);
        void Link(string kind,int mapping,int id)
        {
            var table=kind=="TXN"?"Fintech_Transactions":"Fintech_TankLevels";
            using var snapshot=Command(c,tx,$"SELECT (SELECT * FROM dbo.{table} WHERE ID=@id FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)",("@id",id));
            var json=(string)snapshot.ExecuteScalar();
            using var cmd=Command(c,tx,"INSERT dbo.Alson_Fintech_Rows(FrameHash,EntityType,MappingID,TargetID,SiteID,TargetSnapshot) VALUES(@hash,@kind,@map,@id,@site,@json)",
                ("@hash",frame.Hash),("@kind",kind),("@map",mapping),("@id",id),("@site",siteId),("@json",json));cmd.ExecuteNonQuery();
        }
        Result Finish(string outcome,int rows,string note)
        {
            if(commit){Audit(c,tx,key,name,etag,envelopeHash,envelope,frame.Hash,outcome,note,batch,frame.Master,frame.Command);tx.Commit();}
            else tx.Rollback();return new(outcome,rows,note);
        }
    }
    private static object Measurement(DataRow row,string prefix,decimal raw)
    {
        if(Value(row,prefix+"Kind")==null)return null;
        if(Value(row,prefix+"Scale")==null||Value(row,"Max"+prefix)==null)throw new AlsonPendingException("Measurement units configured without scale/physical bounds.");
        if(raw>Convert.ToDecimal(row["Max"+prefix]))throw new FormatException(prefix+" outside configured physical limits.");
        return (double)(raw*Convert.ToDecimal(row[prefix+"Scale"]));
    }
    private static void Audit(SqlConnection c,SqlTransaction tx,byte[] key,string name,string etag,byte[] hash,byte[] envelope,byte[] frameHash,string outcome,string error,Guid batch,string master=null,string code=null)
    {
        using var cmd=Command(c,tx,"""
UPDATE dbo.Alson_Fintech_Imports SET SourceETag=@etag,FrameHash=@frame,Outcome=@outcome,ErrorText=@error,BatchID=@batch,MasterId=COALESCE(@master,MasterId),CommandCode=COALESCE(@code,CommandCode),UpdatedOn=SYSUTCDATETIME() WHERE SourceKey=@key;
IF @@ROWCOUNT=0 INSERT dbo.Alson_Fintech_Imports(SourceKey,BlobName,SourceETag,EnvelopeHash,Envelope,FrameHash,Outcome,ErrorText,BatchID,MasterId,CommandCode)
VALUES(@key,@name,@etag,@hash,@body,@frame,@outcome,@error,@batch,@master,@code);
""",("@key",key),("@name",name),("@etag",etag),("@hash",hash),("@body",envelope),("@frame",frameHash),("@outcome",outcome),("@error",error),("@batch",batch),("@master",string.IsNullOrEmpty(master)?null:master),("@code",code?.Length==2?code:null));cmd.ExecuteNonQuery();
    }
    internal void RecordFailure(string name,string etag,byte[] envelope,string outcome,string error,Guid batch)
    {
        using var c=Open();using var tx=c.BeginTransaction();var key=SourceKey(settings.Container,name);var hash=SHA256.HashData(envelope);
        using(var old=Command(c,tx,"SELECT EnvelopeHash,Outcome FROM dbo.Alson_Fintech_Imports WITH(UPDLOCK,HOLDLOCK) WHERE SourceKey=@key",("@key",key)))
        using(var reader=old.ExecuteReader())
        {
            if(reader.Read())
            {
                if(!((byte[])reader[0]).SequenceEqual(hash))throw new InvalidOperationException("Source conflict: original audit preserved.");
                // A simultaneous successful import must not be downgraded by an older failure.
                if(reader.GetString(1) is "imported" or "duplicate" or "unsupported" or "excluded" or "invalid")return;
            }
        }
        Audit(c,tx,key,name,etag,hash,envelope,null,outcome,error.Length>4000?error[..4000]:error,batch);tx.Commit();
    }
}
