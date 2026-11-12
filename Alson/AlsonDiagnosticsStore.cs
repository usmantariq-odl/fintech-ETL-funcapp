using System.Data;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;

namespace FintechEtl.FunctionApp.Alson;

internal sealed partial class AlsonStore
{
    internal static byte[] DiagnosticReceiptKey(AlsonFrame frame,byte[] sourceKey)=>
        SHA256.HashData(Encoding.UTF8.GetBytes(frame.Command+"/"+frame.Master+"/"+(string.IsNullOrEmpty(frame.MessageId)?"source:"+Convert.ToHexString(sourceKey):"message:"+frame.MessageId)));
    private Result ProcessDiagnostic(SqlConnection c,SqlTransaction tx,byte[] key,string name,string etag,byte[] envelopeHash,byte[] envelope,AlsonFrame frame,Guid batch,bool commit)
    {
        if(!settings.Masters.Contains(frame.Master)||(frame.GatewayReceivedAt.HasValue&&frame.GatewayReceivedAt.Value.UtcDateTime<settings.NotBefore))
            return Finish("excluded",0);
        var receipt=DiagnosticReceiptKey(frame,key);
        if(commit)using(var l=Command(c,tx,"DECLARE @r int;EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;SELECT @r",("@key","Alson:diagnostic:"+Convert.ToHexString(receipt))))
            if(Convert.ToInt32(l.ExecuteScalar())<0)throw new TimeoutException("Diagnostic receipt lock unavailable.");
        using(var old=Command(c,tx,"SELECT FrameHash FROM dbo.Alson_Fintech_DiagnosticReceipts WHERE ReceiptKey=@key",("@key",receipt)))
        {
            var existing=old.ExecuteScalar() as byte[];
            if(existing!=null)
            {
                if(!existing.SequenceEqual(frame.Hash))throw new InvalidOperationException("Diagnostic message ID reused with changed frame; prior event retained.");
                return Finish("duplicate",0);
            }
        }
        var master=One(c,tx,"""
SELECT a.ID,a.Omni_SiteID FROM dbo.Omni_Fintech_AlsonSiteMaster a JOIN dbo.Plant_Site s ON s.Site_Id=a.Omni_SiteID
WHERE a.MasterId COLLATE Latin1_General_100_BIN2=@master AND a.DeviceType=@type AND a.IsActive=1 AND s.IsActive=1 AND s.DeviceTypeId=3
""",("@master",frame.Master),("@type",frame.Command is "31" or "70"?"DSP":"ATG"));
        int masterId=Convert.ToInt32(master["ID"]),siteId=Convert.ToInt32(master["Omni_SiteID"]);
        if(commit)using(var cmd=Command(c,tx,"""
INSERT dbo.Alson_Fintech_DiagnosticReceipts(ReceiptKey,MasterMappingID,MasterId,SiteID,CommandCode,MessageId,GatewayReceivedAt,FrameHash,RawFrame,RawBytes,BatchID)
VALUES(@receipt,@mapping,@master,@site,@command,@message,@received,@hash,@raw,@bytes,@batch)
""",Common().Concat(new[]{("@command",(object)frame.Command),("@message",frame.MessageId),("@hash",frame.Hash),("@raw",frame.Raw),("@bytes",frame.Bytes),("@batch",batch)}).ToArray()))cmd.ExecuteNonQuery();
        int count=0;
        if(frame.Command=="31")
        {
            string channel=frame.Diagnostic.Channels[0];object pump=Pump(channel);
            if(commit)using(var cmd=Command(c,tx,"""
INSERT dbo.Alson_Fintech_AutoManualEvents(ReceiptKey,MasterMappingID,MasterId,SiteID,RawDispenserCode,PumpMappingID,ModeCode,ModeName,FileNumber,SlaveCode,NozzleNo,SourceAt,GatewayReceivedAt)
VALUES(@receipt,@mapping,@master,@site,@channel,@pump,@mode,@name,@file,@slave,@nozzle,@at,@received)
""",Common().Concat(new[]{("@channel",(object)channel),("@pump",pump),("@mode",frame.Diagnostic.ModeCode),("@name",frame.Diagnostic.Status),("@file",frame.RecordId),("@slave",frame.Diagnostic.SlaveCode),("@nozzle",frame.Diagnostic.Nozzle),("@at",(object)frame.At)}).ToArray()))cmd.ExecuteNonQuery();
            count=1;
        }
        else if(frame.Command=="70")
        {
            foreach(var channel in frame.Diagnostic.Channels)
            {
                object pump=Pump(channel);
                if(commit)using(var cmd=Command(c,tx,"""
INSERT dbo.Alson_Fintech_DspConnectionEvents(ReceiptKey,RawDispenserCode,MasterMappingID,MasterId,SiteID,PumpMappingID,RawStatus,GatewayReceivedAt)
VALUES(@receipt,@channel,@mapping,@master,@site,@pump,@status,@received)
""",Common().Concat(new[]{("@channel",(object)channel),("@pump",pump),("@status",frame.Diagnostic.Status)}).ToArray()))cmd.ExecuteNonQuery();
                count++;
            }
        }
        else if(frame.Command=="13")
        {
            if(commit)using(var cmd=Command(c,tx,"INSERT dbo.Alson_Fintech_AtgConnectionEvents(ReceiptKey,MasterMappingID,MasterId,SiteID,RawStatus,GatewayReceivedAt) VALUES(@receipt,@mapping,@master,@site,@status,@received)",
                Common().Append(("@status",(object)frame.Diagnostic.Status)).ToArray()))cmd.ExecuteNonQuery();
            count=1;
        }
        else
        {
            object tank=null;
            using(var cmd=Command(c,tx,"SELECT q.TankMappingID FROM dbo.Alson_Fintech_ProbeConfig q JOIN dbo.Omni_Fintech_TankMapping m ON m.ID=q.TankMappingID WHERE q.MasterId=@master AND q.ProbeCode=@probe AND m.Omni_SiteID=@site",("@master",frame.Master),("@probe",frame.RecordId),("@site",siteId)))
                tank=cmd.ExecuteScalar();
            if(commit)using(var cmd=Command(c,tx,"""
INSERT dbo.Alson_Fintech_ProbeStatusEvents(ReceiptKey,MasterMappingID,MasterId,SiteID,ProbeCode,TankMappingID,StatusType,StatusValue,GatewayReceivedAt)
VALUES(@receipt,@mapping,@master,@site,@probe,@tank,@type,@value,@received)
""",Common().Concat(new[]{("@probe",(object)frame.RecordId),("@tank",tank),("@type",frame.Diagnostic.Status),("@value",frame.Diagnostic.StatusValue)}).ToArray()))cmd.ExecuteNonQuery();
            count=1;
        }
        return Finish("imported",count);
        (string,object)[] Common()=>[("@receipt",receipt),("@mapping",masterId),("@master",frame.Master),("@site",siteId),("@received",frame.GatewayReceivedAt)];
        object Pump(string raw)
        {
            using var cmd=Command(c,tx,"SELECT ID FROM dbo.Omni_Fintech_PumpMapping WHERE Omni_SiteID=@site AND MasterId COLLATE Latin1_General_100_BIN2=@master AND (AlsonPumpCode COLLATE Latin1_General_100_BIN2=@raw OR AlsonPumpCode COLLATE Latin1_General_100_BIN2=@alias)",("@site",siteId),("@master",frame.Master),("@raw",raw),("@alias","0"+raw));
            using var r=cmd.ExecuteReader();if(!r.Read())return null;var id=r.GetInt32(0);return r.Read()?null:id;
        }
        Result Finish(string outcome,int rows)
        {
            if(commit){Audit(c,tx,key,name,etag,envelopeHash,envelope,frame.Hash,outcome,null,batch,frame.Master,frame.Command);tx.Commit();}
            else tx.Rollback();
            return new(outcome,rows);
        }
    }
}
