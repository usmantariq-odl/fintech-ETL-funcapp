using System.Data.SqlClient;
using System.Globalization;
using CsvHelper;
using log4net;

namespace CSVReader;

internal sealed class RetryableEtlException(string message, Exception inner = null) : Exception(message, inner);
internal enum EtlResult { Processed, Regenerated, Partial, Failed, NotConfigured }
internal sealed record ArchiveOutput(string LocalPath, string Folder);
internal sealed record EtlOutcome(EtlResult Result, IReadOnlyList<ArchiveOutput> Outputs);

// Keeps archive/routing regression tests independent of SQL and SFTP.
internal interface IEtlBackend
{
    bool IsConfigured(string site);
    string Status(string key);
    int EnsureRecord(string local, string type, string key);
    void SetStatus(int id, string status, string error = null);
    void ValidateCsv(string file, string site);
    void ProcessCsv(string file, string site, int id);
    void Regenerate(string site, DateTime timestamp);
}

public sealed class FtpConnectivity
{
    private static readonly AsyncLocal<bool> Regenerated = new();
    private static readonly AsyncLocal<bool> ExceptionOccurred = new();
    public static bool isFileRegrenerated { get => Regenerated.Value; set => Regenerated.Value = value; }
    public static bool isExceptionOccured { get => ExceptionOccurred.Value; set => ExceptionOccurred.Value = value; }
    private readonly ILog log;
    private readonly IEtlBackend backend;

    public FtpConnectivity(ILog log) : this(log, new LegacyEtlBackend(log)) { }
    internal FtpConnectivity(ILog log, IEtlBackend backend) { this.log = log; this.backend = backend; }

    internal EtlOutcome ProcessArchive(string local, string remote, string site, Action<string> forwardCon)
    {
        isFileRegrenerated = isExceptionOccured = false;
        var configured = backend.IsConfigured(site);
        var root = Path.Combine(Path.GetDirectoryName(local)!, "extracted");
        var files = ArchiveFiles.Extract(local, root);
        // CON delivery also applies to inactive/unconfigured sites, as in the main ETL.
        if (!configured)
        {
            foreach (var file in files.Where(ArchiveFiles.IsCon)) forwardCon(file);
            return new(EtlResult.NotConfigured, [new(local, "notconfigured")]);
        }
        if (files.Length == 0) throw new InvalidDataException("Archive contains no CSV files.");
        var parentStatus = backend.Status(remote);
        if (parentStatus == "Processed")
        {
            var wasRegenerated = files.Any(IsRegeneratedCsv);
            return new(wasRegenerated ? EtlResult.Regenerated : EtlResult.Processed,
                wasRegenerated ? [new(local, "RegeneratedFile"), new(local, SftpLayout.ProcessedFolder)] : [new(local, SftpLayout.ProcessedFolder)]);
        }

        int parentId = backend.EnsureRecord(local, ArchiveFiles.Extension(local).TrimStart('.').ToUpperInvariant(), remote);
        try
        {
            // Validate the regeneration name before any database inserts.
            var regenerated = files.Any(IsRegeneratedCsv);
            var timestamp = regenerated ? ArchiveFiles.RegeneratedTimestamp(local, site) : default;
            var succeeded = new List<string>();
            var failed = new List<string>();
            foreach (var file in files)
            {
                isExceptionOccured = isFileRegrenerated = false;
                var key = remote + "!/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                var previous = backend.Status(key);
                if (previous == "Processed") { succeeded.Add(file); continue; }
                // A final parent may only be waiting for its SFTP archive operations.
                if ((parentStatus is "Partial" or "Failed") && previous == "Failed") { failed.Add(file); continue; }
                var id = backend.EnsureRecord(file, "CSV", key);
                try
                {
                    using (var transaction = EtlTransaction.Begin())
                    {
                        if (ArchiveFiles.IsCon(file)) forwardCon(file);
                        else
                        {
                            backend.ValidateCsv(file, site);
                            // The legacy reader removes the regenerated marker. Use a scratch copy
                            // so output archives retain the received content for audit.
                            var scratch = Path.Combine(Path.GetDirectoryName(local)!, "reading", Path.GetRelativePath(root, file));
                            Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
                            File.Copy(file, scratch, true);
                            backend.ProcessCsv(scratch, site, parentId);
                        }
                        if (isExceptionOccured) throw new InvalidDataException($"CSV processing failed for {Path.GetFileName(file)}.");
                        backend.SetStatus(id, "Processed");
                        transaction.Complete();
                    }
                    succeeded.Add(file);
                }
                catch (Exception ex) when (!IsRetryable(ex))
                {
                    backend.SetStatus(id, "Failed", ex.Message);
                    failed.Add(file);
                    log.Error($"CSV failed: {Path.GetFileName(file)}", ex);
                }
            }

            if (regenerated && failed.Count == 0)
            {
                var key = remote + "!/hourly-regeneration";
                if (backend.Status(key) != "Processed")
                {
                    var id = backend.EnsureRecord(local, "Regeneration", key);
                    using var transaction = EtlTransaction.Begin();
                    backend.Regenerate(site, timestamp);
                    backend.SetStatus(id, "Processed");
                    transaction.Complete();
                }
            }
            var result = failed.Count == 0 ? (regenerated ? EtlResult.Regenerated : EtlResult.Processed)
                : succeeded.Count > 0 ? EtlResult.Partial : EtlResult.Failed;
            var outputs = new List<ArchiveOutput>();
            if (result == EtlResult.Partial)
            {
                foreach (var group in new[] { (succeeded, SftpLayout.ProcessedFolder), (failed, "ExceptionFile") })
                {
                    var output = Path.Combine(Path.GetDirectoryName(local)!, group.Item2, Path.GetFileName(local));
                    ArchiveFiles.Create(output, group.Item1, root);
                    outputs.Add(new(output, group.Item2));
                }
            }
            else
            {
                if (result == EtlResult.Regenerated) outputs.Add(new(local, "RegeneratedFile"));
                outputs.Add(new(local, result == EtlResult.Failed ? "ExceptionFile" : SftpLayout.ProcessedFolder));
            }
            backend.SetStatus(parentId, result is EtlResult.Processed or EtlResult.Regenerated ? "Processed" : result.ToString(),
                failed.Count > 0 ? $"Processed CSVs: {succeeded.Count}; Failed CSVs: {failed.Count}." : null);
            return new(result, outputs);
        }
        catch (Exception ex)
        {
            backend.SetStatus(parentId, IsRetryable(ex) ? "Retry" : "Failed", ex.Message);
            throw;
        }
    }

    private static bool IsRegeneratedCsv(string file) => ArchiveFiles.CsvOrder(file) is >= 5 and <= 9 && ArchiveFiles.HasRegeneratedMarker(file);
    internal static bool IsRetryable(Exception ex) => ex is SqlException or RetryableEtlException or OperationCanceledException
        || ex is IOException or System.Transactions.TransactionException;

    internal EtlOutcome ProcessMagLink(string local, string remote, string site)
    {
        isExceptionOccured = isFileRegrenerated = false;
        if (!backend.IsConfigured(site)) return new(EtlResult.NotConfigured, [new(local, "MagLinkNotConfigured")]);
        if (backend.Status(remote) != "Processed")
        {
            var id = backend.EnsureRecord(local, "MagLink", remote);
            try
            {
                using var transaction = EtlTransaction.Begin();
                backend.ValidateCsv(local, site);
                backend.ProcessCsv(local, site, id);
                if (isExceptionOccured) throw new InvalidDataException("MagLink CSV processing failed.");
                backend.SetStatus(id, "Processed");
                transaction.Complete();
            }
            catch (Exception ex)
            {
                backend.SetStatus(id, IsRetryable(ex) ? "Retry" : "Failed", ex.Message);
                throw;
            }
        }
        return new(EtlResult.Processed, [new(local, "MagLinkProcessed")]);
    }
}

internal sealed class LegacyEtlBackend(ILog log) : IEtlBackend
{
    private OmniFintechMapping map;
    private DAL dal;
    private ReadTransactionTankData reader;
    private DateTime mappingLoaded;
    private void Initialize()
    {
        dal ??= new DAL(log);
        if (map == null || DateTime.UtcNow - mappingLoaded > TimeSpan.FromMinutes(15))
        {
            map = new OmniFintechMapping(log);
            mappingLoaded = DateTime.UtcNow;
        }
        reader ??= new ReadTransactionTankData(log);
    }
    public bool IsConfigured(string site) { Initialize(); return map.getOmniSiteID(int.Parse(site, CultureInfo.InvariantCulture)) > 0; }
    public string Status(string key) => dal.GetProcessedFileStatus(key);
    public int EnsureRecord(string local, string type, string key) => dal.EnsureProcessedFileRecord(local, type, key);
    public void SetStatus(int id, string status, string error = null) => dal.UpdateProcessedFileStatus(id, status, error);
    public void Regenerate(string site, DateTime timestamp)
    {
        FtpConnectivity.isExceptionOccured = false;
        dal.callHourlyProcedureForRegeneratedFile(map.getOmniSiteID(int.Parse(site, CultureInfo.InvariantCulture)), timestamp.AddHours(-1), timestamp.AddHours(1));
        if (FtpConnectivity.isExceptionOccured) throw new RetryableEtlException("Hourly regeneration procedure failed.");
    }

    public void ValidateCsv(string file, string site)
    {
        if (ArchiveFiles.CsvOrder(file) == 100 && !ArchiveFiles.IsMagLink(file)) return;
        // Parse before the DAL writes rows and reject payloads for another site.
        using var input = new StreamReader(file);
        if (ArchiveFiles.HasRegeneratedMarker(file)) input.ReadLine();
        using var csv = new CsvReader(input, CultureInfo.InvariantCulture);
        if (!csv.Read() || !csv.ReadHeader()) throw new InvalidDataException("CSV has no header.");
        var siteColumn = ArchiveFiles.IsMagLink(file) ? "ibank" : "site_id";
        int count = 0;
        while (csv.Read())
        {
            count++;
            for (var column = 0; column < csv.HeaderRecord.Length; column++) csv.GetField(column);
            if (csv.HeaderRecord.Contains(siteColumn) && int.Parse(csv.GetField(siteColumn), CultureInfo.InvariantCulture) != int.Parse(site, CultureInfo.InvariantCulture))
                throw new InvalidDataException($"CSV site does not match input folder {site}.");
            if (csv.HeaderRecord.Contains("file_exchange_id")
                && (!int.TryParse(csv.GetField("file_exchange_id").Split('_')[0], out var responseSite) || responseSite != int.Parse(site, CultureInfo.InvariantCulture)))
                throw new InvalidDataException("Price response site does not match its input folder.");
            if (ArchiveFiles.IsMagLink(file))
            {
                var asset = map.getOmniAssetID(int.Parse(site, CultureInfo.InvariantCulture), csv.GetField<int>("Tank"));
                if (asset <= 0 || map.getArticleName(map.getOmniSiteID(int.Parse(site, CultureInfo.InvariantCulture)), map.getTank_ProductID(asset)) == null)
                    throw new InvalidDataException("MagLink tank/product mapping is missing.");
            }
            if (ArchiveFiles.CsvOrder(file) == 5)
            {
                var product = csv.GetField<int>("grade_id");
                if (map.getArticleName(map.getOmniSiteID(int.Parse(site, CultureInfo.InvariantCulture)), product) == null)
                    throw new InvalidDataException($"Missing product mapping for FC site {site}, product {product}.");
            }
        }
        if (count == 0) throw new InvalidDataException("CSV has no data rows.");
    }
    public void ProcessCsv(string file, string site, int id)
    {
        if (ArchiveFiles.IsMagLink(file))
        {
            if (Path.GetFileName(file).Contains("stockLevels", StringComparison.OrdinalIgnoreCase)) reader.ReadMagLinkStockLevelData(file, map, id);
            else reader.ReadMagLinkDeliveryData(file, map, id);
            return;
        }
        var price = new PriceWritebackResponseProcessor(log, int.Parse(site, CultureInfo.InvariantCulture));
        switch (ArchiveFiles.CsvOrder(file))
        {
            case 0: price.ProcessResFpr(file); break;
            case 1: price.ProcessPca(file); break;
            case 2: price.ProcessPcd(file); break;
            case 3: price.ProcessGrp(file); break;
            case 4: price.ProcessPch(file); break;
            case 5: reader.ReadTransactionData(file, map, id); break;
            case 6: reader.ReadTanksData(file, map, id); break;
            case 7: reader.ReadDeliveryData(file, map, id); break;
            case 8: reader.ReadTransactionDetails(file, map, id); break;
            case 9: reader.ReadAlarmsDetails(file, map, id); break;
            default: log.Warn($"No processor configured for CSV {Path.GetFileName(file)}; preserving it in the processed archive."); break;
        }
    }
}
