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
    public void Run([TimerTrigger("0 * * * * *", RunOnStartup = false)] TimerInfo timer, CancellationToken cancellationToken = default, FunctionContext context = null)
    {
        using var alerts = new EtlErrorEmails(logger, context?.InvocationId ?? Guid.NewGuid().ToString("N"));
        try { RunCore(timer, cancellationToken, alerts); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastRunFailed = Math.Max(LastRunFailed, 1);
            logger.LogError(ex, "ETL timer invocation failed before scan completion");
            if (EtlFailurePolicy.ShouldEmail(ex)) alerts.Report("InvocationFailure", null, null, "Run aborted; inspect file checkpoints and outputs before replay. Unfinished inputs are retried by a later run.", ex.ToString());
            throw;
        }
    }

    private void RunCore(TimerInfo timer, CancellationToken cancellationToken, EtlErrorEmails alerts)
    {
        LastRunFailed = 0;
        var settings = EtlSettings.Load();
        if (!settings.DryRun)
        {
            SqlFileCheckpoints.EnsureCreated(settings.SqlConnectionString);
            SqlSchemaVerifier.Verify(settings.SqlConnectionString);
        }
        logger.LogInformation("Scanning Azure SFTP with {Workers} ETL workers; SQL database: {Database}; dry run: {DryRun}",
            settings.WorkerCount, settings.SqlDatabase, settings.DryRun);

        string[] sites;
        using (var discovery = CreateSftp(settings))
        {
            discovery.Connect();
            sites = (discovery.Exists(SftpLayout.UnprocessedRoot)
                ? discovery.ListDirectory(SftpLayout.UnprocessedRoot) : [])
                .Where(site => site.IsDirectory && SftpLayout.IsSiteId(site.Name))
                .Select(site => site.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (!string.IsNullOrEmpty(settings.MagLinkRoot) && discovery.Exists(settings.MagLinkRoot))
                sites = sites.Concat(discovery.ListDirectory(settings.MagLinkRoot)
                    .Where(s => s.IsDirectory && s.Name.All(c => c >= '0' && c <= '9') && int.TryParse(s.Name, out var id) && id > 0)
                    .Select(s => s.Name)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
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
                settings.DryRun ? null : new FtpConnectivity(LegacyLog), () => CreateSftp(settings, settings.ConUsername));
            logger.LogInformation("ETL site worker {WorkerNumber} started", workerNumber + 1);
            if (localRun) Console.WriteLine($"ETL site worker {workerNumber + 1} started");

            while (watch.Elapsed < runBudget && (maxFiles <= 0 || counters.Found < maxFiles))
            {
                var index = Interlocked.Increment(ref nextSite);
                if (index >= sites.Length) break;
                cancellationToken.ThrowIfCancellationRequested();
                ProcessSite(worker, sites[index], settings, counters, maxFiles, localRun, watch, runBudget, cancellationToken, alerts);
            }
        })).ToArray();
        Task.WaitAll(workers);

        if (watch.Elapsed >= runBudget)
            logger.LogWarning("Run budget reached; remaining files will be picked up by the next timer run");
        logger.LogInformation("Scan complete: {Found} files found, {Processed} processed, {Regenerated} regenerated, {NotConfigured} not configured, {Invalid} invalid, {Failed} failed or left for retry",
            counters.Found, counters.Processed, counters.Regenerated, counters.NotConfigured, counters.Invalid, counters.Failed);
        LastRunFailed = counters.Failed;
        if (localRun)
            Console.WriteLine($"{(settings.DryRun ? "Dry run" : "Live run")}: {counters.Found} files found, {counters.Processed} processed, {counters.NotConfigured} not configured, {counters.Invalid} invalid, {counters.Failed} failed or left for retry.");
    }

    private void ProcessSite(WorkerState worker, string site, EtlSettings settings, RunCounters counters,
        int maxFiles, bool localRun, Stopwatch watch, TimeSpan runBudget, CancellationToken cancellationToken, EtlErrorEmails alerts)
    {
        var input = SftpLayout.Input(site);
        var sftp = worker.Sftp;
        try
        {
            EnsureConnected(sftp);
            using var lease = settings.DryRun ? null : SiteLease.TryAcquire(settings.SqlConnectionString, site);
            if (!settings.DryRun && lease == null)
            {
                logger.LogInformation("Site {Site} is already being processed; skipping this cycle", site);
                return;
            }
            if (!settings.DryRun && sftp.Exists(SftpLayout.SiteRoot(site)))
            {
                EnsureDirectory(sftp, input);
                EnsureDirectory(sftp, SftpLayout.Output(site));
            }
            var candidates = new List<(Renci.SshNet.Sftp.ISftpFile File, bool MagLink)>();
            if (sftp.Exists(input)) candidates.AddRange(sftp.ListDirectory(input)
                .Where(item => item.IsRegularFile && ArchiveFiles.IsSupported(item.Name)).Select(item => (item, false)));
            var magLinkInput = $"{settings.MagLinkRoot}/{site}";
            if (!string.IsNullOrEmpty(settings.MagLinkRoot) && sftp.Exists(magLinkInput))
                candidates.AddRange(sftp.ListDirectory(magLinkInput)
                    .Where(item => item.IsRegularFile && ArchiveFiles.IsMagLink(item.Name)).Select(item => (item, true)));
            var files = candidates
                .Where(candidate => !candidate.File.Name.Contains(".part-", StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - candidate.File.LastWriteTimeUtc >= TimeSpan.FromSeconds(settings.MinimumFileAgeSeconds))
                .OrderBy(candidate => candidate.File.Name, StringComparer.Ordinal)
                .ToList();
            foreach (var candidate in files)
            {
                var item = candidate.File;
                cancellationToken.ThrowIfCancellationRequested();
                lease?.Verify();
                if (watch.Elapsed >= runBudget || !counters.TryTakeFile(maxFiles)) break;
                if (!candidate.MagLink && !ArchiveFiles.IsValidName(site, item.Name))
                {
                    if (settings.DryRun)
                        logger.LogInformation("Dry run: would move invalid archive {Path} to /invalidFile/{Site}/", item.FullName, site);
                    else
                    {
                        try
                        {
                            var invalid = MoveToArchive(sftp, item.FullName, "invalidFile", site, item.Name);
                            counters.IncrementInvalid();
                            logger.LogWarning("Invalid archive name {Input}; moved to {Destination}", item.FullName, invalid);
                            alerts.Report("InvalidFilename", site, item.FullName, "Moved to " + invalid,
                                "Archive name must match the exact input site ID followed by a valid timestamp and a supported archive extension.");
                        }
                        catch (Exception ex)
                        {
                            counters.IncrementFailed();
                            logger.LogError(ex, "Could not archive invalid archive {Path}; input remains for retry", item.FullName);
                            if (EtlFailurePolicy.ShouldEmail(ex)) alerts.Report("ArchiveMoveFailure", site, item.FullName, "Input remains for retry", ex.ToString());
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
                    if (item.Length > ArchiveFiles.MaxTotalBytes)
                    {
                        MoveToArchive(sftp, item.FullName, candidate.MagLink ? "MagLinkException" : "ExceptionFile", site, item.Name);
                        counters.IncrementFailed();
                        logger.LogError("Input {Path} exceeds the file size limit; archived for investigation", item.FullName);
                        alerts.Report("OversizedInput", site, item.FullName, "Moved to exception folder", $"Input bytes: {item.Length}; limit bytes: {ArchiveFiles.MaxTotalBytes}.");
                        continue;
                    }
                    var localArchive = Path.Combine(workDir, item.Name);
                    using (var local = File.Create(localArchive)) sftp.DownloadFile(item.FullName, local);
                    var afterDownload = sftp.GetAttributes(item.FullName);
                    if (afterDownload.Size != item.Length || afterDownload.LastWriteTimeUtc != item.LastWriteTimeUtc
                        || new FileInfo(localArchive).Length != item.Length)
                        throw new RetryableEtlException("Input changed during download.");
                    EtlOutcome outcome;
                    try
                    {
                        outcome = candidate.MagLink ? worker.Processor!.ProcessMagLink(localArchive, item.FullName, site)
                            : worker.Processor!.ProcessArchive(localArchive, item.FullName, site, file =>
                            {
                                if (settings.ConDestinations.Length == 0)
                                    throw new RetryableEtlException("CON file found but ConSftpDestinations is not configured; input retained.");
                                try
                                {
                                    worker.DeliverCon(file, settings.ConDestinations);
                                }
                                catch (Exception ex) { throw new RetryableEtlException("CON delivery failed; input retained.", ex); }
                            });
                    }
                    catch (Exception ex) when (!FtpConnectivity.IsRetryable(ex) && ex is not SshException)
                    {
                        var exceptionPath = MoveToArchive(sftp, item.FullName, candidate.MagLink ? "MagLinkException" : "ExceptionFile", site, item.Name);
                        counters.IncrementFailed();
                        logger.LogError(ex, "ETL processing failed for {Input}; moved to {Destination}", item.FullName, exceptionPath);
                        alerts.Report("FileProcessingFailure", site, item.FullName, "Moved to " + exceptionPath, ex.ToString());
                        if (localRun) Console.Error.WriteLine($"Failed {item.FullName}: {ex}");
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    lease?.Verify();
                    // Folder existence is the notification marker, including empty site folders.
                    // SQL work can outlast the SFTP session; verify the subsystem
                    // before any existence check, copy, move or input deletion.
                    EnsureConnected(sftp);
                    // Check before publication creates the destination.
                    var notifyNotConfigured = outcome.Result == EtlResult.NotConfigured
                        && ShouldAlertNotConfigured(outcome.Outputs.Select(output => output.Folder), folder =>
                        {
                            return sftp.Exists($"/{folder}/{site}");
                        });
                    ArchivePublisher.Publish(outcome, localArchive,
                        output => CopyToArchive(sftp, output.LocalPath, output.Folder, site, item.Name),
                        output => MoveToArchive(sftp, item.FullName, output.Folder, site, item.Name),
                        () => sftp.DeleteFile(item.FullName));
                    logger.LogInformation("{Result} {Input}; outputs: {Folders}", outcome.Result, item.FullName, string.Join(", ", outcome.Outputs.Select(o => o.Folder)));
                    if (localRun) Console.WriteLine($"{outcome.Result} {item.FullName}");
                    if (outcome.Result == EtlResult.NotConfigured)
                    {
                        counters.IncrementNotConfigured();
                        if (notifyNotConfigured)
                        {
                            logger.LogWarning("New notconfigured folder for site {Site}: {Input}; queuing individual diagnostic email", site, item.FullName);
                            alerts.Report("NotConfigured", site, item.FullName,
                                "Moved to " + string.Join(", ", outcome.Outputs.Select(o => $"/{o.Folder}/{site}/")),
                                "No active configured Omni site mapping was found for this FC site ID in the selected database. The destination site folder did not exist before this file was archived. Later arrivals are suppressed whenever that site folder already exists, including when it is empty. Verify the site activation and FC-to-Omni mapping before replay.");
                        }
                    }
                    else if (outcome.Result is EtlResult.Failed or EtlResult.Partial)
                    {
                        counters.IncrementFailed();
                        logger.LogError("Archive {Input} completed as {Result}; CSV errors: {Errors}", item.FullName, outcome.Result, string.Join("\n", outcome.Errors));
                        alerts.Report(outcome.Result.ToString(), site, item.FullName,
                            "Outputs published to " + string.Join(", ", outcome.Outputs.Select(o => o.Folder)) + "; input removed after publication. Inspect the exception subset before replay.",
                            string.Join("\n\n", outcome.Errors));
                    }
                    else
                    {
                        counters.IncrementProcessed();
                        if (outcome.Result == EtlResult.Regenerated) counters.IncrementRegenerated();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    counters.IncrementFailed();
                    logger.LogError(ex, "Could not finish {Path}; input remains for retry", item.FullName);
                    if (EtlFailurePolicy.ShouldEmail(ex)) alerts.Report("ProcessingRequiresIntervention", site, item.FullName, "Input remains for retry; database writes may already be committed. Replay uses content checkpoints and row duplicate checks.", ex.ToString());
                    if (localRun) Console.Error.WriteLine($"Failed {item.FullName}: {ex}");
                    if (SftpSessionRecovery.IsTransient(ex) || !sftp.IsConnected)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            counters.IncrementFailed();
            logger.LogError(ex, "Could not scan site {Site}", site);
            if (EtlFailurePolicy.ShouldEmail(ex)) alerts.Report("SiteScanFailure", site, input, "Site scan stopped; remaining inputs will be retried", ex.ToString());
            if (localRun) Console.Error.WriteLine($"Could not scan site {site}: {ex}");
            if (SftpSessionRecovery.IsTransient(ex) || !sftp.IsConnected) ResetConnection(sftp);
        }
    }

    internal static bool ShouldAlertNotConfigured(IEnumerable<string> folders, Func<string, bool> folderExists)
        => !folders.Any(folderExists);

    private static SftpClient CreateSftp(EtlSettings settings, string username = null)
    {
        var sftp = new SftpClient(settings.Host, settings.Port, username ?? settings.Username, settings.Password)
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
        SftpSessionRecovery.EnsureReady(() => sftp.IsConnected, sftp.Connect,
            () => { _ = sftp.Exists("/"); }, () => ResetConnection(sftp),
            (attempt, error) => LegacyLog.Warn($"Restoring SFTP session after temporary connection failure (attempt {attempt}/3): {error.GetType().Name}"));
    }

    private static void ResetConnection(SftpClient sftp)
    {
        try { sftp.Disconnect(); }
        catch { /* The next site will open a fresh session. */ }
    }

    private static void EnsureArchiveDirectory(SftpClient sftp, string folder, string site)
    {
        EnsureDirectory(sftp, $"/{folder}/{site}");
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

    private static void EnsureDirectory(SftpClient sftp, string directory)
    {
        var current = string.Empty;
        foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (!sftp.Exists(current))
            {
                try { sftp.CreateDirectory(current); }
                catch (SshException) when (sftp.Exists(current)) { /* Another worker created it. */ }
            }
        }
    }

    internal static void PrepareConFolders(EtlSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ConUsername)) throw new InvalidOperationException("ConSftpUsername is required to prepare the CON container.");
        using var sftp = CreateSftp(settings, settings.ConUsername);
        sftp.Connect();
        ConFileDelivery.PrepareFolders(new SftpConFileStore(sftp));
        Console.WriteLine("CON folder structure prepared: " + string.Join(", ", ConFileDelivery.Folders));
    }

    private sealed class SftpConFileStore(SftpClient sftp) : IConFileStore
    {
        public bool Exists(string path) => sftp.Exists(path);
        public void EnsureDirectory(string path) => EtlTimer.EnsureDirectory(sftp, path);
        public void Upload(string localFile, string remotePath)
        {
            using var source = File.OpenRead(localFile);
            sftp.UploadFile(source, remotePath, false);
        }
        public void Rename(string source, string destination) => RenameWithRetry(sftp, source, destination);
        public void Delete(string path) => sftp.DeleteFile(path);
    }

    private static string GetUniqueArchivePath(SftpClient sftp, string folder, string site, string name)
    {
        var siteFolder = $"/{folder}/{site}";
        var destination = $"{siteFolder}/{name}";
        if (sftp.Exists(destination))
        {
            var duplicateFolder = $"{siteFolder}/duplicate";
            if (!sftp.Exists(duplicateFolder)) sftp.CreateDirectory(duplicateFolder);
            destination = $"{duplicateFolder}/{name}";
            for (var index = 1; sftp.Exists(destination); index++)
                destination = $"{duplicateFolder}/{ArchiveFiles.DuplicateName(name, index)}";
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

    private sealed class WorkerState(SftpClient sftp, FtpConnectivity processor, Func<SftpClient> createConSftp) : IDisposable
    {
        private SftpClient conSftp;
        private bool conFoldersPrepared;
        public SftpClient Sftp { get; } = sftp;
        public FtpConnectivity Processor { get; } = processor;
        public void DeliverCon(string file, IEnumerable<string> destinations)
        {
            conSftp ??= createConSftp();
            EnsureConnected(conSftp);
            var store = new SftpConFileStore(conSftp);
            if (!conFoldersPrepared)
            {
                ConFileDelivery.PrepareFolders(store);
                conFoldersPrepared = true;
            }
            foreach (var destination in destinations) ConFileDelivery.Deliver(store, file, destination);
        }
        public void Dispose() { conSftp?.Dispose(); Sftp.Dispose(); }
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
