using System.Diagnostics;
using System.Data.SqlClient;
using System.Security.Cryptography;
using CSVReader;
using log4net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FintechEtl.FunctionApp;

public sealed class EtlTimer
{
    private static readonly ILog LegacyLog = LogManager.GetLogger(typeof(EtlTimer));
    private readonly ILogger<EtlTimer> logger;
    public int LastRunFailed { get; private set; }

    public EtlTimer(ILogger<EtlTimer> logger) => this.logger = logger;

    [Function("FintechEtlTimer")]
    public void Run([TimerTrigger("0 */3 * * * *", RunOnStartup = false)] TimerInfo timer)
    {
        var settings = EtlSettings.Load();
        logger.LogInformation("Scanning Azure SFTP with {Workers} ETL workers; SQL database: FintechStressTesting; dry run: {DryRun}",
            settings.WorkerCount, settings.DryRun);

        string[] sites;
        using (var discovery = CreateSftp(settings))
        {
            discovery.Connect();
            sites = discovery.ListDirectory("/")
                .Where(site => site.IsDirectory && site.Name.Length > 0
                    && site.Name.All(c => c >= '0' && c <= '9')
                    && int.TryParse(site.Name, out var siteId) && siteId > 0)
                .Select(site => site.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        var localRun = Environment.GetEnvironmentVariable("EtlLocalRun") == "true";
        var localSiteFilter = localRun ? Environment.GetEnvironmentVariable("EtlLocalSiteFilter") : null;
        var localSiteMin = localRun && int.TryParse(Environment.GetEnvironmentVariable("EtlLocalSiteMin"), out var parsedMin)
            ? parsedMin : 0;
        var localSiteMax = localRun && int.TryParse(Environment.GetEnvironmentVariable("EtlLocalSiteMax"), out var parsedMax)
            ? parsedMax : int.MaxValue;
        var maxFiles = localRun && int.TryParse(Environment.GetEnvironmentVariable("EtlLocalMaxFiles"), out var localMax)
            ? localMax : 0;
        sites = sites.Where(site => (string.IsNullOrWhiteSpace(localSiteFilter) || site == localSiteFilter)
                && int.Parse(site) >= localSiteMin && int.Parse(site) <= localSiteMax)
            .ToArray();

        var counters = new RunCounters();
        var watch = Stopwatch.StartNew();
        // Finish before the 24-hour host timeout so the next timer run can pick up the remainder.
        var runBudget = localRun ? TimeSpan.MaxValue : TimeSpan.FromHours(23.5);
        var nextSite = -1;
        var workerCount = Math.Min(settings.WorkerCount, sites.Length);
        var workers = Enumerable.Range(0, workerCount).Select(workerNumber => Task.Run(() =>
        {
            using var worker = new WorkerState(CreateSftp(settings),
                settings.DryRun ? null : new FtpConnectivity(LegacyLog));
            logger.LogInformation("ETL site worker {WorkerNumber} started", workerNumber + 1);
            if (localRun) Console.WriteLine($"ETL site worker {workerNumber + 1} started");

            while (watch.Elapsed < runBudget && (maxFiles <= 0 || counters.Found < maxFiles))
            {
                var index = Interlocked.Increment(ref nextSite);
                if (index >= sites.Length) break;
                ProcessSite(worker, sites[index], settings, counters, maxFiles, localRun, watch, runBudget);
            }
        })).ToArray();
        Task.WaitAll(workers);

        if (watch.Elapsed >= runBudget)
            logger.LogWarning("Run budget reached; remaining ZIPs will be picked up by the next timer run");
        logger.LogInformation("Scan complete: {Found} ZIPs found, {Processed} processed, {Regenerated} regenerated, {NotConfigured} not configured, {Invalid} invalid, {Failed} failed or left for retry",
            counters.Found, counters.Processed, counters.Regenerated, counters.NotConfigured, counters.Invalid, counters.Failed);
        LastRunFailed = counters.Failed;
        if (localRun)
            Console.WriteLine($"{(settings.DryRun ? "Dry run" : "Live run")}: {counters.Found} ZIPs found, {counters.Processed} processed, {counters.NotConfigured} not configured, {counters.Invalid} invalid, {counters.Failed} failed or left for retry.");
    }

    private void ProcessSite(WorkerState worker, string site, EtlSettings settings, RunCounters counters,
        int maxFiles, bool localRun, Stopwatch watch, TimeSpan runBudget)
    {
        var input = $"/{site}/input";
        var sftp = worker.Sftp;
        try
        {
            EnsureConnected(sftp);
            if (!sftp.Exists(input)) return;
            var zipFiles = sftp.ListDirectory(input)
                .Where(item => item.IsRegularFile && item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && !item.Name.Contains(".part-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            if (zipFiles.Count == 0) return;
            foreach (var item in zipFiles)
            {
                if (watch.Elapsed >= runBudget || !counters.TryTakeFile(maxFiles)) break;
                if (!IsValidArchiveName(site, item.Name))
                {
                    if (settings.DryRun)
                        logger.LogInformation("Dry run: would move invalid ZIP {Path} to /invalidFile/{Site}/", item.FullName, site);
                    else
                    {
                        try
                        {
                            var invalid = MoveToArchive(sftp, item.FullName, "invalidFile", site, item.Name);
                            counters.IncrementInvalid();
                            logger.LogWarning("Invalid ZIP name {Input}; moved to {Destination}", item.FullName, invalid);
                        }
                        catch (Exception ex)
                        {
                            counters.IncrementFailed();
                            logger.LogError(ex, "Could not archive invalid ZIP {Path}; input remains for retry", item.FullName);
                        }
                    }
                    continue;
                }
                if (settings.DryRun)
                {
                    logger.LogInformation("Dry run: would process {Path} and archive by ETL result", item.FullName);
                    continue;
                }

                var workDir = Path.Combine(Path.GetTempPath(), "FintechEtl", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workDir);
                try
                {
                    var localZip = Path.Combine(workDir, item.Name);
                    using (var local = File.Create(localZip)) sftp.DownloadFile(item.FullName, local);
                    FtpConnectivity.ZipResult result;
                    try { result = worker.Processor!.ProcessZip(localZip, item.FullName, site); }
                    catch (Exception ex) when (ex is not SqlException && ex is not SshConnectionException)
                    {
                        var exceptionPath = MoveToArchive(sftp, item.FullName, "ExceptionFile", site, item.Name);
                        counters.IncrementFailed();
                        logger.LogError(ex, "ETL processing failed for {Input}; moved to {Destination}", item.FullName, exceptionPath);
                        if (localRun) Console.Error.WriteLine($"Failed {item.FullName}: {ex}");
                        continue;
                    }

                    if (result == FtpConnectivity.ZipResult.Regenerated)
                    {
                        var regeneratedCopy = CopyToArchive(sftp, localZip, "RegeneratedFile", site, item.Name);
                        logger.LogInformation("Copied regenerated ZIP {Input} -> {Destination}", item.FullName, regeneratedCopy);
                    }
                    var folder = result == FtpConnectivity.ZipResult.NotConfigured ? "notconfigured" : "ProcessedFile";
                    var destination = MoveToArchive(sftp, item.FullName, folder, site, item.Name);
                    logger.LogInformation("{Result} {Input} -> {Destination}", result, item.FullName, destination);
                    if (localRun) Console.WriteLine($"{result} {item.FullName} -> {destination}");
                    if (result == FtpConnectivity.ZipResult.NotConfigured) counters.IncrementNotConfigured();
                    else
                    {
                        counters.IncrementProcessed();
                        if (result == FtpConnectivity.ZipResult.Regenerated) counters.IncrementRegenerated();
                    }
                }
                catch (Exception ex)
                {
                    counters.IncrementFailed();
                    logger.LogError(ex, "Could not finish {Path}; input remains for retry", item.FullName);
                    if (localRun) Console.Error.WriteLine($"Failed {item.FullName}: {ex}");
                    if (ex is SshConnectionException || !sftp.IsConnected)
                    {
                        ResetConnection(sftp);
                        break; // Retry this site's remaining ZIPs on the next timer run.
                    }
                }
                finally
                {
                    try { Directory.Delete(workDir, true); }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not remove temporary folder {Path}", workDir); }
                }
            }
        }
        catch (Exception ex)
        {
            counters.IncrementFailed();
            logger.LogError(ex, "Could not scan site {Site}", site);
            if (localRun) Console.Error.WriteLine($"Could not scan site {site}: {ex}");
            if (ex is SshConnectionException || !sftp.IsConnected) ResetConnection(sftp);
        }
    }

    private static SftpClient CreateSftp(EtlSettings settings)
    {
        var sftp = new SftpClient(settings.Host, settings.Port, settings.Username, settings.Password)
        {
            OperationTimeout = TimeSpan.FromSeconds(60),
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };
        sftp.HostKeyReceived += (_, e) =>
        {
            var actual = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            e.CanTrust = settings.HostKeySha256.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(expected => string.Equals(actual, expected.TrimEnd('='), StringComparison.Ordinal));
        };
        return sftp;
    }

    private static void EnsureConnected(SftpClient sftp)
    {
        if (!sftp.IsConnected) sftp.Connect();
    }

    private static void ResetConnection(SftpClient sftp)
    {
        try { sftp.Disconnect(); }
        catch { /* The next site will open a fresh session. */ }
    }

    // The legacy ETL used the first six filename digits as the site ID. Match the
    // whole folder ID here so numeric site IDs of other lengths remain supported.
    // A timestamp is required only for regenerated ZIPs by the ETL processor.
    private static bool IsValidArchiveName(string site, string name)
    {
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && name.Length > site.Length + 4
            && name.StartsWith(site, StringComparison.Ordinal);
    }

    private static void EnsureArchiveDirectory(SftpClient sftp, string folder, string site)
    {
        var root = $"/{folder}";
        if (!sftp.Exists(root)) sftp.CreateDirectory(root);
        var siteFolder = $"{root}/{site}";
        if (!sftp.Exists(siteFolder)) sftp.CreateDirectory(siteFolder);
    }

    private static string MoveToArchive(SftpClient sftp, string source, string folder, string site, string name)
    {
        EnsureArchiveDirectory(sftp, folder, site);
        var destination = GetUniqueArchivePath(sftp, folder, site, name);
        RenameWithRetry(sftp, source, destination);
        return destination;
    }

    private static string CopyToArchive(SftpClient sftp, string localZip, string folder, string site, string name)
    {
        EnsureArchiveDirectory(sftp, folder, site);
        var destination = GetUniqueArchivePath(sftp, folder, site, name);
        var temporary = $"/{folder}/{site}/.{name}.part-{Guid.NewGuid():N}";
        try
        {
            using (var source = File.OpenRead(localZip)) sftp.UploadFile(source, temporary);
            RenameWithRetry(sftp, temporary, destination);
            return destination;
        }
        finally
        {
            try { if (sftp.IsConnected && sftp.Exists(temporary)) sftp.DeleteFile(temporary); }
            catch { /* Leave the input ZIP for retry if the archive copy failed. */ }
        }
    }

    private static string GetUniqueArchivePath(SftpClient sftp, string folder, string site, string name)
    {
        var siteFolder = $"/{folder}/{site}";
        var destination = $"{siteFolder}/{name}";
        if (sftp.Exists(destination))
        {
            var duplicateFolder = $"{siteFolder}/duplicate";
            if (!sftp.Exists(duplicateFolder)) sftp.CreateDirectory(duplicateFolder);
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            destination = $"{duplicateFolder}/{name}";
            for (var index = 1; sftp.Exists(destination); index++)
                destination = $"{duplicateFolder}/{stem}_{index}{extension}";
        }
        return destination;
    }

    private static void RenameWithRetry(SftpClient sftp, string source, string destination)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                sftp.RenameFile(source, destination);
                return;
            }
            catch (Exception) when (attempt < 3)
            {
                // A timed-out rename can still have completed on the server.
                Thread.Sleep(TimeSpan.FromSeconds(attempt));
                EnsureConnected(sftp);
                if (!sftp.Exists(source) && sftp.Exists(destination)) return;
                if (sftp.Exists(destination)) throw;
            }
        }
    }

    private sealed class WorkerState(SftpClient sftp, FtpConnectivity processor) : IDisposable
    {
        public SftpClient Sftp { get; } = sftp;
        public FtpConnectivity Processor { get; } = processor;
        public void Dispose() => Sftp.Dispose();
    }

    private sealed class RunCounters
    {
        private int found;
        private int processed;
        private int regenerated;
        private int notConfigured;
        private int invalid;
        private int failed;
        public int Found => Volatile.Read(ref found);
        public int Processed => Volatile.Read(ref processed);
        public int Regenerated => Volatile.Read(ref regenerated);
        public int NotConfigured => Volatile.Read(ref notConfigured);
        public int Invalid => Volatile.Read(ref invalid);
        public int Failed => Volatile.Read(ref failed);
        public bool TryTakeFile(int maxFiles)
        {
            if (maxFiles <= 0) { Interlocked.Increment(ref found); return true; }
            while (true)
            {
                var current = Volatile.Read(ref found);
                if (current >= maxFiles) return false;
                if (Interlocked.CompareExchange(ref found, current + 1, current) == current) return true;
            }
        }
        public void IncrementProcessed() => Interlocked.Increment(ref processed);
        public void IncrementRegenerated() => Interlocked.Increment(ref regenerated);
        public void IncrementNotConfigured() => Interlocked.Increment(ref notConfigured);
        public void IncrementInvalid() => Interlocked.Increment(ref invalid);
        public void IncrementFailed() => Interlocked.Increment(ref failed);
    }
}
