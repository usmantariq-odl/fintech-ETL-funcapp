using System.Globalization;

namespace CSVReader;

internal static class SftpLayout
{
    public const string UnprocessedRoot = "/Unprocessed";
    public const string ProcessedFolder = "Processed";
    public static IReadOnlyList<string> ArchiveFolders { get; } = Array.AsReadOnly(new[]
        { ProcessedFolder, "ExceptionFile", "invalidFile", "notconfigured", "RegeneratedFile" });

    public static bool IsSiteId(string site) => !string.IsNullOrEmpty(site)
        && site.All(c => c is >= '0' and <= '9')
        && int.TryParse(site, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;

    public static string SiteRoot(string site) => IsSiteId(site) ? $"{UnprocessedRoot}/{site}"
        : throw new ArgumentException("A positive numeric site ID is required.", nameof(site));
    public static string Input(string site) => SiteRoot(site) + "/input";
    public static string Output(string site) => SiteRoot(site) + "/output";

    // A moved site's historical SQL checkpoints may still use /<site>/input.
    public static string PreviousInputDirectory(string directory)
    {
        var normalized = directory.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "Unprocessed" && IsSiteId(parts[1]) && parts[2] == "input"
            ? normalized[UnprocessedRoot.Length..] : normalized;
    }
}
