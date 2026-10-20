using System.Net;
using System.Text;
using System.Text.Json;
using CSVReader;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace FintechEtl.FunctionApp;

public sealed class CreateSftpUser(ILogger<CreateSftpUser> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<SftpUserProvisioner> provisionerFactory;
    internal CreateSftpUser(ILogger<CreateSftpUser> logger, Func<SftpUserProvisioner> provisionerFactory) : this(logger)
        => this.provisionerFactory = provisionerFactory;

    [Function("CreateSftpUser")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "sftp/users")] HttpRequestData request,
        CancellationToken cancellation)
    {
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            // Bound the body even for chunked requests. Credentials/paths in the request are not accepted.
            var bytes = new byte[4097];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await request.Body.ReadAsync(bytes.AsMemory(count), cancellation);
                if (read == 0) break;
                count += read;
            }
            if (count > 4096) throw new ProvisioningException(413, "RequestTooLarge", "The request body must be at most 4096 bytes.");
            var input = SftpUserRequest.Parse(Encoding.UTF8.GetString(bytes, 0, count));
            var service = provisionerFactory?.Invoke() ?? CreateService();
            var user = await service.Create(input, cancellation);
            logger.LogInformation("SFTP provisioning succeeded. RequestId={RequestId}; SiteId={SiteId}; Username={Username}", requestId, user.SiteId, user.Username);
            return await Reply(request, 201, new { success = true, user.SiteId, user.Username, user.LoginUsername,
                user.Host, user.Port, user.HomeDirectory, user.Password, error = (object)null, requestId });
        }
        catch (ProvisioningException ex)
        {
            logger.LogWarning("SFTP provisioning rejected. RequestId={RequestId}; Code={Code}", requestId, ex.Code);
            return await Reply(request, ex.Status, new { success = false, password = (string)null,
                error = new { code = ex.Code, message = ex.Message }, requestId });
        }
        catch (Exception ex)
        {
            // Do not log exception objects: SDK errors can include request bodies or credentials.
            var type = ex.GetType().Name;
            logger.LogError("SFTP provisioning failed. RequestId={RequestId}; ExceptionType={ExceptionType}", requestId, type);
            return await Reply(request, 500, new { success = false, password = (string)null,
                error = new { code = "ProvisioningFailed", type, message = "SFTP user provisioning failed. Check application configuration, Azure permissions and SFTP connectivity." }, requestId });
        }
    }

    private static SftpUserProvisioner CreateService()
    {
        var provisioning = SftpProvisioningSettings.Load();
        var etl = EtlSettings.Load();
        if (string.IsNullOrWhiteSpace(etl.SqlConnectionString))
            throw new ProvisioningException(503, "ProvisioningNotConfigured", "FintechSqlConnectionString is required for the distributed provisioning lock.");
        return new SftpUserProvisioner(new AzureLocalSftpUsers(provisioning),
            () => new SiteSftpFolders(etl, provisioning.Account), () => new SqlProvisioningLease(etl.SqlConnectionString),
            provisioning.Account, etl.Host, etl.Port);
    }

    private static async Task<HttpResponseData> Reply(HttpRequestData request, int status, object result)
    {
        var response = request.CreateResponse((HttpStatusCode)status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        response.Headers.Add("Pragma", "no-cache");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
        return response;
    }
}
