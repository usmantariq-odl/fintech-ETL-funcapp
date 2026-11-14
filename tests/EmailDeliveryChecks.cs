using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using FintechEtl.FunctionApp;

internal sealed class FakeEtlEmailTransport : IEtlEmailTransport
{
    internal int Starts, Polls;
    internal Func<EtlEmailEnvelope,Task<string>> OnStart;
    internal Func<Guid,Task<string>> OnPoll;
    public Task<string> Start(EtlEmailEnvelope item,CancellationToken ct)
    { Interlocked.Increment(ref Starts); return OnStart?.Invoke(item)??Task.FromResult(item.OperationId.ToString()); }
    public Task<string> Poll(Guid id,CancellationToken ct)
    { Interlocked.Increment(ref Polls); return OnPoll?.Invoke(id)??Task.FromResult("succeeded"); }
}

internal static class EmailDeliveryChecks
{
    internal static EtlEmailEnvelope Item()=>new(){Alert=new("Partial","900003","input.tgz","Retained","Empty CSV","run",DateTimeOffset.UtcNow),
        Request=new("from@example.com",["to@example.com"],"Subject","Body","<p>Body</p>")};
    internal static EtlEmailEnvelope Copy(EtlEmailEnvelope item)=>JsonSerializer.Deserialize<EtlEmailEnvelope>(JsonSerializer.Serialize(item));
    internal static int Run()
    {
        int passed=0;
        void Check(bool condition,string reason){if(!condition)throw new Exception(reason);}
        void Test(string name,Func<Task> action){action().GetAwaiter().GetResult();passed++;Console.WriteLine("PASS email delivery: "+name);}
        var now=DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        Test("durable intent is stored before POST; accepted operation is polled after restart",async()=>
        {
            var item=Item();EtlEmailEnvelope saved=null;var transport=new FakeEtlEmailTransport();
            transport.OnStart=e=>{Check(saved?.State=="submitting"&&saved.OperationId==e.OperationId,"POST before durable intent");return Task.FromResult(e.OperationId.ToString());};
            Task Save(){saved=Copy(item);return Task.CompletedTask;}
            await EtlEmailDelivery.Advance(item,transport,Save,now,default);
            Check(item.State=="accepted"&&transport.Starts==1&&transport.Polls==0,"Submission waited for completion");
            item=Copy(saved);await EtlEmailDelivery.Advance(item,transport,Save,now.AddMinutes(1),default);
            Check(item.State=="succeeded"&&transport.Starts==1&&transport.Polls==1,"Accepted mail resubmitted");
        });
        Test("failed intent write makes zero service requests",async()=>
        {
            var transport=new FakeEtlEmailTransport();bool threw=false;
            try{await EtlEmailDelivery.Advance(Item(),transport,()=>throw new IOException("Storage down"),now,default);}catch(IOException){threw=true;}
            Check(threw&&transport.Starts==0,"Service called before storage commit");
        });
        Test("acceptance then lost response resumes operation without another POST",async()=>
        {
            var item=Item();var transport=new FakeEtlEmailTransport{OnStart=_=>throw new TaskCanceledException("Response lost after acceptance")};
            Task Save()=>Task.CompletedTask;
            await EtlEmailDelivery.Advance(item,transport,Save,now,default);var id=item.OperationId;
            item=Copy(item);await EtlEmailDelivery.Advance(item,transport,Save,now.AddMinutes(1),default);
            Check(item.State=="succeeded"&&transport.Starts==1&&item.OperationId==id,"Ambiguous acceptance resent");
        });
        Test("429 during polling backs off across restarts, never sends again",async()=>
        {
            var item=Item();var transport=new FakeEtlEmailTransport();Task Save()=>Task.CompletedTask;
            await EtlEmailDelivery.Advance(item,transport,Save,now,default);
            transport.OnPoll=_=>throw new RequestFailedException(429,"Rate limited","TooManyRequests",null);
            await EtlEmailDelivery.Advance(item,transport,Save,now.AddMinutes(1),default);
            var due=item.NextAttempt;item=Copy(item);
            await EtlEmailDelivery.Advance(item,transport,Save,now.AddMinutes(2),default);
            Check(transport.Starts==1&&transport.Polls==1&&due>=now.AddMinutes(6),"Polling throttle caused send/retry storm");
            transport.OnPoll=_=>Task.FromResult("succeeded");await EtlEmailDelivery.Advance(item,transport,Save,due,default);
            Check(item.State=="succeeded"&&transport.Starts==1,"Recovery resent mail");
        });
        Test("initial POST 429 is retried only after backoff as a rejected send",async()=>
        {
            var item=Item();var transport=new FakeEtlEmailTransport{OnStart=_=>throw new RequestFailedException(429,"Rate limited","TooManyRequests",null)};
            Task Save()=>Task.CompletedTask;await EtlEmailDelivery.Advance(item,transport,Save,now,default);
            var request=item.RequestId;var operation=item.OperationId;
            await EtlEmailDelivery.Advance(item,transport,Save,now.AddMinutes(1),default);
            Check(item.State=="new"&&transport.Starts==1,"Initial throttle retried too early");
            transport.OnStart=null;await EtlEmailDelivery.Advance(item,transport,Save,item.NextAttempt,default);
            Check(transport.Starts==2&&item.State=="accepted"&&operation==item.OperationId&&request!=item.RequestId,"Rejected send lost retry identity");
        });
        Test("service Retry-After longer than local backoff is honored",async()=>
        {
            var item=Item();var failure=new RequestFailedException(429,"Rate limited","TooManyRequests",null);failure.Data["EtlRetryAfterSeconds"]=7200d;
            var transport=new FakeEtlEmailTransport{OnStart=_=>throw failure};
            await EtlEmailDelivery.Advance(item,transport,()=>Task.CompletedTask,now,default);
            Check(item.NextAttempt==now.AddHours(2),"Ignored Retry-After");
        });
        Test("unknown POST retries only inside repeatability window with unchanged headers/body",async()=>
        {
            var item=Item();var transport=new FakeEtlEmailTransport{OnStart=_=>throw new IOException("Unknown network result"),OnPoll=_=>Task.FromResult("notfound")};Task Save()=>Task.CompletedTask;
            await EtlEmailDelivery.Advance(item,transport,Save,now,default);var before=Copy(item);
            transport.OnStart=e=>Task.FromResult(e.OperationId.ToString());
            await EtlEmailDelivery.Advance(item,transport,Save,now.AddMinutes(1),default);
            Check(item.OperationId==before.OperationId&&item.RequestId==before.RequestId&&item.FirstSent==before.FirstSent&&item.Request==before.Request||
                item.OperationId==before.OperationId&&item.RequestId==before.RequestId&&item.FirstSent==before.FirstSent&&JsonSerializer.Serialize(item.Request)==JsonSerializer.Serialize(before.Request),"Unsafe repeatability retry");
            Check(transport.Starts==2&&item.State=="accepted","Repeatable retry did not recover");
        });
        Test("expired unknown outcome is retained for review, never blindly resubmitted",async()=>
        {
            var item=Item();var transport=new FakeEtlEmailTransport{OnStart=_=>throw new IOException(),OnPoll=_=>Task.FromResult("notfound")};Task Save()=>Task.CompletedTask;
            await EtlEmailDelivery.Advance(item,transport,Save,now,default);
            await EtlEmailDelivery.Advance(item,transport,Save,now.AddMinutes(5),default);
            await EtlEmailDelivery.Advance(item,transport,Save,now.AddDays(1),default);
            Check(item.State=="review"&&transport.Starts==1&&transport.Polls==1,"Expired request resubmitted");
        });
        Test("accepted but missing/failed operation cannot cause a fresh send",async()=>
        {
            foreach(var status in new[]{"notfound","failed"})
            {
                var item=Item();var transport=new FakeEtlEmailTransport{OnPoll=_=>Task.FromResult(status)};Task Save()=>Task.CompletedTask;
                await EtlEmailDelivery.Advance(item,transport,Save,now,default);await EtlEmailDelivery.Advance(item,transport,Save,now.AddMinutes(1),default);
                await EtlEmailDelivery.Advance(item,transport,Save,now.AddDays(2),default);
                Check(EtlEmailDelivery.Terminal(item.State)&&transport.Starts==1,"Accepted or terminal operation resent");
            }
        });
        Test("completion marker prevents resending when cleanup is retried",async()=>
        {
            var item=Item();item.State="succeeded";var transport=new FakeEtlEmailTransport();
            await EtlEmailDelivery.Advance(item,transport,()=>Task.CompletedTask,now,default);
            Check(transport.Starts==0&&transport.Polls==0,"Completed event made service request");
        });
        Test("completion save failure can recover by polling the same accepted operation",async()=>
        {
            var item=Item();var transport=new FakeEtlEmailTransport();EtlEmailEnvelope durable=null;
            Task Save(){durable=Copy(item);return Task.CompletedTask;}
            await EtlEmailDelivery.Advance(item,transport,Save,now,default);
            try{await EtlEmailDelivery.Advance(item,transport,()=>throw new IOException(),now.AddMinutes(1),default);}catch(IOException){}
            item=Copy(durable);await EtlEmailDelivery.Advance(item,transport,Save,now.AddMinutes(2),default);
            Check(item.State=="succeeded"&&transport.Starts==1,"Completion persistence failure resent");
        });
        Test("same event key ignores rendering time, but distinguishes new invocations",()=>
        {
            var a=Item().Alert;Check(EtlErrorEmails.EventKey(a)==EtlErrorEmails.EventKey(a with {At=a.At.AddHours(1)}),"Timestamp creates duplicate events");
            Check(EtlErrorEmails.EventKey(a)!=EtlErrorEmails.EventKey(a with {RunId="other"}),"New real event suppressed");return Task.CompletedTask;
        });
        Test("real SDK POST starts once; resumed GET uses operation ID and stable repeatability headers",async()=>
        {
            var item=Item();item.RequestId=Guid.NewGuid();item.FirstSent=now;var handler=new EmailHandler();
            using var http=new HttpClient(handler);var gateway=new AcsEtlEmailTransport("endpoint=https://mail.example.com/;accesskey="+Convert.ToBase64String(new byte[32]),new HttpClientTransport(http));
            var id=await gateway.Start(item,default);Check(handler.Requests.Count==1,"WaitUntil.Completed still polling");
            Check(id==item.OperationId.ToString(),"Wrong operation ID");
            Check(await gateway.Poll(item.OperationId,default)=="succeeded"&&handler.Requests.Count==2,"Resumed operation did not GET");
            await gateway.Start(item,default);
            var first=handler.Requests[0];var repeated=handler.Requests[2];
            Check(first.Method=="POST"&&handler.Requests[1].Method=="GET"&&first.Id==item.RequestId.ToString()&&
                first.First==now.UtcDateTime.ToString("R",System.Globalization.CultureInfo.InvariantCulture)&&
                first.Id==repeated.Id&&first.First==repeated.First&&first.Body==repeated.Body,"Repeatability identity/body changed");
            handler.ThrottlePoll=true;
            bool throttled=false;
            try{await gateway.Poll(item.OperationId,default);}catch(RequestFailedException ex){throttled=ex.Status==429&&ex.Data["EtlRetryAfterSeconds"] is double delay&&delay==7200;}
            Check(throttled,"SDK polling error did not retain Retry-After");
        });
        return passed;
    }
    private sealed class EmailHandler : HttpMessageHandler
    {
        internal readonly List<(string Method,string Id,string First,string Body)> Requests=[];
        internal bool ThrottlePoll;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var id=request.Method==HttpMethod.Post?request.Headers.GetValues("Operation-Id").Single():request.RequestUri.Segments.Last();
            Requests.Add((request.Method.Method,request.Headers.TryGetValues("Repeatability-Request-ID",out var ids)?ids.Single():null,
                request.Headers.TryGetValues("Repeatability-First-Sent",out var dates)?dates.Single():null,request.Content==null?null:await request.Content.ReadAsStringAsync(ct)));
            if(ThrottlePoll&&request.Method==HttpMethod.Get)
            {
                var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent("{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"Simulated\"}}",Encoding.UTF8,"application/json")};
                response.Headers.TryAddWithoutValidation("Retry-After","7200");return response;
            }
            return new HttpResponseMessage(request.Method==HttpMethod.Post?HttpStatusCode.Accepted:HttpStatusCode.OK)
            {Content=new StringContent(JsonSerializer.Serialize(new{id,status=request.Method==HttpMethod.Post?"Running":"Succeeded"}),Encoding.UTF8,"application/json")};
        }
    }
}
