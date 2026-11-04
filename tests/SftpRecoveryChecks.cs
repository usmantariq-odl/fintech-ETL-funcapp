using System.Runtime.ExceptionServices;
using CSVReader;
using FintechEtl.FunctionApp;
using Renci.SshNet.Common;

internal static class SftpRecoveryChecks
{
    internal static Exception Closed() => ExceptionDispatchInfo.SetRemoteStackTrace(
        new InvalidOperationException("The session is not open."),
        "   at Renci.SshNet.Sftp.SftpSession.GetCanonicalPath(String path)\n");

    internal static int Run()
    {
        int passed=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        void Test(string name,Action body){body();passed++;Console.WriteLine("PASS SFTP recovery: "+name);}
        Test("closed subsystem with connected SSH flag reconnects and probes",()=>
        {
            bool connected=true;int probes=0,connects=0,resets=0;
            SftpSessionRecovery.EnsureReady(()=>connected,()=>{connected=true;connects++;},
                ()=>{if(++probes==1)throw Closed();},()=>{connected=false;resets++;},delay:_=>{});
            Check(probes==2 && connects==1 && resets==1,"Stale SSH flag bypassed session recovery.");
        });
        Test("disconnected transport is opened before probing",()=>
        {
            bool connected=false;int connects=0;
            SftpSessionRecovery.EnsureReady(()=>connected,()=>{connected=true;connects++;},()=>Check(connected,"Probed a disconnected transport."),()=>connected=false,delay:_=>{});
            Check(connects==1,"Unexpected reconnect count.");
        });
        Test("persistent closed session retries three times, remains retryable and does not email",()=>
        {
            int attempts=0;bool failed=false;
            try{SftpSessionRecovery.EnsureReady(()=>true,()=>{},()=>{attempts++;throw Closed();},()=>{},delay:_=>{});}
            catch(RetryableEtlException ex){failed=true;Check(!EtlFailurePolicy.ShouldEmail(ex),"Temporary session loss sends email.");}
            Check(failed && attempts==3,"Recovery is unbounded or silently succeeded.");
            Check(!EtlFailurePolicy.ShouldEmail(new RetryableEtlException("Network retry",new System.Net.Sockets.SocketException(10054))),"Temporary socket failure sends email.");
        });
        Test("authentication errors escape immediately and still email",()=>
        {
            int attempts=0;bool failed=false;
            try{SftpSessionRecovery.EnsureReady(()=>false,()=>{attempts++;throw new SshAuthenticationException("Denied");},()=>{},()=>{},delay:_=>{});}
            catch(SshAuthenticationException ex){failed=true;Check(EtlFailurePolicy.ShouldEmail(ex),"Authentication alert suppressed.");}
            Check(failed && attempts==1,"Authentication was retried as a session failure.");
            var denied=new SftpPermissionDeniedException("Denied");
            Check(!SftpSessionRecovery.IsTransient(denied) && EtlFailurePolicy.ShouldEmail(denied),"Permanent SFTP permission failure was hidden as transient.");
        });
        Test("unrelated InvalidOperationException is not hidden",()=>
        {
            var error=new InvalidOperationException("The session is not open.");
            Check(!SftpSessionRecovery.IsTransient(error) && EtlFailurePolicy.ShouldEmail(error),"Message-only matching hid an application error.");
            Check(!EtlFailurePolicy.ShouldEmail(Closed()),"SSH.NET closed-session error emails.");
        });
        return passed;
    }
}
