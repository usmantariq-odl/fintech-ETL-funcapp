using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;

namespace CSVReader;

internal static class ArchiveFiles
{
    internal const int MaxEntries = 1000;
    internal const long MaxEntryBytes = 100_000_000;
    internal const long MaxTotalBytes = 250_000_000;

    public static string Extension(string name) => name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
        ? ".tar.gz" : Path.GetExtension(name).ToLowerInvariant();
    public static bool IsSupported(string name) => Extension(name) is ".zip" or ".tgz" or ".tar.gz";
    public static string DuplicateName(string name, int index)
    {
        var extension = Extension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        return $"{stem}_{index}{extension}";
    }
    public static bool IsValidName(string site, string name) => IsSupported(name)
        && name.Length > site.Length + Extension(name).Length
        && name.StartsWith(site, StringComparison.Ordinal);

    public static bool IsCon(string name) => Path.GetFileName(name).StartsWith("CON-", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
    public static bool IsMagLink(string name) => name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        && (name.Contains("stockLevels", StringComparison.OrdinalIgnoreCase) || name.Contains("deliveries", StringComparison.OrdinalIgnoreCase));

    public static int CsvOrder(string file)
    {
        var name = Path.GetFileName(file);
        string[] types = ["RESFPR", "PCA-", "PCD-", "GRP-", "PCH-", "PSL", "TIN", "TDD", "PPD", "TAS"];
        for (var i = 0; i < types.Length; i++)
            if (name.Contains(types[i], StringComparison.OrdinalIgnoreCase)) return i;
        return 100;
    }

    public static DateTime RegeneratedTimestamp(string archive, string site)
    {
        var name = Path.GetFileName(archive);
        if (!name.StartsWith(site, StringComparison.Ordinal) || name.Length < site.Length + 14 + Extension(name).Length
            || !DateTime.TryParseExact(name.Substring(site.Length, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
            throw new InvalidDataException("Regenerated archive does not have a valid yyyyMMddHHmmss timestamp immediately after its site ID.");
        return timestamp;
    }

    // Match the legacy marker semantics without mutating the source used for output archives.
    public static bool HasRegeneratedMarker(string file)
    {
        using var reader = new StreamReader(file);
        var buffer = new char[8192];
        var tail = string.Empty;
        int length;
        while ((length = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var chunk = tail + new string(buffer, 0, length);
            if (chunk.Contains("regenerated", StringComparison.Ordinal)) return true;
            tail = chunk.Length > 10 ? chunk[^10..] : chunk;
        }
        return false;
    }

    public static string[] Extract(string archive, string root)
    {
        Directory.CreateDirectory(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int count = 0;

        string Validate(string name, long size)
        {
            if (++count > MaxEntries || size < 0 || size > MaxEntryBytes || size > MaxTotalBytes - total)
                throw new InvalidDataException("Archive exceeds extraction limits.");
            total += size;
            var normalized = name.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
            if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(p => p is ".." or "."))
                throw new InvalidDataException("Unsafe archive entry path.");
            var path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(path))
                throw new InvalidDataException("Unsafe or duplicate archive entry path.");
            return path;
        }

        void ExtractEntry(string path, Stream input, long size)
        {
            if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.CreateNew);
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                copied += read;
                if (copied > size || copied > MaxEntryBytes) throw new InvalidDataException("Archive entry exceeds declared size.");
                output.Write(buffer, 0, read);
            }
            if (copied != size) throw new InvalidDataException("Truncated archive entry.");
        }

        if (Extension(archive) == ".zip")
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                var path = Validate(entry.FullName.TrimEnd('/'), entry.Length);
                // Unix symlinks and other special files must never be extracted.
                var kind = (entry.ExternalAttributes >> 16) & 0xF000;
                if (kind != 0 && kind != 0x8000 && kind != 0x4000)
                    throw new InvalidDataException("Archive contains a special file.");
                if (string.IsNullOrEmpty(entry.Name)) continue;
                using var input = entry.Open();
                ExtractEntry(path, input, entry.Length);
            }
        }
        else if (Extension(archive) is ".tgz" or ".tar.gz")
        {
            using var source = File.OpenRead(archive);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            TarEntry entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                if (entry.EntryType == TarEntryType.Directory && entry.Name.TrimEnd('/') is "." or "")
                {
                    if (++count > MaxEntries) throw new InvalidDataException("Archive exceeds extraction limits.");
                    continue;
                }
                var path = Validate(entry.Name.TrimEnd('/'), entry.Length);
                if (entry.EntryType == TarEntryType.Directory) continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("TAR contains a link or special file.");
                if (entry.DataStream != null) ExtractEntry(path, entry.DataStream, entry.Length);
                else if (entry.Length == 0 && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, []);
                }
            }
        }
        else throw new NotSupportedException("Unsupported archive format.");

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(CsvOrder).ThenBy(f => Path.GetRelativePath(root, f), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void Create(string output, IEnumerable<string> files, string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (Extension(output) == ".zip")
        {
            using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
            foreach (var file in files)
                zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
        }
        else
        {
            using var target = File.Create(output);
            using var gzip = new GZipStream(target, CompressionLevel.Optimal);
            using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
            foreach (var file in files) tar.WriteEntry(file, Path.GetRelativePath(root, file).Replace('\\', '/'));
        }
    }
}
