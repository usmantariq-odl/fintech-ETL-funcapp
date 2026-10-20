using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using System.Security.Claims;
using System.Text;

internal sealed class TestHttpRequest(string body) : HttpRequestData(new TestFunctionContext())
{
    public override Stream Body { get; } = new MemoryStream(Encoding.UTF8.GetBytes(body));
    public override HttpHeadersCollection Headers { get; } = new();
    public override IReadOnlyCollection<IHttpCookie> Cookies => Array.Empty<IHttpCookie>();
    public override Uri Url => new("https://localhost/api/sftp/users");
    public override IEnumerable<ClaimsIdentity> Identities => Array.Empty<ClaimsIdentity>();
    public override string Method => "POST";
    public override HttpResponseData CreateResponse() => new TestHttpResponse();
}

internal sealed class TestHttpResponse() : HttpResponseData(new TestFunctionContext())
{
    public override HttpStatusCode StatusCode { get; set; }
    public override HttpHeadersCollection Headers { get; set; } = new();
    public override Stream Body { get; set; } = new MemoryStream();
    public override HttpCookies Cookies => throw new NotSupportedException();
}

internal sealed class TestFunctionContext : FunctionContext
{
    public override string InvocationId => "test-invocation";
    public override string FunctionId => "test-function";
    public override TraceContext TraceContext => null;
    public override BindingContext BindingContext => null;
    public override RetryContext RetryContext => null;
    public override IServiceProvider InstanceServices { get; set; }
    public override FunctionDefinition FunctionDefinition => null;
    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
    public override IInvocationFeatures Features => null;
}
