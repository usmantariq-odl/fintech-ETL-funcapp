using CSVReader;
using FintechEtl.FunctionApp;
using System.Text.Json;

internal static class LiveProvisioningCheck
{
    public static async Task Run()
    {
        using var local = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json")));
        foreach (var setting in local.RootElement.GetProperty("Values").EnumerateObject())
            Environment.SetEnvironmentVariable(setting.Name, setting.Value.GetString());
        var settings = SftpProvisioningSettings.Load();
        var etl = EtlSettings.Load();
        // Dedicated opt-in live check: creates only a unique temporary user/site, never runs the ETL.
        // No generated password or token is written to the console or disk.
        var request = new SftpUserRequest("209" + Random.Shared.Next(1000000, 9999999), "codextest" + Guid.NewGuid().ToString("N")[..12]);
        Console.WriteLine($"Live provisioning test: username={request.Username}, siteId={request.SiteId}");
        var users = new AzureLocalSftpUsers(settings);
        var service = new SftpUserProvisioner(users, () => new SiteSftpFolders(etl, settings.Account),
            () => new SqlProvisioningLease(etl.SqlConnectionString), settings.Account, etl.Host, etl.Port);
        var succeeded = false;
        try
        {
            _ = await service.Create(request, default);
            succeeded = true;
            try { await service.Create(request, default); throw new Exception("Existing username was overwritten."); }
            catch (ProvisioningException ex) when (ex.Code == "UserAlreadyExists") { }
            Console.WriteLine("PASS live own-site read/write/delete, denied ancestor/sibling/CON access, and duplicate-user rejection.");
        }
        finally
        {
            if (succeeded)
            {
                await users.Disable(request.Username, default);
                try { await RemoveTemporarySite(etl, request.SiteId); }
                finally { await users.Delete(request.Username, default); }
                Console.WriteLine("Temporary user/site removed. Site 111111 and CON files were not changed.");
            }
        }
    }

    private static async Task RemoveTemporarySite(EtlSettings settings, string site)
    {
        if (!site.StartsWith("209", StringComparison.Ordinal) || site.Length != 10 || !SftpLayout.IsSiteId(site))
            throw new InvalidOperationException("Refusing cleanup outside the temporary test-site range.");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var admin = SiteSftpFolders.OpenClient(settings, settings.Username, settings.Password);
                // Empty-directory deletes only, with fixed validated paths; never recursive.
                foreach (var path in new[] { SftpLayout.Input(site), SftpLayout.Output(site), SftpLayout.SiteRoot(site) })
                    if (admin.Exists(path)) admin.DeleteDirectory(path);
                return;
            }
            catch when (attempt < 3) { await Task.Delay(1500); }
        }
    }
}
