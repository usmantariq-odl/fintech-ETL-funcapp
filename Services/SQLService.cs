using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;
using System.Text;

public class SQLService
{
    private readonly IOptions<ApplicationSettings> _settings;
    private SqlConnection? configurationConnection;
    private SqlTransaction? configurationTransaction;

    public SQLService(IOptions<ApplicationSettings> settings)
    {
        _settings = settings;
    }

    public virtual async Task<List<SiteAndProductResponse>> GetOmniSiteIdAndProducts(int FC_SiteId)
    {
        try
        {
            string query = $@"SELECT Site_Id, Customer_Id_FK, p.product_id, p.product_name 
FROM Plant_Site ps
INNER JOIN Omni_Fintech_SiteMapping sm
ON ps.Site_Id = sm.Omni_SiteID
INNER JOIN Omni_Fintech_Products p
ON p.OmniCustomerID_FK = ps.Customer_Id_FK
WHERE sm.FC_SiteID = @FCSiteId";


            var resp = await ExecuteReaderAsync(query,
                        r => new SiteAndProductResponse
                        {
                            SiteId = r.GetInt32("Site_Id"),
                            CustomerId = r.GetInt32("Customer_Id_FK"),
                            ProductId = r.GetInt32("product_id"),
                            ProductName = r.GetString("product_name")
                        },
                        new SqlParameter("@FCSiteId", FC_SiteId));
            return resp;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public async Task<int> GetAssetsCountBySiteId(int Fintech_SiteId)
    {
        try
        {
            string query = $@"select COUNT(*) [TotalCount] from Assets where site_id_fk = @siteId";

            var resp = await ExecuteReaderAsync(query,
                        r => r.GetInt32("TotalCount"),
                        new SqlParameter("@siteId", Fintech_SiteId));
            return resp.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public virtual async Task<List<AssetResponse>> GetAssetsBySiteId(int Fintech_SiteId)
    {
        try
        {
            string query = $@"select Asset_Id, Asset_Name, AssetType from Assets where site_id_fk = @siteId";

            var resp = await ExecuteReaderAsync(query,
                        r => new AssetResponse
                        {
                            AssetId = r.GetInt32("Asset_Id"),
                            AssetName = r.GetString("Asset_Name"),
                            AssetType = r.GetString("AssetType")
                        },
                        new SqlParameter("@siteId", Fintech_SiteId));
            return resp;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Replaceable site configuration only; the activation record is preserved. Includes
    /// Assets
    /// TankMapping
    /// PumpMapping
    /// </summary>
    /// <param name="customerId"></param>
    /// <param name="siteId"></param>
    /// <returns>boolean: true/false</returns>
    public virtual async Task<bool> ClearSiteConfiguration(int customerId, int siteId)
    {
        if (configurationTransaction is null)
            throw new InvalidOperationException("Configuration can only be cleared inside an atomic replacement transaction.");
        try
        {
            string query = $@"DELETE FROM Omni_Fintech_TankMapping WHERE Omni_SiteID = {siteId}
DELETE FROM Omni_Fintech_PumpMapping WHERE Omni_SiteID = {siteId}
DELETE FROM Asset_Asset_Area_M_To_M WHERE Asset_Id_FK IN (SELECT Asset_Id FROM Assets WHERE Site_Id_FK = {siteId})
DELETE FROM Assets WHERE Site_Id_FK = {siteId}
DELETE FROM Asset_Area WHERE Site_Id_FK = {siteId}";

            return await ExecuteNonQueryAsync(query);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public virtual async Task<int> CreateArea(int Fintech_SiteId, string Fintech_SiteName)
    {
        try
        {
            string areaName = $"{Fintech_SiteName} Area",
                query = $@"DECLARE @areaId INT = 0;
SELECT @areaId = Area_Id FROM Asset_Area
WHERE LOWER(Area_Name) = LOWER('{EscapeSqlLiteral(areaName)}') AND Site_Id_FK = {Fintech_SiteId}

IF @areaId <= 0
	BEGIN
		INSERT INTO [dbo].[Asset_Area]([Area_Name],[Description],[Site_Id_FK],[Created_By],[Created_Date])
		VALUES('{EscapeSqlLiteral(areaName)}', 'Area of site {EscapeSqlLiteral(Fintech_SiteName)}({Fintech_SiteId}) by {ApplicationConstants.JOB_NAME} Job', {Fintech_SiteId}, '{ApplicationConstants.JOB_NAME}', GETDATE());
		SET @areaId = CAST(SCOPE_IDENTITY() AS INT);
	END

SELECT @areaId [AreaId]";


            var resp = await ExecuteReaderAsync(query,
                        r => r.GetInt32("AreaId"));
            return resp.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public virtual async Task<bool> CreateAssets(List<CreateAssetRequest> assets)
    {
        try
        {
            if (assets.Count == 0)
                throw new InvalidOperationException("No tank or pump assets were found in the configuration file.");

            StringBuilder queryBuilder = new StringBuilder(@"INSERT INTO [dbo].[Assets]([Asset_Name],[Type],[Description],[Created_By],[Created_Date],[Site_Id_FK],[AssetType])
VALUES");
            queryBuilder.AppendLine(string.Join(",", assets.Select(a => $"('{EscapeSqlLiteral(a.Asset_Name)}', '{EscapeSqlLiteral(a.Type)}', '{EscapeSqlLiteral(a.Description)}', '{EscapeSqlLiteral(a.Created_By)}', GETDATE(), {a.Site_Id_FK}, '{a.AssetType}')")));

            return await ExecuteNonQueryAsync(queryBuilder.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public virtual async Task<bool> InsertIntoMappingTable(int areaId, int fintech_SiteId)
    {
        try
        {
            string query = $@"DELETE FROM Asset_Asset_Area_M_To_M
WHERE Area_Id_FK = {areaId}

INSERT INTO Asset_Asset_Area_M_To_M
SELECT Asset_Id, {areaId} FROM Assets
WHERE Site_Id_FK = {fintech_SiteId}";

            return await ExecuteNonQueryAsync(query);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public virtual async Task<bool> SaveTankMapping(List<CreateTankMappingRequest> tankMappings, int fintech_siteId)
    {
        try
        {
            if (tankMappings.Count == 0)
                throw new InvalidOperationException($"No tank mappings were found for site {fintech_siteId}.");

            StringBuilder queryBuilder = new StringBuilder($@"DELETE FROM [dbo].[Omni_Fintech_TankMapping] WHERE [Omni_SiteID] = {fintech_siteId};
INSERT INTO [dbo].[Omni_Fintech_TankMapping]([FC_TankID],[FC_SiteID],[Omni_AssetID],[Omni_SiteID],[CreatedOn],[CreatedBy],[ProdID_FK],[Capacity])
VALUES");
            queryBuilder.AppendLine(string.Join(",", tankMappings.Select(t => $"({t.FC_TankID}, {t.FC_SiteID}, {t.Omni_AssetID}, {t.Omni_SiteID}, GETDATE(), '{t.CreatedBy}', {t.ProdId}, {t.Capacity})")));

            return await ExecuteNonQueryAsync(queryBuilder.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public virtual async Task<bool> SavePumpMapping(List<CreatePumpMappingRequest> pumpMappings, int fintech_siteId)
    {
        try
        {
            if (pumpMappings.Count == 0)
                throw new InvalidOperationException($"No pump mappings were found");

            StringBuilder queryBuilder = new StringBuilder($@"DELETE FROM [dbo].[Omni_Fintech_PumpMapping] WHERE [Omni_SiteID] = {fintech_siteId};
INSERT INTO [dbo].[Omni_Fintech_PumpMapping]([FC_NozzleID],[FC_PumpID],[FC_TankID],[FC_SiteID],[Omni_SiteID],[Omni_AssetPumpID],[Omni_AssetNozzleID],[Omni_TankID])
VALUES");
            queryBuilder.AppendLine(string.Join(",", pumpMappings.Select(p => $"({p.FC_NozzleID}, {p.FC_PumpID}, {p.FC_TankID}, {p.FC_SiteID}, {p.Omni_SiteID}, {p.Omni_AssetPumpID}, {p.Omni_AssetNozzleID}, {p.Omni_TankID})")));

            return await ExecuteNonQueryAsync(queryBuilder.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public virtual async Task<bool> MarkSiteActivated(int customerId, int siteId)
    {
        try
        {
            string query = $@"IF NOT EXISTS (SELECT 1 FROM FintechMappedCustomers WHERE CustomerId = {customerId} AND siteId = {siteId})
	BEGIN
		INSERT INTO FintechMappedCustomers(CustomerId,SiteId,IsActive,LastActivatedOn)
		VALUES({customerId},{siteId},1,GETDATE());
	END
ELSE
	BEGIN
		UPDATE FintechMappedCustomers
		SET LastActivatedOn = CASE WHEN IsActive = 1 THEN LastActivatedOn ELSE GETDATE() END, IsActive = 1
		WHERE CustomerId = {customerId} AND SiteId = {siteId}
	END


IF NOT EXISTS (SELECT 1 FROM Fintech_Pointer_new WHERE Site_ID = {siteId})
	BEGIN
		INSERT INTO Fintech_Pointer_new (ID, Site_ID, FC_StartDateTime, FC_EndDateTime, New_Site, manual_entry, ReconTime) 
                       SELECT ISNULL(MAX(ID), 0) + 1, {siteId}, '{DateTime.Now.AddDays(-2)}', NULL, 1, 0, NULL 
                       FROM Fintech_Pointer_new
	END";

            return await ExecuteNonQueryAsync(query);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    public virtual async Task<List<SiteMetaDataResponse>> GetSitesMetaData()
    {
        try
        {
            string query = $@"SELECT sm.FC_SiteID, ps.Site_Id, ps.Site_Name, ps.Customer_Id_FK, v.VendorName, ps.IsConfigurationAllowed, 
ps.IsActivationAllowed, mc.IsActive, u.Email, ps.AutoConfigurationStatus, ps.AutoConfigurationExceptionMessage
FROM Plant_Site ps
INNER JOIN Omni_Fintech_SiteMapping sm
ON ps.Site_Id = sm.Omni_SiteID
LEFT JOIN FintechMappedCustomers mc
ON mc.CustomerId = ps.Customer_Id_FK AND mc.SiteId = ps.Site_Id
LEFT JOIN FintechUsers u
ON u.UserName = ps.Updated_By OR u.Id = ps.Updated_By
LEFT JOIN Vendor v
ON v.VendorId = ps.VendorIdFk
WHERE IsConfigurationAllowed = 1 OR IsActivationAllowed = 1";

            var resp = await ExecuteReaderAsync(query,
                        r => new SiteMetaDataResponse
                        {
                            FCSiteId = r.GetInt32("FC_SiteID"),
                            FintechSiteId = r.GetInt32("Site_Id"),
                            FintechSiteName = r.GetString("Site_Name"),
                            FintechCustomerId = r.GetInt32("Customer_Id_FK"),
                            VendorName = r.IsDBNull("VendorName") ? null : r.GetString("VendorName"),
                            IsConfigurationAllowed = r.IsDBNull("IsConfigurationAllowed") ? null : r.GetBoolean("IsConfigurationAllowed"),
                            IsActivationAllowed = r.IsDBNull("IsActivationAllowed") ? null : r.GetBoolean("IsActivationAllowed"),
                            IsActive = r.IsDBNull("IsActive") ? null : r.GetBoolean("IsActive"),
                            UserEmail = r.IsDBNull("Email") ? null : r.GetString("Email"),
                            Status = r.IsDBNull("AutoConfigurationStatus") ? null : r.GetString("AutoConfigurationStatus"),
                            Exception = r.IsDBNull("AutoConfigurationExceptionMessage") ? null : r.GetString("AutoConfigurationExceptionMessage")
                        });
            return resp;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    // Every replacement command uses this one connection/transaction. The service
    // is scoped to a single timer scan and replacements are processed sequentially.
    public virtual async Task ExecuteConfigurationTransactionAsync(int siteId, Func<Task> replace)
    {
        if (configurationConnection is not null)
            throw new InvalidOperationException("Nested configuration replacements are not supported.");
        await using var connection = new SqlConnection(_settings.Value.AzureSql);
        await connection.OpenAsync();
        await SqlConfigurationTransaction.RunAsync(connection, async transaction =>
        {
            configurationConnection = connection;
            configurationTransaction = transaction;
            try
            {
                // Serialize competing replacements of this site; hold the row lock
                // until the complete assets/mappings/activation change commits.
                var sites = await ExecuteReaderAsync("SELECT Site_Id FROM Plant_Site WITH (UPDLOCK, HOLDLOCK) WHERE Site_Id = @siteId",
                    row => row.GetInt32(0), new SqlParameter("@siteId", siteId));
                if (sites.Count != 1) throw new InvalidOperationException("The site no longer exists; configuration replacement was cancelled.");
                await replace();
            }
            finally
            {
                configurationConnection = null;
                configurationTransaction = null;
            }
        });
    }

    public async Task<List<T>> ExecuteReaderAsync<T>(string query, Func<SqlDataReader, T> mapper, params SqlParameter[] parameters)
    {
        var connection = configurationConnection ?? new SqlConnection(_settings.Value.AzureSql);
        bool ownsConnection = configurationConnection is null;
        try
        {
            if (ownsConnection) await connection.OpenAsync();
            await using var command = new SqlCommand(query, connection, configurationTransaction);
            if (parameters.Length > 0) command.Parameters.AddRange(parameters);
            await using var reader = await command.ExecuteReaderAsync();
            var result = new List<T>();
            while (await reader.ReadAsync()) result.Add(mapper(reader));
            return result;
        }
        finally { if (ownsConnection) await connection.DisposeAsync(); }
    }

    public async Task<bool> ExecuteNonQueryAsync(string query)
    {
        var connection = configurationConnection ?? new SqlConnection(_settings.Value.AzureSql);
        bool ownsConnection = configurationConnection is null;
        try
        {
            if (ownsConnection) await connection.OpenAsync();
            await using var command = new SqlCommand(query, connection, configurationTransaction);
            return await command.ExecuteNonQueryAsync() > 0;
        }
        finally { if (ownsConnection) await connection.DisposeAsync(); }
    }

    public virtual async Task<bool> UpdateSiteStatus(List<SiteMetaDataResponse> sites)
    {
        try
        {
            bool isUpdated = false;
            if (sites.Count > 0)
            {
                StringBuilder query = new StringBuilder($@"CREATE TABLE #Sites(SiteId INT, SiteStatus NVARCHAR(50), ExceptionMessage NVARCHAR(MAX))
INSERT INTO #Sites(SiteId, SiteStatus, ExceptionMessage) VALUES");
                query.AppendLine(string.Join(',', sites.Select(s => $"({s.FintechSiteId}, '{EscapeSqlLiteral(s.Status)}', '{EscapeSqlLiteral(s.Exception)}')")));
                query.AppendLine(@"UPDATE ps
SET AutoConfigurationStatus = s.SiteStatus, AutoConfigurationExceptionMessage = s.ExceptionMessage
FROM Plant_Site ps
INNER JOIN #Sites s
ON ps.Site_Id = s.SiteId

DROP TABLE #Sites");
                isUpdated = await ExecuteNonQueryAsync(query.ToString());
            }

            return isUpdated;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    /// <summary>Escapes a value embedded in a SQL string literal.</summary>
    private static string EscapeSqlLiteral(string? value)
    {
        return (value ?? string.Empty).Replace("'", "''");
    }
}