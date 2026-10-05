using System.Data.SqlClient;
using System.Globalization;
using System.IO.Compression;
using CsvHelper;
using log4net;

namespace CSVReader;

// Reuses the existing mapping, CSV readers, and DAL; Azure SFTP I/O lives in EtlTimer.
public sealed class FtpConnectivity
{
    public enum ZipResult { Processed, Regenerated, NotConfigured }
    private static readonly AsyncLocal<bool> Regenerated = new();
    private static readonly AsyncLocal<bool> ExceptionOccurred = new();
    public static bool isFileRegrenerated { get => Regenerated.Value; set => Regenerated.Value = value; }
    public static bool isExceptionOccured { get => ExceptionOccurred.Value; set => ExceptionOccurred.Value = value; }
    private readonly ILog log;
    private OmniFintechMapping map;

    public FtpConnectivity(ILog log) => this.log = log;

    public ZipResult ProcessZip(string localZip, string remotePath, string siteId)
    {
        isFileRegrenerated = isExceptionOccured = false;
        var dal = new DAL(log);
        map ??= new OmniFintechMapping(log);
        if (map.getOmniSiteID(int.Parse(siteId, CultureInfo.InvariantCulture)) <= 0)
            return ZipResult.NotConfigured;

        var status = dal.GetProcessedFileStatus(remotePath);
        if (status == "Processed")
        {
            log.Info($"Database already contains processed record for {remotePath}; archive move can be retried.");
            return ContainsRegeneratedMarker(localZip) ? ZipResult.Regenerated : ZipResult.Processed;
        }

        int id = dal.EnsureProcessedFileRecord(localZip, "ZIP", remotePath);
        try
        {
            var extractDir = Path.Combine(Path.GetDirectoryName(localZip)!, "extracted");
            Directory.CreateDirectory(extractDir);
            using (var archive = ZipFile.OpenRead(localZip))
            {
                if (archive.Entries.Count > 1000)
                    throw new InvalidDataException("ZIP contains too many entries.");
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    total += entry.Length;
                    if (total > 250_000_000 || entry.Length > 100_000_000)
                        throw new InvalidDataException("ZIP exceeds extraction limit.");
                    if (!entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = Path.GetFullPath(Path.Combine(extractDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsafe ZIP entry path.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, true);
                }
            }

            // Reject unmapped transaction products before any CSV group is inserted.
            foreach (var psl in Directory.EnumerateFiles(extractDir, "*.csv", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file).Contains("PSL", StringComparison.OrdinalIgnoreCase)))
            {
                using var input = new StreamReader(psl);
                var firstLine = input.ReadLine();
                if (firstLine is null) throw new InvalidDataException($"Empty PSL CSV in {remotePath}.");
                if (!firstLine.Contains("regenerated", StringComparison.Ordinal))
                {
                    input.BaseStream.Position = 0;
                    input.DiscardBufferedData();
                }
                using var csv = new CsvReader(input, CultureInfo.InvariantCulture);
                foreach (var row in csv.GetRecords<dynamic>())
                {
                    var fields = (IDictionary<string, object>)row;
                    var product = int.Parse(fields["grade_id"].ToString()!, CultureInfo.InvariantCulture);
                    var omniSite = map.getOmniSiteID(int.Parse(siteId, CultureInfo.InvariantCulture));
                    if (map.getArticleName(omniSite, product) == null)
                        throw new InvalidOperationException($"Missing product mapping for FC site {siteId}, product {product}; ZIP remains in input.");
                }
            }

            var reader = new ReadTransactionTankData(log);
            int recognized = 0;
            foreach (var file in Directory.EnumerateFiles(extractDir, "*.csv", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.Contains("PSL", StringComparison.OrdinalIgnoreCase)) { reader.ReadTransactionData(file, map, id); recognized++; }
                else if (name.Contains("TIN", StringComparison.OrdinalIgnoreCase)) { reader.ReadTanksData(file, map, id); recognized++; }
                else if (name.Contains("TDD", StringComparison.OrdinalIgnoreCase)) { reader.ReadDeliveryData(file, map, id); recognized++; }
                else if (name.Contains("PPD", StringComparison.OrdinalIgnoreCase)) { reader.ReadTransactionDetails(file, map, id); recognized++; }
                else if (name.Contains("TAS", StringComparison.OrdinalIgnoreCase)) { reader.ReadAlarmsDetails(file, map, id); recognized++; }
                if (isExceptionOccured) throw new InvalidOperationException($"CSV processing failed for {name}.");
            }
            if (recognized == 0) throw new InvalidDataException("ZIP has no recognized ETL CSV files.");

            if (isFileRegrenerated)
            {
                var fileName = Path.GetFileName(localZip);
                if (!fileName.StartsWith(siteId, StringComparison.Ordinal)
                    || fileName.Length < siteId.Length + 14 + 4
                    || !DateTime.TryParseExact(fileName.Substring(siteId.Length, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                    throw new InvalidDataException("Regenerated ZIP does not have a valid timestamp in its name.");
                var omniId = map.getOmniSiteID(int.Parse(siteId, CultureInfo.InvariantCulture));
                dal.callHourlyProcedureForRegeneratedFile(omniId, timestamp.AddHours(-1), timestamp.AddHours(1));
                if (isExceptionOccured) throw new InvalidOperationException("Hourly regeneration procedure failed.");
            }
            dal.UpdateProcessedFileStatus(id, "Processed");
            return isFileRegrenerated ? ZipResult.Regenerated : ZipResult.Processed;
        }
        catch (Exception ex)
        {
            dal.UpdateProcessedFileStatus(id, "Failed", ex.Message);
            throw;
        }
    }

    private static bool ContainsRegeneratedMarker(string localZip)
    {
        using var archive = ZipFile.OpenRead(localZip);
        var buffer = new char[8192];
        foreach (var entry in archive.Entries.Where(entry => entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = new StreamReader(entry.Open());
            var tail = string.Empty;
            int length;
            while ((length = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = tail + new string(buffer, 0, length);
                if (chunk.Contains("regenerated", StringComparison.Ordinal)) return true;
                tail = chunk.Length > 10 ? chunk[^10..] : chunk;
            }
        }
        return false;
    }
}
