using FintechAutoSiteConfiguration.FunctionApp.Storage;
using Microsoft.Extensions.Options;
using System.Threading.Channels;
using System.Xml.Linq;

internal class ParserService
{
    private readonly IOptions<ApplicationSettings> _settings;
    private readonly SQLService _sqlService;
    private readonly IConfigurationFiles _files;
    private readonly FileHelperService _fileHelper;
    private readonly Action<string, string, string> _notify;

    public ParserService(SQLService sqlService, IOptions<ApplicationSettings> settings, IConfigurationFiles files, FileHelperService fileHelper, Action<string, string, string>? notify = null)
    {
        _sqlService = sqlService;
        _settings = settings;
        _files = files;
        _fileHelper = fileHelper;
        _notify = notify ?? EmailService.SendEmail;
    }

    public async Task ProcessFilesAsync(Channel<string> channel,
        Action<string, string?, string?, string, string>? reportError = null)
    {
        string? activeFile = null, activeSite = null;
        void Error(string category, string? site, string? file, string disposition, string details)
        {
            if (reportError is not null) reportError(category, site, file, disposition, details);
            else _notify($"AutoSite {category}: site {site ?? "N/A"}", $"Input: {file}\n{disposition}\n{details}", _settings.Value.EmailRecipients);
        }
        List<SiteMetaDataResponse> siteMetaData = new();
        SiteMetaDataResponse? siteObj = null;
        bool restorePreservedActivation = false;
        async Task<string> RestoreOnError()
        {
            if (!restorePreservedActivation || siteObj is null) return "No preserved reconfiguration was eligible for activation restoration.";
            try
            {
                if (!await _sqlService.RestorePreservedSiteActivation(siteObj.FintechCustomerId ?? 0, siteObj.FintechSiteId ?? 0))
                    throw new InvalidOperationException("Preserved activation row or configuration is no longer available.");
                siteObj.IsActive = true;
                return "Preserved site configuration reactivated: IsActive=1. Reconfiguration remains Error.";
            }
            catch (Exception error)
            {
                var detail = "ACTIVATION RESTORATION FAILED; operator intervention required. " + error;
                siteObj.Exception += "\n" + detail;
                Console.Error.WriteLine(FintechAutoSiteConfiguration.FunctionApp.AutoSiteErrorEmails.Redact(detail));
                return detail;
            }
        }
        try
        {
            List<int> processedList;
            // One scan per invocation; the Azure Functions timer owns the five-minute schedule.
            {
                processedList = new();
                Console.WriteLine($"Trigger running at: {DateTime.Now:MMM dd, yyyy hh:mm:ss tt}");
                Console.WriteLine("Fetching sites meta data");
                siteMetaData = await _sqlService.GetSitesMetaData();

                // Requeue the newest configuration update after the portal approves reconfiguration.
                foreach (var site in siteMetaData.Where(site =>
                    site.IsConfigurationAllowed == true
                    && string.Equals(site.Status, "In Progress", StringComparison.OrdinalIgnoreCase)
                    && site.FCSiteId.HasValue))
                {
                    string configurationUpdateFolder = Path.Combine(
                        _settings.Value.ConfigurationUpdateFolder,
                        site.FCSiteId.GetValueOrDefault().ToString());
                    string? file = _files.DirectoryExists(configurationUpdateFolder)
                        ? _files.GetFiles(configurationUpdateFolder, "*.csv")
                            .OrderByDescending(Path.GetFileName)
                            .FirstOrDefault()
                        : null;
                    if (file is not null)
                        await channel.Writer.WriteAsync(file);
                    else
                        Console.WriteLine($"No pending configuration update for FC_SiteId {site.FCSiteId}; approval remains pending for a future input.");
                }

                foreach (var file in _files.GetFiles(_settings.Value.UnprocessedFolder, "*.csv").OrderByDescending(Path.GetFileName))
                {
                    await channel.Writer.WriteAsync(file);
                }
                Console.WriteLine($"Total files to process: {channel.Reader.Count}");

                if (channel.Reader.Count > 0)
                {
                    int FC_SiteId = 0, Fintech_SiteId = 0, Fintech_CustomerId = 0; string[] fileNameArr;

                    while (channel.Reader.TryRead(out var filePath))
                    {
                        Console.WriteLine($"Processing: {filePath}");
                        // A malformed next file must never reuse the preceding site's state.
                        siteObj = null;
                        FC_SiteId = 0;
                        activeFile = _files.DescribeSource(filePath);
                        activeSite = null;
                        restorePreservedActivation = false;
                        try
                        {
                            if (string.IsNullOrWhiteSpace(filePath))
                            {
                                Console.WriteLine("Empty file path captured.");
                                continue;
                            }
                            if (!Path.Exists(filePath))
                            {
                                Console.WriteLine("Invalid file path captured.");
                                continue;
                            }

                            fileNameArr = Path.GetFileName(filePath).Split('-');
                            if (fileNameArr.Length < 2 || fileNameArr[1].Length < 6 ||
                                !fileNameArr[1][..6].All(char.IsAsciiDigit) ||
                                !int.TryParse(fileNameArr[1][..6], out FC_SiteId) || FC_SiteId <= 0)
                            {
                                _fileHelper.MoveFileToException(filePath,
                                    "Invalid_FC_SITE_ID",
                                    _settings,
                                    $"Invalid FC_SiteId found or unable to extract FC_SiteId from first six digits from file: {Path.GetFileName(filePath)}");
                                Error("InvalidFilename", null, activeFile, "Moved to " + _settings.Value.ExceptionFolder + "/Invalid_FC_SITE_ID/",
                                    "Unable to extract a positive FC site ID from the first six digits after CON- in the filename.");
                                Console.WriteLine($"Invalid FC_SiteId captured. {Path.GetFileName(filePath)}");
                                continue;
                            }

                            activeSite = fileNameArr[1][..6];
                            if (processedList.Contains(FC_SiteId))
                            {
                                Console.WriteLine($"Discarding file for FC_SiteId: {FC_SiteId} as it's already been processed.");
                                _fileHelper.MoveFileToDiscarded(filePath, FC_SiteId.ToString(), _settings);
                                continue;
                            }

                            Console.WriteLine($"Checking Site's Data for FC_SiteId: {FC_SiteId}");
                            siteObj = siteMetaData.Find(x => x.FCSiteId == FC_SiteId);
                            if (siteObj is null)
                            {
                                Console.WriteLine($"FC_SiteId {FC_SiteId} has no eligible site mapping in this Function's SQL database; input retained. Check the target database and configuration/activation permissions.");
                                Error("MissingSiteMapping", activeSite, activeFile, "Input retained for a later scan; not moved to a notconfigured folder",
                                    "No eligible site metadata was returned for this FC site ID in the configured AutoSite database. Check the target database, FC-to-Omni site mapping and configuration/activation eligibility.");
                                continue;
                            }

                            bool approvedReconfiguration = siteObj.IsConfigurationAllowed == true &&
                                string.Equals(siteObj.Status, "In Progress", StringComparison.OrdinalIgnoreCase);
                            if (siteObj.IsActive == true && !approvedReconfiguration)
                            {
                                siteObj.Status = "New Configuration Detected";
                                Console.WriteLine($"## Site changed configuration detected with FC_SiteId: {FC_SiteId} | Fintech_SiteId: {siteObj.FintechSiteId}.");
                                _fileHelper.MoveFileToConfigurationUpdate(filePath, FC_SiteId.ToString(), _settings);
                                _notify($"{siteObj.FintechSiteName}'s Updated Configuration Detected", $"NEW CON FILE RECEIVED for the {siteObj.FintechSiteName}({siteObj.FCSiteId}) i.e. is ALREADY CONFIGURED.", _settings.Value.EmailRecipients);
                                continue;
                            }

                            Console.WriteLine($"_data: configuration_allowed = {siteObj.IsConfigurationAllowed} | activation_allowed = {siteObj.IsActivationAllowed}");
                            if (siteObj.IsConfigurationAllowed != true) continue;

                            // The portal may already have set IsActive=0. Remember whether a complete old
                            // configuration exists before parsing or replacing anything for this approval.
                            restorePreservedActivation = approvedReconfiguration && await _sqlService.HasPreservedSiteConfiguration(
                                siteObj.FintechCustomerId ?? 0, siteObj.FintechSiteId ?? 0);
                            Console.WriteLine($"STEP 1: Reading");
                            var document = HelperService.ReadFile(filePath);

                            Console.WriteLine($"STEP 2: Validating");
                            var siteData = await ValidateFile(document, filePath, FC_SiteId);
                            if (siteData is not null && siteData.Any())
                            {
                                Console.WriteLine($"STEP 3: Parsing & Saving mapping");
                                Fintech_SiteId = siteData.Select(s => s.SiteId).FirstOrDefault();
                                Fintech_CustomerId = siteData.Select(s => s.CustomerId).FirstOrDefault();

                                if (Fintech_SiteId > 0 && Fintech_CustomerId > 0)
                                {
                                    List<DocumentNodeResponse> tanks = HelperService.GetTagByName(document, ApplicationConstants.FILE_NODE_TANK),
                                        pumps = HelperService.GetTagByName(document, ApplicationConstants.FILE_NODE_PUMP),
                                        grades = HelperService.GetTagByName(document, ApplicationConstants.FILE_NODE_GRADE);

                                    var resp = await ParseData(new ParseDataRequest
                                    {
                                        document = document,
                                        Fintech_CustomerId = Fintech_CustomerId,
                                        Fintech_SiteId = Fintech_SiteId,
                                        Fintech_SiteName = siteObj.FintechSiteName ?? string.Empty,
                                        filePath = filePath,
                                        FC_SiteId = FC_SiteId,
                                        Nodes = new ParseDataNodeRequest
                                        {
                                            Tanks = tanks,
                                            Pumps = pumps,
                                            Grades = grades
                                        }
                                    }, siteData, siteObj.IsActivationAllowed);
                                    if (resp)
                                    {
                                        processedList.Add(FC_SiteId);
                                        siteObj.Status = siteObj.IsActive == true || siteObj.IsActivationAllowed == true ? "Activated" : "Configured";
                                        siteObj.Exception = null;
                                        if (siteObj.IsActivationAllowed == true) siteObj.IsActive = true;
                                        _fileHelper.MoveFileToProcessed(filePath, FC_SiteId.ToString(), _settings);
                                        _notify($"{siteObj.VendorName ?? "N/A"}/{siteObj.FintechSiteName}({siteObj.FCSiteId}) {siteObj.Status} Successfully!", $"The site {siteObj.FintechSiteName} has been {siteObj.Status} successfully by {siteObj.UserEmail}.", $"{_settings.Value.EmailRecipients},{siteObj.UserEmail}");
                                    }
                                    else
                                    {
                                        siteObj.Status = "Error";
                                        siteObj.Exception = $"Error processing file: {filePath}";
                                        var activationResult = await RestoreOnError();
                                        _fileHelper.MoveFileToException(filePath, FC_SiteId.ToString(), _settings, $"Error processing file: {filePath}\n{activationResult}");
                                        Error("ConfigurationFailed", activeSite, activeFile, "Moved to " + _settings.Value.ExceptionFolder + "/" + FC_SiteId + "/", "Configuration replacement reported failure. " + activationResult);
                                    }
                                }
                                else
                                    throw new Exception($"Invalid Site & Product mapping found for FC_SiteId: {FC_SiteId}");
                            }
                            else
                                throw new Exception($"Product mapping miss-matched for FC_SiteId: {FC_SiteId} i.e, customer and file product Ids are not alligned.");

                            Console.WriteLine($"Processed: {Path.GetFileName(filePath)}");
                        }
                        catch (Exception ex) when (ex is not ConfigurationStorageException)
                        {
                            Console.WriteLine(ex.Message);
                            if (siteObj is not null)
                            {
                                siteObj.Status = "Error";
                                siteObj.Exception = ex.Message;
                                // Validation never mutates SQL; failed replacement rolls back.
                                // Restore preserved activation below after validation failure or transaction rollback.

                            }
                            var activationResult = await RestoreOnError();
                            try { _fileHelper.MoveFileToException(filePath, FC_SiteId.ToString(), _settings, ex); }
                            catch (Exception storageError)
                            {
                                throw new ConfigurationStorageException("Configuration failed and exception-file publication also failed. Verify actual file location and committed configuration.", new AggregateException(ex, storageError));
                            }
                            Error("ConfigurationRejectedOrFailed", activeSite, activeFile,
                                "Moved to " + _settings.Value.ExceptionFolder + "/" + FC_SiteId + "/; " + activationResult, ex + "\n\n" + activationResult);
                        }
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();

                activeFile = activeSite = null;
                Console.WriteLine($"Updating sites statuses");
                var statusesSaved = await _sqlService.UpdateSiteStatus(siteMetaData);
                if (siteMetaData.Count > 0 && !statusesSaved)
                    Error("StatusUpdateFailure", null, null, "Configuration or file operations may already have completed; verify portal statuses against committed SQL state.",
                        "UpdateSiteStatus returned false for a nonempty site list. No SQL exception was supplied; inspect affected rows and database mappings.");

                Console.WriteLine("Auto site configuration scan complete.");
            }
        }
        catch (Exception ex)
        {
            if (activeFile is not null) ex.Data["AutoSiteFile"] = activeFile;
            if (activeSite is not null) ex.Data["AutoSiteSite"] = activeSite;
            await FileHelperService.LogExceptionAsync(ex);
            throw;
        }
    }

    /// <summary>
    /// It will validate file by tanks and it's FC site Id
    /// </summary>
    /// <param name="document"></param>
    /// <returns>BOOLEAN: true if validation passes else false</returns>
    private async Task<List<SiteAndProductResponse>?> ValidateFile(XDocument document, string filePath, int FC_SiteId)
    {
        try
        {
            Console.WriteLine($"Validating file: {filePath}");

            // Validate site (by FC_site Id), customer and their products
            var resp = await _sqlService.GetOmniSiteIdAndProducts(FC_SiteId);
            if (resp.Any())
            {
                var validationResp = HelperService.ValidateFileByGrades(document, resp);
                if (validationResp == "true")
                    return resp;
                else
                    throw new Exception($@"Error while processing site FC_SiteId: {FC_SiteId}!
FileName: {Path.GetFileName(filePath)}
Description:
{validationResp}");
            }
            else throw new Exception($"Missing customer products while processing site FC_SiteId: {FC_SiteId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    private async Task<bool> ParseData(ParseDataRequest request, List<SiteAndProductResponse> products, bool? isActivationAllowed)
    {
        await new SiteConfigurationWriter(_sqlService).ReplaceAsync(request, products, isActivationAllowed);
        return true;
    }
}
