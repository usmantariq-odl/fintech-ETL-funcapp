namespace CSVReader;

internal interface IConFileStore
{
    bool Exists(string path);
    void EnsureDirectory(string path);
    void Upload(string localFile, string remotePath);
    void Rename(string source, string destination);
    void Delete(string path);
}

internal static class ConFileDelivery
{
    public static IReadOnlyList<string> Folders { get; } = Array.AsReadOnly(new[]
        { "ConfigurationUpdate", "Discarded", "Exception", "Processed", "Unprocessed" });

    public static void PrepareFolders(IConFileStore store)
    {
        foreach (var folder in Folders) store.EnsureDirectory("/" + folder);
    }

    public static string Deliver(IConFileStore store, string localFile, string directory)
    {
        var name = Path.GetFileName(localFile);
        if (!ArchiveFiles.IsCon(name)) throw new InvalidDataException("Only CON CSV files may be delivered to the CON container.");
        store.EnsureDirectory(directory);
        var destination = directory.TrimEnd('/') + "/" + name;
        if (store.Exists(destination))
        {
            var duplicates = directory.TrimEnd('/') + "/duplicate";
            store.EnsureDirectory(duplicates);
            destination = duplicates + "/" + name;
            for (var i = 1; store.Exists(destination); i++)
                destination = duplicates + "/" + ArchiveFiles.DuplicateName(name, i);
        }
        // Consumers see only fully uploaded final names.
        var temporary = directory.TrimEnd('/') + "/." + name + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            store.Upload(localFile, temporary);
            store.Rename(temporary, destination);
            return destination;
        }
        finally
        {
            try { if (store.Exists(temporary)) store.Delete(temporary); }
            catch { /* Input archive remains retryable if delivery failed. */ }
        }
    }
}
