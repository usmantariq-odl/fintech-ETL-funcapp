using System.Data.SqlClient;
using System.Globalization;
using CsvHelper;
using log4net;

namespace CSVReader;

internal sealed class RetryableEtlException(string message, Exception inner = null) : Exception(message, inner);
internal enum EtlResult { Processed, Regenerated, Partial, Failed, NotConfigured }
internal sealed record ArchiveOutput(string LocalPath, string Folder);
internal sealed record EtlOutcome(EtlResult Result, IReadOnlyList<ArchiveOutput> Outputs)
{
    public IReadOnlyList<string> Errors { get; init; } = [];
}

// Keeps archive/routing regression tests independent of SQL and SFTP.
internal interface IEtlBackend
{
    bool IsConfigured(string site);
    string Status(string key);
    int EnsureRecord(string local, string type, string key);
    void SetStatus(int id, string status, string error = null);
    string CheckpointStatus(string key, string contentHash);
    void SetCheckpoint(string key, string contentHash, string status, string error = null);
    void ValidateCsv(string file, string site);
    void ProcessCsv(string file, string site, int? id);
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
        // Match the original ETL: an existing archive name/status must not suppress
        // new or repaired CSVs. Row-level duplicate checks still run in the DAL.
        int parentId = backend.EnsureRecord(local, ArchiveFiles.Extension(local).TrimStart('.').ToUpperInvariant(), remote);
        try
        {
            // Validate the regeneration name before any database inserts.
            var regenerated = files.Any(IsRegeneratedCsv);
            var timestamp = regenerated ? ArchiveFiles.RegeneratedTimestamp(local, site) : default;
            var contentHashes = files.ToDictionary(file => file, ArchiveFiles.ContentHash);
            var succeeded = new List<string>();
            var failed = new List<string>();
            var errors = new List<string>();
            foreach (var file in files)
            {
                isExceptionOccured = isFileRegrenerated = false;
                var key = remote + "!/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                var contentHash = contentHashes[file];
                var previous = backend.CheckpointStatus(key, contentHash);
                if (previous == "Processed") { succeeded.Add(file); continue; }
                // Failed CSVs get another attempt, as in the original ETL. Only an
                // identical, successfully committed CSV is safe to skip on retry.
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
                        backend.SetCheckpoint(key, contentHash, "Processed");
                        transaction.Complete();
                    }
                    succeeded.Add(file);
                }
                catch (Exception ex) when (!IsRetryable(ex))
                {
                    backend.SetCheckpoint(key, contentHash, "Failed", ex.Message);
                    failed.Add(file);
                    if (errors.Count < 20) errors.Add(Path.GetRelativePath(root, file) + ": " + FintechEtl.FunctionApp.EtlErrorEmails.Redact(ex.ToString()));
                    log.Error($"CSV failed: {Path.GetFileName(file)}", ex);
                }
            }

            if (regenerated && failed.Count == 0)
            {
                var key = remote + "!/hourly-regeneration";
                var contentHash = ArchiveFiles.ContentManifestHash(root, contentHashes);
                if (backend.CheckpointStatus(key, contentHash) != "Processed")
                {
                    using var transaction = EtlTransaction.Begin();
                    backend.Regenerate(site, timestamp);
                    backend.SetCheckpoint(key, contentHash, "Processed");
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
            return new(result, outputs) { Errors = errors };
        }
        catch (Exception ex)
        {
            backend.SetStatus(parentId, IsRetryable(ex) ? "Retry" : "Failed", ex.Message);
            throw;
        }
    }

    private static bool IsRegeneratedCsv(string file) => ArchiveFiles.CsvOrder(file) is >= 5 and <= 9 && ArchiveFiles.HasRegeneratedMarker(file);
    internal static bool IsRetryable(Exception ex) => ex is SqlException sql ? FintechEtl.FunctionApp.EtlFailurePolicy.IsRetryableSql(sql) : ex is RetryableEtlException or OperationCanceledException
        || ex is IOException or System.Transactions.TransactionException;

    internal EtlOutcome ProcessMagLink(string local, string remote, string site)
    {
        isExceptionOccured = isFileRegrenerated = false;
        if (!backend.IsConfigured(site)) return new(EtlResult.NotConfigured, [new(local, "MagLinkNotConfigured")]);
        var contentHash = ArchiveFiles.ContentHash(local);
        if (backend.CheckpointStatus(remote, contentHash) != "Processed")
        {
            try
            {
                using var transaction = EtlTransaction.Begin();
                backend.ValidateCsv(local, site);
                backend.ProcessCsv(local, site, null);
                if (isExceptionOccured) throw new InvalidDataException("MagLink CSV processing failed.");
                backend.SetCheckpoint(remote, contentHash, "Processed");
                transaction.Complete();
            }
            catch (Exception ex)
            {
                backend.SetCheckpoint(remote, contentHash, IsRetryable(ex) ? "Retry" : "Failed", ex.Message);
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
    // Old path-only checkpoints cannot prove that the current payload was committed.
    // Revalidate those deliveries once; the unchanged DAL excludes existing sale IDs.
    public string CheckpointStatus(string key, string contentHash) => new SqlFileCheckpoints(DAL.connectToDb).Status(key, contentHash);
    public void SetCheckpoint(string key, string contentHash, string status, string error = null) => new SqlFileCheckpoints(DAL.connectToDb).SetStatus(key, status, error, contentHash);
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
    public void ProcessCsv(string file, string site, int? id)
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
