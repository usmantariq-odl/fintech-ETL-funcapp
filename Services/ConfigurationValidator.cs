using System.Globalization;

internal static class ConfigurationValidator
{
    internal static void Validate(ParseDataRequest request, List<SiteAndProductResponse> products)
    {
        var errors = new List<string>();
        if (request.FC_SiteId <= 0 || request.Fintech_SiteId <= 0 || request.Fintech_CustomerId <= 0)
            errors.Add("A positive FC site ID, mapped site ID and customer ID are required.");
        if (products.Count == 0 || products.Any(p => p.SiteId != request.Fintech_SiteId || p.CustomerId != request.Fintech_CustomerId))
            errors.Add("Customer products must belong to this site and customer.");
        var tanks = request.Nodes.Tanks;
        var pumps = request.Nodes.Pumps;
        var grades = request.Nodes.Grades;
        if (tanks.Count == 0) errors.Add("At least one tank definition is required.");
        if (pumps.Count == 0) errors.Add("At least one pump definition is required.");
        if (grades.Count == 0) errors.Add("At least one grade definition is required.");
        if (tanks.Any(t => t.Id is null or <= 0) || tanks.GroupBy(t => t.Id).Any(g => g.Count() > 1))
            errors.Add("Tank IDs must be positive and unique.");
        if (pumps.Any(p => p.Id is null or <= 0) || pumps.GroupBy(p => p.Id).Any(g => g.Count() > 1))
            errors.Add("Pump IDs must be positive and unique.");
        if (grades.Any(g => g.Number is null or <= 0 || string.IsNullOrWhiteSpace(g.Name)) ||
            grades.GroupBy(g => g.Number).Any(g => g.Count() > 1) ||
            grades.GroupBy(g => g.Name?.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            errors.Add("Grade numbers must be positive and unique; grade names must be non-empty and unique.");

        DocumentNodeResponse? Grade(string? value) => grades.Find(g =>
            string.Equals(g.Name, value?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && g.Number == number));

        foreach (var tank in tanks)
        {
            if (tank.TankCapacity is null or <= 0) errors.Add($"Tank {tank.Id}: capacity must be positive.");
            if (string.IsNullOrWhiteSpace(tank.ProductName) || Grade(tank.ProductName) is null)
                errors.Add($"Tank {tank.Id}: product '{tank.ProductName}' does not resolve to a configured grade.");
        }
        foreach (var pump in pumps)
        {
            if (pump.Hoses.Count == 0) errors.Add($"Pump {pump.Id}: at least one complete hose definition is required.");
            if (pump.HoseQuantity is null or <= 0 || pump.HoseQuantity != pump.Hoses.Count)
                errors.Add($"Pump {pump.Id}: hoses_qty must match the number of hose definitions.");
            if (pump.Hoses.GroupBy(h => h.RealId).Any(g => g.Count() > 1))
                errors.Add($"Pump {pump.Id}: nozzle real_id values must be unique within the pump.");
            foreach (var hose in pump.Hoses)
            {
                var label = $"Pump {pump.Id}, nozzle {hose.RealId?.ToString() ?? "missing"}";
                if (hose.RealId is null or <= 0) errors.Add($"{label}: real_id must be positive.");
                var grade = Grade(hose.Grade);
                if (string.IsNullOrWhiteSpace(hose.Grade) || grade is null)
                    errors.Add($"{label}: grade '{hose.Grade}' is missing or unknown.");
                var parts = hose.MappedTankName?.Split('-', 2, StringSplitOptions.TrimEntries);
                if (parts is null || parts.Length != 2 || !int.TryParse(parts[0], out var tankId) || string.IsNullOrWhiteSpace(parts[1]))
                {
                    errors.Add($"{label}: suction_high must identify an existing tank and product (for example 1-Diesel).");
                    continue;
                }
                var tank = tanks.Find(t => t.Id == tankId);
                if (tank is null) errors.Add($"{label}: suction_high references missing tank {tankId}.");
                else if (Grade(tank.ProductName)?.Number != grade?.Number || Grade(parts[1])?.Number != grade?.Number)
                    errors.Add($"{label}: hose grade, suction_high product and tank {tankId} product must agree.");
            }
        }
        var gradeValidation = HelperService.ValidateFileByGrades(request.document, products);
        if (gradeValidation != "true") errors.Add(gradeValidation);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Configuration rejected for site {request.FC_SiteId}, file {Path.GetFileName(request.filePath)}. Existing configuration was not changed.\n" + string.Join("\n", errors));

        // Exercise the actual mapping builders against a proposed in-memory asset
        // set. Missing suction/product/asset links must fail before any DELETE.
        var proposedAssets = HelperService.BuildSiteAssets(request).Select((a, i) => new AssetResponse
        {
            AssetId = i + 1, AssetName = a.Asset_Name, AssetType = a.AssetType
        }).ToList();
        ValidateMappings(HelperService.GetTankMappingRequestModels(request, proposedAssets),
            HelperService.GetPumpMappingRequestModels(request, proposedAssets));
    }

    internal static void ValidateMappings(List<CreateTankMappingRequest> tanks, List<CreatePumpMappingRequest> pumps)
    {
        if (tanks.Count == 0 || tanks.Any(t => t.Omni_AssetID is null or <= 0 || t.ProdId is null or <= 0 || t.Capacity is null or <= 0) ||
            pumps.Count == 0 || pumps.Any(p => p.Omni_AssetPumpID is null or <= 0 || p.Omni_AssetNozzleID is null or <= 0 || p.Omni_TankID is null or <= 0))
            throw new InvalidOperationException("Configuration contains incomplete tank, pump or nozzle asset mappings.");
    }
}
