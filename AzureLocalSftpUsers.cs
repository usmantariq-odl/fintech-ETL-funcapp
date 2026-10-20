using Azure.Core;
using Azure.Identity;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FintechEtl.FunctionApp;

internal sealed record SftpProvisioningSettings(string SubscriptionId, string ResourceGroup, string Account)
{
    public static SftpProvisioningSettings Load()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
            : throw new ProvisioningException(503, "ProvisioningNotConfigured", $"Set the {name} application setting before using this endpoint.");
        var result = new SftpProvisioningSettings(Required("SftpProvisioningSubscriptionId"), Required("SftpProvisioningResourceGroup"), Required("SftpProvisioningStorageAccount"));
        if (!Guid.TryParse(result.SubscriptionId, out _) || result.ResourceGroup.Contains('/')
            || !System.Text.RegularExpressions.Regex.IsMatch(result.Account, "\\A[a-z0-9]{3,24}\\z"))
            throw new ProvisioningException(503, "ProvisioningNotConfigured", "The SFTP provisioning Azure resource settings are invalid.");
        // Ensure the management and SFTP data operations cannot accidentally target different accounts.
        if (!string.Equals(Environment.GetEnvironmentVariable("SftpHost"), result.Account + ".blob.core.windows.net", StringComparison.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("SftpUsername") != result.Account + ".sftpadmin")
            throw new ProvisioningException(503, "ProvisioningNotConfigured", "SftpHost and SftpUsername must select the provisioning account and its sftpadmin user.");
        return result;
    }
}

internal sealed class AzureLocalSftpUsers : ILocalSftpUsers
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private static readonly TokenCredential Credential = new DefaultAzureCredential();
    private const string Api = "api-version=2025-06-01";
    private readonly string root;
    public AzureLocalSftpUsers(SftpProvisioningSettings settings) => root =
        $"https://management.azure.com/subscriptions/{settings.SubscriptionId}/resourceGroups/{Uri.EscapeDataString(settings.ResourceGroup)}/providers/Microsoft.Storage/storageAccounts/{settings.Account}/localUsers";

    private string UserUrl(string username) => root + "/" + Uri.EscapeDataString(username);

    public async Task<bool> Exists(string username, CancellationToken cancellation)
    {
        using var response = await Send(HttpMethod.Get, UserUrl(username) + "?" + Api, null, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureSuccess(response, cancellation);
        return true;
    }

    public async Task<bool> IsSiteAssigned(string site, CancellationToken cancellation)
    {
        string url = root + "?" + Api;
        while (url != null)
        {
            using var response = await Send(HttpMethod.Get, url, null, cancellation);
            await EnsureSuccess(response, cancellation);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            foreach (var entry in json.RootElement.GetProperty("value").EnumerateArray())
            {
                if (!entry.GetProperty("properties").TryGetProperty("homeDirectory", out var directory)) continue;
                var home = directory.GetString()?.Trim('/');
                if (ContainsSite(home, "sftp-data/Unprocessed/" + site) || ContainsSite(home, "sftp-data/" + site)) return true;
            }
            url = json.RootElement.TryGetProperty("nextLink", out var next) ? next.GetString() : null;
            if (url != null && (!url.StartsWith(root + "?", StringComparison.OrdinalIgnoreCase)))
                throw new ProvisioningException(502, "InvalidAzureResponse", "Azure returned an unexpected pagination URL.");
        }
        return false;
    }

    internal static bool ContainsSite(string home, string siteRoot) => home != null
        && (home.Equals(siteRoot, StringComparison.Ordinal) || home.StartsWith(siteRoot + "/", StringComparison.Ordinal));

    public async Task<LocalSftpUser> Create(string username, string home, CancellationToken cancellation)
    {
        // No credential is enabled until ownership/ACL configuration has finished.
        using var response = await Send(HttpMethod.Put, UserUrl(username) + "?" + Api, new
        {
            properties = new { homeDirectory = home, allowAclAuthorization = true, permissionScopes = Array.Empty<object>(),
                hasSshPassword = false, hasSshKey = false, hasSharedKey = false, isNFSv3Enabled = false }
        }, cancellation);
        await EnsureSuccess(response, cancellation);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return new(username, home, json.RootElement.GetProperty("properties").GetProperty("userId").GetInt32());
    }

    public async Task<string> EnablePassword(LocalSftpUser user, CancellationToken cancellation)
    {
        // A unique primary group prevents incidental access via another site's owning group.
        using (var update = await Send(HttpMethod.Put, UserUrl(user.Name) + "?" + Api, new
        {
            properties = new { homeDirectory = user.HomeDirectory, groupId = user.UserId, allowAclAuthorization = true,
                permissionScopes = Array.Empty<object>(), extendedGroups = Array.Empty<int>(),
                hasSshPassword = true, hasSshKey = false, hasSharedKey = false, isNFSv3Enabled = false }
        }, cancellation)) await EnsureSuccess(update, cancellation);
        using var response = await Send(HttpMethod.Post, UserUrl(user.Name) + "/regeneratePassword?" + Api, new { }, cancellation);
        await EnsureSuccess(response, cancellation);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return json.RootElement.GetProperty("sshPassword").GetString();
    }

    public async Task Delete(string username, CancellationToken cancellation)
    {
        using var response = await Send(HttpMethod.Delete, UserUrl(username) + "?" + Api, null, cancellation);
        if (response.StatusCode != HttpStatusCode.NotFound) await EnsureSuccess(response, cancellation);
    }

    public async Task Disable(string username, CancellationToken cancellation)
    {
        using var response = await Send(HttpMethod.Put, UserUrl(username) + "?" + Api, new
        {
            properties = new { hasSshPassword = false, hasSshKey = false, hasSharedKey = false, isNFSv3Enabled = false }
        }, cancellation);
        await EnsureSuccess(response, cancellation);
    }

    private static async Task<HttpResponseMessage> Send(HttpMethod method, string url, object body, CancellationToken cancellation)
    {
        AccessToken token;
        try { token = await Credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), cancellation); }
        catch (AuthenticationFailedException) { throw new ProvisioningException(503, "AzureAuthenticationFailed", "Configure the Function App managed identity and its storage local-user role. For local development, sign in using Azure CLI."); }
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body != null) request.Content = JsonContent.Create(body);
        return await Http.SendAsync(request, cancellation);
    }

    private static Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellation)
    {
        // Raw ARM response bodies and authentication exceptions are not returned or logged.
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        var status = (int)response.StatusCode;
        throw new ProvisioningException(status == 429 ? 503 : 502,
            status is 401 or 403 ? "AzureAuthorizationFailed" : status == 409 ? "AzureConflict" : "AzureProvisioningFailed",
            status is 401 or 403 ? "The app identity lacks permission to manage SFTP local users on the configured storage account."
                : $"Azure local-user management returned HTTP {status}. Retry or inspect the Azure activity log.");
    }
}
