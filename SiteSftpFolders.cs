using CSVReader;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using System.Security.Cryptography;

namespace FintechEtl.FunctionApp;

internal sealed class SiteSftpFolders : ISiteSftpFolders
{
    private readonly EtlSettings settings;
    private readonly string account;
    private readonly SftpClient admin;
    private readonly List<string> created = [];
    private readonly List<(string Path, int Owner, int Group, bool GroupAccess, bool IsDirectory)> siblings = [];
    private (int Owner, int Group, bool GroupAccess) conRoot;

    public SiteSftpFolders(EtlSettings settings, string account)
    {
        this.settings = settings;
        this.account = account;
        admin = Connect(settings.Username, settings.Password);
    }

    private SftpClient Connect(string username, string password) => OpenClient(settings, username, password);

    internal static SftpClient OpenClient(EtlSettings settings, string username, string password)
    {
        var client = new SftpClient(settings.Host, settings.Port, username, password)
        {
            OperationTimeout = TimeSpan.FromSeconds(20), KeepAliveInterval = TimeSpan.FromSeconds(15)
        };
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
        client.HostKeyReceived += (_, e) =>
        {
            var actual = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            e.CanTrust = settings.HostKeySha256.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(expected => actual.Equals(expected.TrimEnd('='), StringComparison.Ordinal));
        };
        try { client.Connect(); return client; }
        catch { client.Dispose(); throw; }
    }

    public void ValidateNewSite(string site)
    {
        if (admin.WorkingDirectory.TrimEnd('/') != "")
            throw new ProvisioningException(503, "ProvisioningNotConfigured", "The sftpadmin home directory must be the sftp-data container root.");
        if (admin.Exists(SftpLayout.SiteRoot(site)))
            throw new ProvisioningException(409, "SiteAlreadyExists", "The site folder already exists. Its files and ownership were not changed.");
        // Fail closed if another branch is publicly accessible. The common ancestors require
        // execute permission, so every sibling branch must protect its own contents.
        foreach (var entry in admin.ListDirectory("/").Where(f => f.Name is not "." and not ".." && f.Name != "Unprocessed"))
            CheckPrivateBranch(entry.FullName, entry.Attributes);
        if (admin.Exists(SftpLayout.UnprocessedRoot))
        {
            foreach (var entry in admin.ListDirectory(SftpLayout.UnprocessedRoot).Where(f => f.Name is not "." and not ".."))
                CheckPrivateBranch(entry.FullName, entry.Attributes);
        }
        using var conAdmin = Connect(account + ".con-files.sftpadmin", settings.Password);
        var con = conAdmin.GetAttributes("/");
        if (con.OthersCanRead || con.OthersCanWrite || con.OthersCanExecute)
            throw new ProvisioningException(503, "UnsafeContainerAcl", "The CON container grants access to other users. An administrator must secure its root ACL before provisioning.");
        conRoot = (con.UserId, con.GroupId, con.GroupCanRead || con.GroupCanWrite || con.GroupCanExecute);
    }

    private void CheckPrivateBranch(string path, SftpFileAttributes attributes)
    {
        if (attributes.IsSymbolicLink || attributes.OthersCanRead || attributes.OthersCanWrite || attributes.OthersCanExecute)
            throw new ProvisioningException(503, "UnsafeContainerAcl", "A sibling folder/file grants access to other users. An administrator must secure the container ACLs before provisioning.");
        siblings.Add((path, attributes.UserId, attributes.GroupId, attributes.GroupCanRead || attributes.GroupCanWrite || attributes.GroupCanExecute, attributes.IsDirectory));
    }

    public void Create(string site, int ownerId)
    {
        foreach (var ancestor in new[] { "/", SftpLayout.UnprocessedRoot }.Where(admin.Exists))
        {
            var a = admin.GetAttributes(ancestor);
            if (a.UserId == ownerId || a.GroupId == ownerId && (a.GroupCanRead || a.GroupCanWrite))
                throw new ProvisioningException(503, "UnsafeContainerAcl", "The allocated user/group ID has access to a common ancestor.");
        }
        if (siblings.Any(s => s.Owner == ownerId || s.Group == ownerId && s.GroupAccess)
            || conRoot.Owner == ownerId || conRoot.Group == ownerId && conRoot.GroupAccess)
            throw new ProvisioningException(503, "UnsafeContainerAcl", "The allocated user/group ID already owns or can access another branch.");
        // Enable traversal only. This does not permit listing the common ancestors or creating
        // another site. Existing branches retain their original owners and permissions.
        SetTraversalOnly("/");
        if (!admin.Exists(SftpLayout.UnprocessedRoot)) admin.CreateDirectory(SftpLayout.UnprocessedRoot);
        SetTraversalOnly(SftpLayout.UnprocessedRoot);
        foreach (var path in new[] { SftpLayout.SiteRoot(site), SftpLayout.Input(site), SftpLayout.Output(site) })
        {
            if (admin.Exists(path)) throw new ProvisioningException(409, "SiteAlreadyExists", "A site folder appeared during provisioning. No existing folder was reassigned.");
            admin.CreateDirectory(path);
            created.Add(path);
            var attributes = admin.GetAttributes(path);
            attributes.UserId = ownerId;
            attributes.GroupId = ownerId;
            attributes.OwnerCanRead = attributes.OwnerCanWrite = attributes.OwnerCanExecute = true;
            attributes.GroupCanRead = attributes.GroupCanWrite = attributes.GroupCanExecute = false;
            attributes.OthersCanRead = attributes.OthersCanWrite = attributes.OthersCanExecute = false;
            admin.SetAttributes(path, attributes);
            var actual = admin.GetAttributes(path);
            if (actual.UserId != ownerId || actual.GroupId != ownerId || !actual.OwnerCanRead || !actual.OwnerCanWrite || !actual.OwnerCanExecute
                || actual.GroupCanRead || actual.GroupCanWrite || actual.GroupCanExecute
                || actual.OthersCanRead || actual.OthersCanWrite || actual.OthersCanExecute)
                throw new ProvisioningException(502, "AclVerificationFailed", "The site directory ownership or permissions could not be verified.");
        }
    }

    private void SetTraversalOnly(string path)
    {
        var attributes = admin.GetAttributes(path);
        attributes.OthersCanRead = false;
        attributes.OthersCanWrite = false;
        attributes.OthersCanExecute = true;
        admin.SetAttributes(path, attributes);
    }

    public void Verify(string site, string username, string password)
    {
        using var user = Connect(account + "." + username, password);
        if (user.WorkingDirectory.TrimEnd('/') != SftpLayout.SiteRoot(site))
            throw new ProvisioningException(502, "AccessVerificationFailed", "The new user did not start in the expected site directory.");
        foreach (var path in new[] { SftpLayout.SiteRoot(site), SftpLayout.Input(site), SftpLayout.Output(site) })
            _ = user.ListDirectory(path).ToArray();
        // Verify actual create/read/delete permissions without leaving a file for the ETL.
        foreach (var path in new[] { SftpLayout.Input(site), SftpLayout.Output(site) })
        {
            var probe = path + "/.provisioning-" + Guid.NewGuid().ToString("N");
            using var content = new MemoryStream([42]);
            user.UploadFile(content, probe, false);
            try
            {
                using var read = new MemoryStream();
                user.DownloadFile(probe, read);
                if (!read.ToArray().SequenceEqual(new byte[] { 42 }))
                    throw new ProvisioningException(502, "AccessVerificationFailed", "The site write/read check failed.");
            }
            finally { user.DeleteFile(probe); }
        }
        RequireDenied(() => user.ListDirectory("/").ToArray());
        RequireDenied(() => user.ListDirectory(SftpLayout.UnprocessedRoot).ToArray());
        var probes = siblings.Where(s => s.Path.StartsWith(SftpLayout.UnprocessedRoot + "/", StringComparison.Ordinal)).Take(1)
            .Concat(siblings.Where(s => !s.Path.StartsWith(SftpLayout.UnprocessedRoot + "/", StringComparison.Ordinal)).Take(2));
        foreach (var sibling in probes)
        {
            // Every sibling ACL was inspected; representative SFTP reads also test enforcement.
            if (sibling.IsDirectory)
                RequireDenied(() => user.ListDirectory(sibling.Path).ToArray());
            else RequireDenied(() => { using var read = user.OpenRead(sibling.Path); });
        }
        // The separate CON container must remain inaccessible even with an explicit container login.
        SftpClient con = null;
        try
        {
            con = Connect(account + ".con-files." + username, password);
            RequireDenied(() => con.ListDirectory("/").ToArray());
        }
        catch (SftpPermissionDeniedException) { }
        catch (SshAuthenticationException) { }
        catch (SshConnectionException ex) when (ex.Message.Contains("requested container does not exist or is not accessible", StringComparison.OrdinalIgnoreCase)) { }
        finally { con?.Dispose(); }
    }

    private static void RequireDenied(Action operation)
    {
        try { operation(); }
        catch (SftpPermissionDeniedException) { return; }
        throw new ProvisioningException(502, "AccessVerificationFailed", "The new user could access a directory outside its assigned site.");
    }

    public void Rollback()
    {
        foreach (var path in created.AsEnumerable().Reverse())
        {
            if (!admin.IsConnected) admin.Connect();
            if (admin.Exists(path)) admin.DeleteDirectory(path); // Fails safely if nonempty.
        }
    }

    public void Dispose() => admin.Dispose();

}
