using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FintechEtl.FunctionApp.Alson;

internal static class AlsonChecks
{
    internal static byte[] Envelope(string frame)=>JsonSerializer.SerializeToUtf8Bytes(new{rawFrame=frame,rawBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes(frame)),sha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(frame))).ToLowerInvariant()});
    internal static int Run()
    {
        int passed=0;void Test(string name,Action check){check();passed++;Console.WriteLine("PASS Alson: "+name);}
        void Assert(bool b,string text){if(!b)throw new Exception(text);}
        void Reject(string frame){try{AlsonParser.Parse(Envelope(frame));}catch(FormatException){return;}throw new Exception("Invalid frame accepted.");}
        Test("100 sales preserve repeated IDs across dispensers, decimals, midnight and source bytes",()=>
        {
            var hashes=new HashSet<string>();
            for(int i=0;i<100;i++)
            {
                var code=new[]{"0@","0A","0J","0P","0Q"}[i%5];
                var raw=$"#00,{i/5:0000},MASTER,{code},1,R,000001.23,0350.00,000430.51,00001000.00,00:00:00,2026-11-07,No|";
                var f=AlsonParser.Parse(Envelope(raw));
                Assert(f.Dispenser==code&&f.Nozzle==1&&f.Amount==430.51m&&f.Volume==1.23m&&f.Price==350m,"Sale value changed.");
                Assert(f.At.Hour==0&&f.Raw==raw&&f.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(raw)),"Time/raw changed.");
                Assert(hashes.Add(Convert.ToHexString(f.Hash)),"Valid different dispenser collided.");
            }
        });
        Test("100 ATG readings preserve corrected fuel/water/temp order and probe leading zeros",()=>
        {
            int readings=0;
            for(int i=0;i<25;i++)
            {
                var f=AlsonParser.Parse(Envelope($"#20,{i:0000},MASTER,12:00:00,2026-11-07~00002,1234,56,31.25~00004,2345,67,32.25~00007,3456,78,33.25~00009,4567,89,34.25~FFFFF,0,0,0~1|"));
                Assert(f.Probes.Count==4&&f.Probes[0]==new AlsonProbe("00002",1234,56,31.25m,1),"ATG order/identifier changed.");readings+=f.Probes.Count;
            }
            Assert(readings==100,"Wrong reading count.");
        });
        Test("identical frame under two envelopes hashes identically",()=>
        {
            var raw="#00,0001,M,0@,1,D,1,2,2,3,12:00:00,2026-11-07,No|";
            var a=AlsonParser.Parse(Envelope(raw));var b=AlsonParser.Parse(JsonSerializer.SerializeToUtf8Bytes(new{rawFrame=raw,messageId="different-name"}));
            Assert(a.Hash.SequenceEqual(b.Hash),"Envelope affected business identity.");
        });
        Test("unused probe slots preserve physical tank positions",()=>
        {
            var f=AlsonParser.Parse(Envelope("#20,1,M,12:00:00,2026-11-07~24696,1,0,30~FFFFF,0,0,0~24697,2,0,31~1|"));
            Assert(f.Probes.Count==2&&f.Probes[0].Position==1&&f.Probes[1].Position==3,"Unused slot shifted the physical tank numbering.");
        });
        Test("reject checksum mismatch and invalid UTF8",()=>
        {
            try{AlsonParser.Parse(JsonSerializer.SerializeToUtf8Bytes(new{rawFrame="#13,M,con|",sha256="bad"}));throw new Exception("Bad digest accepted.");}catch(FormatException){}
            try{AlsonParser.Parse(JsonSerializer.SerializeToUtf8Bytes(new{rawBase64="/w=="}));throw new Exception("Invalid UTF8 accepted.");}catch(DecoderFallbackException){}
        });
        Test("reject repeated, empty, unused-only probes and appended modem commands",()=>
        {
            Reject("#20,1,M,12:00:00,2026-11-07~00002,1,2,3~00002,4,5,6~1|");
            Reject("#20,1,M,12:00:00,2026-11-07~FFFFF,0,0,0~1|");
            Reject("#20,1,M,12:00:00,2026-11-07~,1,2,3~1|");
            Reject("#20,1,M,12:00:00,2026-11-07~00002,1,2,3~1|AT+QMTPUB");
        });
        Test("diagnostic fields and receipt identities preserve repeated heartbeats",()=>
        {
            var mode=AlsonParser.Parse(Envelope("#31,M,31,0002,A,1,1,10:01:32,2026-11-07|"));
            Assert(mode.Diagnostic.ModeCode==1&&mode.Diagnostic.Status=="Manual"&&mode.Diagnostic.Nozzle==null&&mode.Diagnostic.Channels[0]=="A","Mode fields changed.");
            var conn=AlsonParser.Parse(Envelope("#70,M,70,P,Q,Alive|"));
            Assert(conn.Diagnostic.Channels.SequenceEqual(new[]{"P","Q"})&&conn.Diagnostic.Status=="Alive","Connection channels lost.");
            var probe=AlsonParser.Parse(Envelope("#27,M,00004,STAT,2|"));
            Assert(probe.RecordId=="00004"&&probe.Master=="M"&&probe.Diagnostic.StatusValue=="2","Probe status changed.");
            var raw="#13,M,con|";
            AlsonFrame Receipt(string id)=>AlsonParser.Parse(JsonSerializer.SerializeToUtf8Bytes(new{rawFrame=raw,messageId=id,gatewayTs="2026-11-07T12:00:00+05:00"}));
            var a=Receipt("a");var b=Receipt("b");var sourceA=SHA256.HashData([1]);var sourceB=SHA256.HashData([2]);
            Assert(a.GatewayReceivedAt==DateTimeOffset.Parse("2026-11-07T07:00:00Z"),"Receipt timestamp offset changed.");
            Assert(a.Hash.SequenceEqual(b.Hash)&&!AlsonStore.DiagnosticReceiptKey(a,sourceA).SequenceEqual(AlsonStore.DiagnosticReceiptKey(b,sourceA)),"Distinct heartbeats collapsed.");
            Assert(AlsonStore.DiagnosticReceiptKey(a,sourceA).SequenceEqual(AlsonStore.DiagnosticReceiptKey(a,sourceB)),"Same receipt under another filename duplicated.");
            var missingId=AlsonParser.Parse(Envelope(raw));
            Assert(!AlsonStore.DiagnosticReceiptKey(missingId,sourceA).SequenceEqual(AlsonStore.DiagnosticReceiptKey(missingId,sourceB)),"Missing-ID fallback collapsed files.");
            Reject("#27,M,00004,STAT|");Reject("#13,M,con|AT+X");Reject("#31,M,31,1,A,2,1,10:01:32,2026-11-07|");
        });
        Test("flat ATG and operating mode remain separate",()=>
        {
            Assert(AlsonParser.Parse(Envelope("#20,1,M,12:00:00,2026-11-07,00002,123,4,32.5|")).Probes[0].Fuel==123,"Flat ATG wrong.");
            Assert(AlsonParser.Parse(Envelope("#31,M,31,1,A,3,0,12:00:00,2026-11-07|")).Command=="31","Mode event became business data.");
        });
        Test("reject invalid dates, non-finite values and invalid integer IDs",()=>
        {
            Reject("#00,1,M,0A,1,R,NaN,2,2,3,12:00:00,2026-11-07,No|");
            Reject("#00,1,M,0A,1,R,1,2,2,3,12:00:00,2026-02-30,No|");
            Reject("#00,999999999999,M,0A,1,R,1,2,2,3,12:00:00,2026-11-07,No|");
        });
        Test("Alson mail labels, HTML encoding and credentials redaction",()=>
        {
            var mail=AlsonAlerts.Render(["<script> Password=do-not-expose;"],"test-run");
            Assert(mail.Subject.Contains("Alson STAGING")&&mail.Text.Contains("FintechStagDB_NewArchitecture"),"Wrong email environment.");
            Assert(!mail.Text.Contains("do-not-expose")&&!mail.Html.Contains("<script>"),"Email leaked secret/HTML.");
        });
        Test("enabled Alson refuses a production connection without opening SQL",()=>
        {
            string[] keys=["AlsonEtlEnabled","AlsonEtlDryRun","AlsonSqlConnectionString","AlsonExpectedDatabase"];
            var old=keys.ToDictionary(k=>k,Environment.GetEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable("AlsonEtlEnabled","true");Environment.SetEnvironmentVariable("AlsonEtlDryRun","true");
                Environment.SetEnvironmentVariable("AlsonSqlConnectionString","Server=example;Database=FintechProdDB;Integrated Security=true");
                Environment.SetEnvironmentVariable("AlsonExpectedDatabase","FintechProdDB");
                try{AlsonSettings.Load();throw new Exception("Production database allowed.");}
                catch(InvalidOperationException ex)when(ex.Message.Contains("staging")){}
            }
            finally{foreach(var key in keys)Environment.SetEnvironmentVariable(key,old[key]);}
        });
        return passed;
    }
}
