using Azure.Core;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public static class HelperService
{
    public static async Task WaitUntilFileIsReadyAsync(string filePath)
    {
        const int maximumAttempts = 10;

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);

                return;
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                await Task.Delay(1000);
            }
        }

        throw new IOException(
            $"The file is still locked: {filePath}");
    }

    public static XDocument ReadFile(string filePath)
    {
        Console.WriteLine($"Reading file: {filePath}");
        string xml = File.ReadAllText(filePath);

        if (!xml.TrimStart().StartsWith("<ROOT>"))
            xml = $"<ROOT>{Environment.NewLine}{xml}{Environment.NewLine}</ROOT>";

        XDocument doc = XDocument.Parse(xml);
        return doc;
    }

    public static string ValidateFileByGrades(XDocument document, List<SiteAndProductResponse> resp)
    {
        try
        {
            StringBuilder errorsSB = new();
            List<DocumentNodeResponse> grades = GetTagByName(document, ApplicationConstants.FILE_NODE_GRADE),
                pumps = GetTagByName(document, ApplicationConstants.FILE_NODE_PUMP);

            var errorResp = pumps
                .Where(_ => _.Hoses.Any(h => h.RealId is not null && h.RealId <= 0))
                .Select(s => s.Id);

            if (errorResp.Any())
                errorsSB.AppendLine($"Following pump(s) \"{string.Join(", ", errorResp)}\" having invalid nozzle id \"0\".");

            bool containsAll = grades.All(g => resp.Any(r =>
                Tokenize(g.Name).Intersect(
                    Tokenize(r.ProductName),
                    StringComparer.OrdinalIgnoreCase)
                .Any()));

            if (!containsAll || grades.Count > resp.Count)
            {
                errorsSB.AppendLine(new string('-', 40));
                errorsSB.AppendLine($@"Auto site configuration failed!
Products mismtached found between the configuration file and the customer product list.

Grades at the customer: {string.Join(", ", resp.Select(r => r.ProductName))}
Grades in the CON file: {string.Join(", ", grades.Select(g => g.Name))}");
            }

            if (errorsSB.Length > 0)
                return errorsSB.ToString();

            var mismatchedProducts = grades
                .Where(siteGrade => !resp.Any(customerGrade =>
                    siteGrade.Number == customerGrade.ProductId &&
                    Tokenize(siteGrade.Name).Intersect(Tokenize(customerGrade.ProductName), StringComparer.OrdinalIgnoreCase).Any()
                    )).ToList();
            if (!mismatchedProducts.Any())
                return "true";

            foreach (var product in mismatchedProducts)
            {
                errorsSB.AppendLine("Product ID mismatch between the customer product records and the configuration file.");
                errorsSB.AppendLine();
                errorsSB.AppendLine($"Product: {product.Name}");
                errorsSB.AppendLine($"Customer products: {string.Join(", ", resp.Select(s => $"{s.ProductName}: {s.ProductId}"))}");
                errorsSB.AppendLine($"Configuration File products: {string.Join(", ", grades.Select(g => $"{g.Name}: {g.Number}"))}");
            }

            errorsSB.AppendLine();
            errorsSB.AppendLine("Each product name must have one matching ID in the customer product records and configuration file. Correct the conflicting IDs or missing grade numbers, then reprocess the file.");

            return errorsSB.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public static List<DocumentNodeResponse> GetTagByName(XDocument document, string tagName)
    {
        try
        {
            var defName = $"{tagName}_def";
            var nodeDefinitions = document
                .Descendants(tagName)
                .Where(x => x.Elements(defName).Any())
                .Elements(defName)
                .Select(x => new DocumentNodeResponse
                {
                    Id = int.TryParse((string?)x.Attribute("ID"), out _) ? (int?)x.Attribute("ID") : 0,
                    Tank_Suction_Id = (string?)x.Attribute("ID"),
                    Number = (int?)x.Element("number"),
                    Name = (string?)x.Element("name"),
                    ProductName = (string?)x.Element("product"),
                    HoseQuantity = (int?)x.Element("hoses_qty"),
                    TankCapacity = (int?)x.Element("capacity"),
                    Tank_Suction_Mapped_Tank = (int?)x.Element("tank1"),
                    Hoses = x.Elements().Where(x => x.Name.LocalName.StartsWith("hose", StringComparison.OrdinalIgnoreCase))
                    .Select(hose => new DocumentNodeHoseResponse
                    {
                        HoseName = hose.Name.LocalName,
                        Grade = (string?)hose.Element("grade"),
                        RealId = int.TryParse((string?)hose.Element("real_id"), out _) ? (int?)hose.Element("real_id") : null,
                        MappedTankName = (string?)hose.Element("suction_high")
                    })
                    .ToList()
                })
                .ToList();

            return nodeDefinitions;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HelperService | L:103 | {ex.Message}");
            throw;
        }
    }

    public static async Task<bool> CreateSiteAssets(ParseDataRequest request, SQLService _sqlService)
    {
        try
        {
            string fullName = string.Empty, productName = string.Empty;
            List<CreateAssetRequest> assets = new();

            int areaId = await _sqlService.CreateArea(request.Fintech_SiteId, request.Fintech_SiteName);
            if (areaId > 0)
            {
                if (request.Nodes.Tanks.Any())
                {
                    foreach (var tank in request.Nodes.Tanks)
                    {
                        productName = getFinalProductName(tank.ProductName, request);
                        fullName = $"Tank_{tank.Id}({productName})";
                        assets.Add(new CreateAssetRequest
                        {
                            Asset_Name = fullName,
                            Type = "Asset",
                            Description = $"{fullName} of site {request.Fintech_SiteId}({request.FC_SiteId}) by {ApplicationConstants.JOB_NAME} Job",
                            Site_Id_FK = request.Fintech_SiteId,
                            AssetType = ApplicationConstants.ASSET_TYPE_TANK,
                            Created_By = ApplicationConstants.JOB_NAME
                        });
                    }
                }


                if (request.Nodes.Pumps.Any())
                {
                    foreach (var pump in request.Nodes.Pumps)
                    {
                        fullName = $"Pump_{pump.Id}";
                        assets.Add(new CreateAssetRequest
                        {
                            Asset_Name = fullName,
                            Type = "Asset",
                            Description = $"{fullName} of site {request.Fintech_SiteId}({request.FC_SiteId}) by {ApplicationConstants.JOB_NAME} Job",
                            Site_Id_FK = request.Fintech_SiteId,
                            AssetType = ApplicationConstants.ASSET_TYPE_PUMP,
                            Created_By = ApplicationConstants.JOB_NAME
                        });
                        foreach (var hose in pump.Hoses.Where(h => h.RealId is not null))
                        {
                            if (hose.RealId <= 0)
                                throw new Exception($@"Error while processing site FC_SiteId: {request.FC_SiteId}!
Description:
Nozzle ID '{hose.RealId}' against Pump_{pump.Id} is incorrect and does not meet the Auto Site Configuration criteria.");

                            fullName = $"Nozzle{hose.RealId}_Pump{pump.Id}";
                            assets.Add(new CreateAssetRequest
                            {
                                Asset_Name = fullName,
                                Type = "Asset",
                                Description = $"{fullName} of site {request.Fintech_SiteId}({request.FC_SiteId}) by {ApplicationConstants.JOB_NAME} Job",
                                Site_Id_FK = request.Fintech_SiteId,
                                AssetType = ApplicationConstants.ASSET_TYPE_NOZZLE,
                                Created_By = ApplicationConstants.JOB_NAME
                            });
                        }
                    }
                }

                var resp = await _sqlService.CreateAssets(assets);
                return resp && await _sqlService.InsertIntoMappingTable(areaId, request.Fintech_SiteId);
            }
            else
            {
                Console.WriteLine($"HelperService | L:164 | Area creation failed for site {request.Fintech_SiteId}({request.FC_SiteId})");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HelperService | {ex.Message}");
            throw;
        }
    }

    public static List<CreateTankMappingRequest> GetTankMappingRequestModels(ParseDataRequest request, List<AssetResponse> sqlAssets)
    {
        try
        {
            var resp = new List<CreateTankMappingRequest>();
            string fullName = string.Empty, productName = string.Empty;

            foreach (var tank in request.Nodes.Tanks)
            {
                productName = getFinalProductName(tank.ProductName, request);
                fullName = $"Tank_{tank.Id}({productName})";
                AssetResponse? obj = sqlAssets.Find(f => f.AssetName == fullName && f.AssetType == ApplicationConstants.ASSET_TYPE_TANK);

                var gradeObj = request.Nodes.Grades.Find(f => f.Name != null && f.Name.ToLower().Trim() == productName.ToLower().Trim());

                resp.Add(new CreateTankMappingRequest
                {
                    FC_TankID = tank.Id,
                    FC_SiteID = request.FC_SiteId,
                    Omni_AssetID = obj != null ? obj.AssetId : -1,
                    Omni_SiteID = request.Fintech_SiteId,
                    CreatedBy = ApplicationConstants.JOB_NAME,
                    ProdId = gradeObj != null ? gradeObj.Number : -1,
                    Capacity = tank.TankCapacity
                });
            }

            return resp;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    /// <summary>Builds pump mappings and reports all incomplete hose mappings in one error.</summary>
    public static List<CreatePumpMappingRequest> GetPumpMappingRequestModels(ParseDataRequest request, List<AssetResponse> sqlAssets)
    {
        try
        {
            var resp = new List<CreatePumpMappingRequest>();
            var mappingErrors = new StringBuilder();
            string pumpFullName = string.Empty, nozzleFullName = string.Empty, tankFullName = string.Empty, product = string.Empty;
            int tankId = 0;
            string[] arr;

            if (request.Nodes.Pumps.Count == 0)
            {
                AppendPumpMappingError(mappingErrors, null, null, "pump definition");
            }

            foreach (var pump in request.Nodes.Pumps)
            {
                if (!pump.Hoses.Any(h => h.RealId is not null))
                {
                    AppendPumpMappingError(mappingErrors, pump.Id, null, "hose with real_id");
                    continue;
                }

                foreach (var hose in pump.Hoses.Where(h => h.RealId is not null))
                {
                    if (string.IsNullOrWhiteSpace(hose.MappedTankName))
                    {
                        AppendPumpMappingError(
                            mappingErrors,
                            pump.Id,
                            hose.RealId?.ToString(),
                            "suction_high");
                        continue;
                    }

                    arr = hose.MappedTankName!.Split('-', 2, StringSplitOptions.TrimEntries);
                    if (arr.Length != 2 || !int.TryParse(arr[0], out tankId) || string.IsNullOrWhiteSpace(arr[1]))
                    {
                        AppendPumpMappingError(mappingErrors, pump.Id, hose.RealId?.ToString(), $"invalid suction_high format: {hose.MappedTankName}");
                        continue;
                    }

                    product = arr[1];
                    tankFullName = $"Tank_{tankId}({product})";
                    pumpFullName = $"Pump_{pump.Id}";
                    nozzleFullName = $"Nozzle{hose.RealId}_Pump{pump.Id}";

                    AssetResponse? pumpObj = sqlAssets.Find(f => f.AssetName == pumpFullName && f.AssetType == ApplicationConstants.ASSET_TYPE_PUMP),
                    nozzleObj = sqlAssets.Find(f => f.AssetName == nozzleFullName && f.AssetType == ApplicationConstants.ASSET_TYPE_NOZZLE),
                    tankObj = sqlAssets.Find(f => string.Equals(f.AssetName, tankFullName, StringComparison.OrdinalIgnoreCase) && f.AssetType == ApplicationConstants.ASSET_TYPE_TANK);
                    if (tankObj is null)
                    {
                        var availableTanks = sqlAssets
                            .Where(asset => asset.AssetType == ApplicationConstants.ASSET_TYPE_TANK)
                            .Select(asset => asset.AssetName)
                            .ToList();
                        string? expectedTank = availableTanks
                            .Find(assetName => assetName?.StartsWith($"Tank_{tankId}(", StringComparison.OrdinalIgnoreCase) == true);
                        string message = $@"Auto Configuration Failed

A tank mapping mismatch was detected.

Site: {request.Fintech_SiteName} ({request.FC_SiteId})
Configuration File: {Path.GetFileName(request.filePath)}
Pump: {pump.Id}
Nozzle: {hose.RealId}
Current Mapping: {tankFullName}
Expected Mapping: {expectedTank ?? "No matching configured tank asset"}

{tankFullName} is not available in the configured tank assets.

Available Tanks:
{string.Join(Environment.NewLine, availableTanks.Select(availableTank => $"- {availableTank}"))}";
                        throw new Exception(message);
                    }

                    resp.Add(new CreatePumpMappingRequest
                    {
                        FC_NozzleID = hose.RealId,
                        FC_PumpID = pump.Id,
                        FC_TankID = tankId,
                        FC_SiteID = request.FC_SiteId,
                        Omni_SiteID = request.Fintech_SiteId,
                        Omni_AssetPumpID = pumpObj != null ? pumpObj.AssetId : -1,
                        Omni_AssetNozzleID = nozzleObj != null ? nozzleObj.AssetId : -1,
                        Omni_TankID = tankObj != null ? tankObj.AssetId : -1
                    });
                }
            }

            if (mappingErrors.Length > 0)
            {
                string message = $@"Auto Configuration Failed

Missing mapped tank information.

Site: {request.Fintech_SiteName} ({request.FC_SiteId})
Configuration File: {Path.GetFileName(request.filePath)}

The following pump mappings could not be created:
{mappingErrors.ToString().TrimEnd()}";
                throw new Exception(message);
            }

            return resp;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    /// <summary>Appends one incomplete pump mapping using the standard configuration error structure.</summary>
    private static void AppendPumpMappingError(StringBuilder errors, int? pumpId, string? nozzle, string missingField)
    {
        errors.AppendLine($"Pump: {pumpId?.ToString() ?? "Not configured"}");
        errors.AppendLine($"Nozzle: {nozzle ?? "Not configured"}");
        errors.AppendLine("Expected Mapping: valid real_id and suction_high in pump-hose mapping");
        errors.AppendLine($"Missing Mapping Data: {missingField}");
        errors.AppendLine();
    }

    private static string getFinalProductName(string? tankProductName, ParseDataRequest request)
    {
        string? productName = tankProductName;
        if (int.TryParse(tankProductName, out int productId))
        {
            var gradeObj = request.Nodes.Grades.Find(f => f.Number == productId);
            productName = gradeObj != null ? gradeObj.Name : string.Empty;
        }

        return productName ?? string.Empty;
    }

    static string[] Tokenize(string value) =>
    Regex.Split(value ?? "", @"[^a-zA-Z0-9]+")
         .Where(x => !string.IsNullOrWhiteSpace(x))
         .ToArray();
}
