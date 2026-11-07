namespace FintechEtl.FunctionApp;

internal sealed record SiteFolderEntry(string Path, bool Directory, bool RegularFile, bool Link, int Owner, int Group, int Mode);

internal interface ISiteFolderStore
{
    bool Exists(string path);
    SiteFolderEntry Read(string path);
    IEnumerable<SiteFolderEntry> List(string path);
    void CreateDirectory(string path);
    void SetAccess(string path, int owner, int group, int mode);
    void DeleteDirectory(string path);
}

// Snapshot access metadata, never file contents. Existing files are never deleted or replaced.
internal sealed class SiteFolderAdoption(ISiteFolderStore store)
{
    private readonly Dictionary<string, SiteFolderEntry> original = new(StringComparer.Ordinal);
    private readonly List<string> changed = [];
    private readonly List<string> created = [];
    private string root;

    public void Inspect(string path)
    {
        root = path;
        if (!store.Exists(path)) return;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var next))
        {
            var item = store.Read(next);
            Validate(item);
            original.Add(next, item);
            if (item.Directory)
                foreach (var child in store.List(next))
                {
                    if (!child.Path.StartsWith(path + "/", StringComparison.Ordinal))
                        throw new ProvisioningException(409, "UnsafeSiteFolder", "A site entry resolves outside its assigned folder.");
                    Validate(child);
                    pending.Push(child.Path);
                }
        }
        if (!original[path].Directory)
            throw new ProvisioningException(409, "UnsafeSiteFolder", "The site path must be a directory.");
    }

    private static void Validate(SiteFolderEntry item)
    {
        if (item.Link || (!item.Directory && !item.RegularFile))
            throw new ProvisioningException(409, "UnsafeSiteFolder", "Symbolic links and special files cannot be adopted as a site folder.");
    }

    public void Apply(string siteRoot, int owner)
    {
        if (root != siteRoot) throw new InvalidOperationException("The site folder was not inspected.");
        // Reinspect before changing anything. Refuse additions/deletions or ACL changes since validation.
        var current = new SiteFolderAdoption(store);
        current.Inspect(root);
        if (original.Count != current.original.Count || original.Any(p => !current.original.TryGetValue(p.Key, out var now) || now != p.Value))
            throw new ProvisioningException(409, "SiteFolderChanged", "The site folder changed during provisioning. Retry after current file activity finishes.");
        foreach (var required in new[] { root, root + "/input", root + "/output" })
        {
            if (original.TryGetValue(required, out var existing))
            {
                if (!existing.Directory)
                    throw new ProvisioningException(409, "UnsafeSiteFolder", "The site, input and output paths must be directories.");
            }
        }
        foreach (var required in new[] { root, root + "/input", root + "/output" })
            if (!store.Exists(required))
            {
                store.CreateDirectory(required);
                created.Add(required);
            }
        foreach (var item in original.Values)
        {
            // A disappearing ETL input is harmless; do not recreate it or restore its contents.
            if (!store.Exists(item.Path)) continue;
            if (store.Read(item.Path) != item)
                throw new ProvisioningException(409, "SiteFolderChanged", "A site entry changed during provisioning. Retry shortly.");
            changed.Add(item.Path); // Include ambiguous/partially applied SFTP writes in rollback.
            SetAndVerify(item.Path, owner, item.Directory);
        }
        foreach (var path in created) SetAndVerify(path, owner, true);
    }

    private void SetAndVerify(string path, int owner, bool directory)
    {
        int mode = directory ? 448 : 384; // POSIX 0700 / 0600, represented as bit masks.
        store.SetAccess(path, owner, owner, mode);
        var actual = store.Read(path);
        if (actual.Link || actual.Directory != directory || actual.Owner != owner || actual.Group != owner || actual.Mode != mode)
            throw new ProvisioningException(502, "AclVerificationFailed", "Site ownership or private permissions could not be verified.");
    }

    public void Rollback()
    {
        var errors = new List<Exception>();
        foreach (var path in changed.AsEnumerable().Reverse())
            try
            {
                if (!store.Exists(path)) continue;
                var before = original[path];
                var now = store.Read(path);
                if (now.Link || now.Directory != before.Directory)
                    throw new IOException("A site entry changed type before ACL rollback.");
                store.SetAccess(path, before.Owner, before.Group, before.Mode);
                var restored = store.Read(path);
                if (restored.Owner != before.Owner || restored.Group != before.Group || restored.Mode != before.Mode)
                    throw new IOException("Site ACL rollback verification failed.");
            }
            catch (Exception ex) { errors.Add(ex); }
        foreach (var path in created.AsEnumerable().Reverse())
            try { if (store.Exists(path)) store.DeleteDirectory(path); } // Only empty directories made by this request.
            catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("Site ACL rollback needs administrator review.", errors);
    }
}
