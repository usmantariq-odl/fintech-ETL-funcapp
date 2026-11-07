using FintechEtl.FunctionApp;

internal static class SiteFolderAdoptionChecks
{
    public static int Run()
    {
        int passed = 0;
        const string root = "/Unprocessed/007001";
        void Check(bool condition) { if (!condition) throw new Exception("Site adoption regression failed."); }
        void Run(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected rejection: " + code); }
            catch (ProvisioningException ex) { Check(ex.Code == code); }
        }
        Run("Adopt existing nested archives without changing bytes or sibling ACLs", () =>
        {
            var s = Existing(root); var sibling = s.Entries["/Unprocessed/007002"];
            var a = new SiteFolderAdoption(s); a.Inspect(root); a.Apply(root, 2345);
            Check(s.Entries.Where(x => x.Key == root || x.Key.StartsWith(root + "/"))
                .All(x => x.Value.Owner == 2345 && x.Value.Group == 2345 && x.Value.Mode == (x.Value.Directory ? 448 : 384)));
            Check(s.Contents.Single().Value == "original archive bytes" && s.Entries["/Unprocessed/007002"] == sibling);
            Check(s.Entries.ContainsKey(root + "/output"));
        });
        Run("Rollback restores existing ownership and removes only newly created empty directories", () =>
        {
            var s = Existing(root); var before = new Dictionary<string, SiteFolderEntry>(s.Entries);
            var a = new SiteFolderAdoption(s); a.Inspect(root); a.Apply(root, 2345); a.Rollback();
            Check(before.Count == s.Entries.Count && before.All(x => s.Entries[x.Key] == x.Value));
            Check(s.Contents.Single().Value == "original archive bytes");
        });
        Run("Partial ACL write failure restores the failed write as well", () =>
        {
            var s = Existing(root); var before = new Dictionary<string, SiteFolderEntry>(s.Entries);
            var a = new SiteFolderAdoption(s); a.Inspect(root); s.FailAfterWrite = root + "/input";
            try { a.Apply(root, 2345); throw new Exception("Expected write failure"); } catch (IOException) { }
            a.Rollback(); Check(before.Count == s.Entries.Count && before.All(x => s.Entries[x.Key] == x.Value));
        });
        Run("Fresh site still creates private root/input/output", () =>
        {
            var s = new Store(); var a = new SiteFolderAdoption(s); a.Inspect(root); a.Apply(root, 2345);
            Check(s.Entries.Count == 3 && s.Entries.Values.All(x => x.Directory && x.Owner == 2345 && x.Mode == 448));
            a.Rollback(); Check(s.Entries.Count == 0);
        });
        Run("Reject symlinks before changing ownership", () =>
        {
            var s = Existing(root); s.Entries[root + "/input/link"] = new(root + "/input/link", false, false, true, 1000, 1000, 511);
            Reject(() => new SiteFolderAdoption(s).Inspect(root), "UnsafeSiteFolder"); Check(s.Writes == 0);
        });
        Run("Reject site changes between inspection and adoption", () =>
        {
            var s = Existing(root); var a = new SiteFolderAdoption(s); a.Inspect(root);
            s.Entries[root + "/input/new.zip"] = new(root + "/input/new.zip", false, true, false, 1000, 1000, 384);
            Reject(() => a.Apply(root, 2345), "SiteFolderChanged"); Check(s.Writes == 0);
        });
        Run("Preserve and reject a file occupying the output directory path", () =>
        {
            var s = Existing(root); s.Entries[root + "/output"] = new(root + "/output", false, true, false, 1000, 1000, 384);
            var a = new SiteFolderAdoption(s); a.Inspect(root);
            Reject(() => a.Apply(root, 2345), "UnsafeSiteFolder"); Check(s.Writes == 0);
        });
        Run("Rollback never deletes new arriving data in a directory it created", () =>
        {
            var s = new Store(); var a = new SiteFolderAdoption(s); a.Inspect(root); a.Apply(root, 2345);
            s.Entries[root + "/input/arrived.zip"] = new(root + "/input/arrived.zip", false, true, false, 2345, 2345, 384);
            try { a.Rollback(); throw new Exception("Expected nonempty rollback failure"); } catch (AggregateException) { }
            Check(s.Entries.ContainsKey(root + "/input/arrived.zip"));
        });
        return passed;
    }

    private static Store Existing(string root)
    {
        var s = new Store();
        foreach (var p in new[] { root, root + "/input", root + "/input/duplicate", "/Unprocessed/007002" })
            s.Entries[p] = new(p, true, false, false, 1000, 1001, 488);
        var file = root + "/input/duplicate/file.tgz";
        s.Entries[file] = new(file, false, true, false, 1000, 1001, 416);
        s.Contents[file] = "original archive bytes";
        return s;
    }

    private sealed class Store : ISiteFolderStore
    {
        public Dictionary<string, SiteFolderEntry> Entries { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Contents { get; } = new(StringComparer.Ordinal);
        public string FailAfterWrite;
        public int Writes;
        public bool Exists(string path) => Entries.ContainsKey(path);
        public SiteFolderEntry Read(string path) => Entries[path];
        public IEnumerable<SiteFolderEntry> List(string path) => Entries.Values
            .Where(x => x.Path.StartsWith(path + "/") && !x.Path[(path.Length + 1)..].Contains('/')).ToArray();
        public void CreateDirectory(string path) => Entries.Add(path, new(path, true, false, false, 1000, 1000, 488));
        public void SetAccess(string path, int owner, int group, int mode)
        {
            Entries[path] = Entries[path] with { Owner = owner, Group = group, Mode = mode }; Writes++;
            if (FailAfterWrite == path) { FailAfterWrite = null; throw new IOException("Ambiguous SFTP write failure"); }
        }
        public void DeleteDirectory(string path)
        {
            if (Entries.Keys.Any(x => x.StartsWith(path + "/"))) throw new IOException("Not empty");
            Entries.Remove(path);
        }
    }
}
