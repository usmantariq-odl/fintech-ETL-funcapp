using System.Text.Json;
using CSVReader;

namespace FintechEtl.FunctionApp;

internal sealed record SftpUserRequest(string Username)
{
    public string SiteId => Username;
    public string SftpUsername => "user" + Username;

    public static SftpUserRequest Parse(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            string username = null;
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("username", StringComparison.OrdinalIgnoreCase))
                {
                    if (username != null || property.Value.ValueKind != JsonValueKind.String) throw Invalid();
                    username = property.Value.GetString();
                }
                else if (property.Name.Equals("password", StringComparison.OrdinalIgnoreCase))
                    throw new ProvisioningException(400, "CustomPasswordNotSupported", "Azure Blob SFTP does not support caller-chosen passwords. Omit password to receive an Azure-generated SFTP password.");
                else throw Invalid();
            }
            var request = new SftpUserRequest(username);
            request.Validate();
            return request;
        }
        catch (JsonException) { throw Invalid(); }
    }

    public void Validate()
    {
        if (!SftpLayout.IsSiteId(Username))
            throw new ProvisioningException(400, "InvalidUsername", "username is the site ID and must be a string of digits representing a positive integer up to 2147483647. Leading zeros are allowed and preserved. The SFTP username is generated as user{username}.");
    }

    private static ProvisioningException Invalid() => new(400, "InvalidRequest", "Provide a JSON object containing only username; it is also the site ID. Azure generates the SFTP password.");
}

internal sealed class ProvisioningException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

internal sealed record ProvisionedUser(string SiteId, string Username, string LoginUsername, string Host, int Port, string HomeDirectory, string Password);
internal sealed record LocalSftpUser(string Name, string HomeDirectory, int UserId);

internal interface ILocalSftpUsers
{
    Task<bool> Exists(string username, CancellationToken cancellation);
    Task<bool> IsSiteAssigned(string site, CancellationToken cancellation);
    Task<LocalSftpUser> Create(string username, string home, CancellationToken cancellation);
    Task<string> EnablePassword(LocalSftpUser user, CancellationToken cancellation);
    Task Disable(string username, CancellationToken cancellation);
    Task Delete(string username, CancellationToken cancellation);
}

internal interface ISiteSftpFolders : IDisposable
{
    void ValidateNewSite(string site);
    void Create(string site, int ownerId);
    void Verify(string site, string username, string password);
    void Rollback();
}

internal interface IProvisioningLease : IDisposable { void Verify(); }

// All calls from this app serialize through a session SQL lock, including scaled-out hosts.
internal sealed class SqlProvisioningLease : IProvisioningLease
{
    private readonly SiteLease lease;
    public SqlProvisioningLease(string connectionString) => lease = SiteLease.TryAcquireResource(connectionString, "FintechEtl:sftp-provisioning")
        ?? throw new ProvisioningException(409, "ProvisioningBusy", "Another SFTP user is being provisioned. Retry shortly.");
    public void Verify() => lease.Verify();
    public void Dispose() => lease.Dispose();
}

internal sealed class SftpUserProvisioner(ILocalSftpUsers users, Func<ISiteSftpFolders> foldersFactory,
    Func<IProvisioningLease> leaseFactory, string account, string host, int port)
{
    public async Task<ProvisionedUser> Create(SftpUserRequest request, CancellationToken cancellation)
    {
        request.Validate();
        using var lease = leaseFactory();
        if (await users.Exists(request.SftpUsername, cancellation))
            throw new ProvisioningException(409, "UserAlreadyExists", "The SFTP username already exists. Its password and access were not changed.");
        if (await users.IsSiteAssigned(request.SiteId, cancellation))
            throw new ProvisioningException(409, "SiteAlreadyAssigned", "An SFTP user is already assigned to this site, including the previous folder layout.");
        using var folders = foldersFactory();
        folders.ValidateNewSite(request.SiteId);
        var home = "sftp-data" + SftpLayout.SiteRoot(request.SiteId);
        var created = false;
        try
        {
            lease.Verify();
            var user = await users.Create(request.SftpUsername, home, cancellation);
            created = true;
            if (user.UserId <= 0) throw new ProvisioningException(502, "InvalidAzureResponse", "Azure did not return a valid local user ID.");
            lease.Verify();
            cancellation.ThrowIfCancellationRequested();
            folders.Create(request.SiteId, user.UserId);
            lease.Verify();
            var password = await users.EnablePassword(user, cancellation);
            if (string.IsNullOrEmpty(password)) throw new ProvisioningException(502, "InvalidAzureResponse", "Azure did not return the generated password.");
            cancellation.ThrowIfCancellationRequested();
            folders.Verify(request.SiteId, request.SftpUsername, password);
            lease.Verify();
            return new(request.SiteId, request.SftpUsername, account + "." + request.SftpUsername, host, port, home, password);
        }
        catch
        {
            if (created)
            {
                try
                {
                    // Revoke authentication before removing folders, keeping the UID resolvable until
                    // its directories are gone. Remove only empty directories created by this request.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    try { await users.Disable(request.SftpUsername, cleanup.Token); }
                    catch
                    {
                        await users.Delete(request.SftpUsername, cleanup.Token);
                        throw; // Folder cleanup is unsafe after uncertain credential revocation.
                    }
                    try { folders.Rollback(); }
                    finally { await users.Delete(request.SftpUsername, cleanup.Token); }
                }
                catch
                {
                    throw new ProvisioningException(500, "ProvisioningCleanupFailed", "Provisioning failed and cleanup could not finish. An administrator must inspect this username and site before retrying.");
                }
            }
            throw;
        }
    }
}
