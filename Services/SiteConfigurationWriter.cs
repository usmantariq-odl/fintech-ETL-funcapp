internal sealed class SiteConfigurationWriter(SQLService sql)
{
    internal async Task ReplaceAsync(ParseDataRequest request, List<SiteAndProductResponse> products, bool? activationAllowed)
    {
        ConfigurationValidator.Validate(request, products);
        await sql.ExecuteConfigurationTransactionAsync(request.Fintech_SiteId, async () =>
        {
            // Clear assets/mappings only. Never delete the existing activation row.
            await sql.ClearSiteConfiguration(request.Fintech_CustomerId, request.Fintech_SiteId);
            if (!await HelperService.CreateSiteAssets(request, sql))
                throw new InvalidOperationException("Creating the replacement assets failed; configuration replacement was rolled back.");
            var assets = await sql.GetAssetsBySiteId(request.Fintech_SiteId);
            var tanks = HelperService.GetTankMappingRequestModels(request, assets);
            var pumps = HelperService.GetPumpMappingRequestModels(request, assets);
            ConfigurationValidator.ValidateMappings(tanks, pumps);
            if (!await sql.SaveTankMapping(tanks, request.Fintech_SiteId) ||
                !await sql.SavePumpMapping(pumps, request.Fintech_SiteId))
                throw new InvalidOperationException("Saving the replacement mappings failed; configuration replacement was rolled back.");
            if (activationAllowed == true && !await sql.MarkSiteActivated(request.Fintech_CustomerId, request.Fintech_SiteId))
                throw new InvalidOperationException("Activating the replacement failed; configuration replacement was rolled back.");
        });
    }
}
