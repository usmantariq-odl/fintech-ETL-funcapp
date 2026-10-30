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

    public ParserService(SQLService sqlService, IOptions<ApplicationSettings> settings, IConfigurationFiles files, FileHelperService fileHelper)
    {
        _sqlService = sqlService;
        _settings = settings;
        _files = files;
        _fileHelper = fileHelper;
    }

    public async Task ProcessFilesAsync(Channel<string> channel)
    {
        List<SiteMetaDataResponse> siteMetaData = new();
        SiteMetaDataResponse siteObj = new();
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
                    site.IsActive != true
                    && site.IsConfigurationAllowed == true
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
                        site.Status = "File Not Landed";
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
                            if (fileNameArr.Count() > 1 && !int.TryParse(fileNameArr[1].Substring(0, 6), out FC_SiteId))
                            {
                                _fileHelper.MoveFileToException(filePath,
                                    "Invalid_FC_SITE_ID",
                                    _settings,
                                    $"Invalid FC_SiteId found or unable to extract FC_SiteId from first six digits from file: {Path.GetFileName(filePath)}");
                                Console.WriteLine($"Invalid FC_SiteId captured. {Path.GetFileName(filePath)}");
                                continue;
                            }

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
                                continue;
                            }

                            if (siteObj.IsActive == true)
                            {
                                siteObj.Status = "New Configuration Detected";
                                Console.WriteLine($"## Site changed configuration detected with FC_SiteId: {FC_SiteId} | Fintech_SiteId: {siteObj.FintechSiteId}.");
                                _fileHelper.MoveFileToConfigurationUpdate(filePath, FC_SiteId.ToString(), _settings);
                                EmailService.SendEmail($"{siteObj.FintechSiteName}'s Updated Configuration Detected", $"NEW CON FILE RECEIVED for the {siteObj.FintechSiteName}({siteObj.FCSiteId}) i.e. is ALREADY CONFIGURED.", _settings.Value.EmailRecipients);
                                continue;
                            }

                            Console.WriteLine($"_data: configuration_allowed = {siteObj.IsConfigurationAllowed} | activation_allowed = {siteObj.IsActivationAllowed}");
                            if (siteObj.IsConfigurationAllowed != true) continue;

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
                                    }, siteObj.IsActivationAllowed);
                                    if (resp)
                                    {
                                        processedList.Add(FC_SiteId);
                                        siteObj.Status = siteObj.IsConfigurationAllowed == true && siteObj.IsActivationAllowed == true ? "Activated" : "Configured";
                                        _fileHelper.MoveFileToProcessed(filePath, FC_SiteId.ToString(), _settings);
                                        EmailService.SendEmail($"{siteObj.VendorName ?? "N/A"}/{siteObj.FintechSiteName}({siteObj.FCSiteId}) {siteObj.Status} Successfully!", $"The site {siteObj.FintechSiteName} has been {siteObj.Status} successfully by {siteObj.UserEmail}.", $"{_settings.Value.EmailRecipients},{siteObj.UserEmail}");
                                    }
                                    else
                                    {
                                        siteObj.Status = "Error";
                                        siteObj.Exception = $"Error processing file: {filePath}";
                                        _fileHelper.MoveFileToException(filePath, FC_SiteId.ToString(), _settings, $"Error processing file: {filePath}");
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
                                await _sqlService.ClearSiteConfiguration(siteObj.FintechCustomerId ?? 0, siteObj.FintechSiteId ?? 0);
                                EmailService.SendEmail($"Error while processing {siteObj.FintechSiteName}!", ex.Message, _settings.Value.EmailRecipients);
                            }
                            _fileHelper.MoveFileToException(filePath, FC_SiteId.ToString(), _settings, ex);
                        }
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();

                Console.WriteLine($"Updating sites statuses");
                await _sqlService.UpdateSiteStatus(siteMetaData);

                Console.WriteLine("Auto site configuration scan complete.");
            }
        }
        catch (Exception ex)
        {
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

    private async Task<bool> ParseData(ParseDataRequest request, bool? isActivationAllowed)
    {
        try
        {
            Console.WriteLine($"Parsing file: {request.filePath}");
            List<CreateAssetRequest> assets = new();

            var assetsCount = await _sqlService.GetAssetsCountBySiteId(request.Fintech_SiteId);
            if (assetsCount > 0)
                await _sqlService.ClearSiteConfiguration(request.Fintech_CustomerId, request.Fintech_SiteId);

            bool resp = await HelperService.CreateSiteAssets(request, _sqlService);
            if (resp)
            {
                var assetEntities = await _sqlService.GetAssetsBySiteId(request.Fintech_SiteId);
                if (assetEntities.Any())
                {
                    var configResp = await _sqlService.SaveTankMapping(HelperService.GetTankMappingRequestModels(request, assetEntities), request.Fintech_SiteId) &&
                              await _sqlService.SavePumpMapping(HelperService.GetPumpMappingRequestModels(request, assetEntities), request.Fintech_SiteId);

                    if (configResp && isActivationAllowed == true) return await _sqlService.MarkSiteActivated(request.Fintech_CustomerId, request.Fintech_SiteId);
                    else return configResp;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }
}
