-- READ ONLY: Alson staging review, customer 227.
-- Makkah: Site_ID 1191 / FC_SiteID 560257.
-- Manzoor: Site_ID 1192 / FC_SiteID 560256.
-- Recent business/event records are limited to 100 per result set across both sites.
-- No schema/configuration changes, stored-procedure execution or data writes.
USE FintechStagDB_NewArchitecture;
SET NOCOUNT ON;

-- Current totals. The enabled timer continues to add records.
SELECT Site_ID,COUNT_BIG(*) AS Transactions FROM dbo.Fintech_Transactions
WHERE Site_ID IN(1191,1192) GROUP BY Site_ID;
SELECT Site_ID,COUNT_BIG(*) AS TankReadings FROM dbo.Fintech_TankLevels
WHERE Site_ID IN(1191,1192) GROUP BY Site_ID;

-- Resolve native Alson grades by CUSTOMER + SITE + GRADE.
-- ProductRowID references Omni_Fintech_Products.Id, not product_id.
SELECT m.CustomerID,m.OmniSiteID,m.RawCode,m.AlsonGradeID,
       m.ProductRowID,p.product_id AS CanonicalProductID,p.product_name
FROM dbo.Alson_Fintech_ProductMap m
JOIN dbo.Omni_Fintech_Products p ON p.Id=m.ProductRowID AND p.OmniCustomerID_FK=m.CustomerID
WHERE m.CustomerID=227 AND m.OmniSiteID IN(1191,1192)
ORDER BY m.OmniSiteID,m.AlsonGradeID;

-- Configured-site processing outcomes and unresolved failures.
SELECT m.Omni_SiteID,i.MasterId,i.Outcome,COUNT_BIG(*) AS Files,MAX(i.UpdatedOn) AS LatestUtc
FROM dbo.Alson_Fintech_Imports i
JOIN dbo.Omni_Fintech_AlsonSiteMaster m
 ON m.MasterId COLLATE Latin1_General_100_BIN2=i.MasterId COLLATE Latin1_General_100_BIN2
WHERE m.Omni_SiteID IN(1191,1192)
GROUP BY m.Omni_SiteID,i.MasterId,i.Outcome;
SELECT TOP (100) i.MasterId,i.BlobName,i.Outcome,i.ErrorText,i.UpdatedOn
FROM dbo.Alson_Fintech_Imports i
WHERE i.Outcome IN('invalid','pending_mapping')
AND EXISTS(SELECT 1 FROM dbo.Omni_Fintech_AlsonSiteMaster m WHERE m.Omni_SiteID IN(1191,1192)
 AND m.MasterId COLLATE Latin1_General_100_BIN2=i.MasterId COLLATE Latin1_General_100_BIN2)
ORDER BY i.UpdatedOn DESC;

-- Site/master/product/probe/tank/pump configuration and sample inserted records.
-- Read only. New Makkah and Manzoor sites, customer 227.
USE FintechStagDB_NewArchitecture;
SELECT * FROM dbo.Plant_Site WHERE Site_Id IN(1191,1192);
SELECT * FROM dbo.Omni_Fintech_SiteMapping WHERE Omni_SiteID IN(1191,1192);
SELECT * FROM dbo.Omni_Fintech_AlsonSiteMaster WHERE Omni_SiteID IN(1191,1192);
SELECT * FROM dbo.Alson_Fintech_ProductMap WHERE CustomerID=227 AND OmniSiteID IN(1191,1192);
SELECT * FROM dbo.Omni_Fintech_TankMapping WHERE Omni_SiteID IN(1191,1192);
SELECT q.* FROM dbo.Alson_Fintech_ProbeConfig q JOIN dbo.Omni_Fintech_TankMapping t ON t.ID=q.TankMappingID WHERE t.Omni_SiteID IN(1191,1192);
SELECT * FROM dbo.Omni_Fintech_PumpMapping WHERE Omni_SiteID IN(1191,1192);
SELECT * FROM dbo.Assets WHERE Site_Id_FK IN(1191,1192);
SELECT * FROM dbo.OPC_SourceTags WHERE Site__Id_FK IN(1191,1192);
SELECT TOP (100) * FROM dbo.Fintech_Transactions WHERE Site_ID IN(1191,1192) ORDER BY ID DESC;
SELECT TOP (100) * FROM dbo.Fintech_TankLevels WHERE Site_ID IN(1191,1192) ORDER BY ID DESC;
SELECT TOP (100) * FROM dbo.Alson_Fintech_AutoManualEvents WHERE SiteID IN(1191,1192) ORDER BY GatewayReceivedAt DESC;
SELECT TOP (100) * FROM dbo.Alson_Fintech_DspConnectionEvents WHERE SiteID IN(1191,1192) ORDER BY GatewayReceivedAt DESC;
SELECT TOP (100) * FROM dbo.Alson_Fintech_AtgConnectionEvents WHERE SiteID IN(1191,1192) ORDER BY GatewayReceivedAt DESC;
-- ConnectionState: STAT 0=Connected, 2=Disconnected, other statuses=NULL.
SELECT TOP (100) * FROM dbo.Alson_Fintech_ProbeStatusEvents WHERE SiteID IN(1191,1192) ORDER BY GatewayReceivedAt DESC;
-- Last received connection/mode versus last data, no timeout classification.
SELECT * FROM dbo.Alson_Fintech_DspLastSeen WHERE SiteID IN(1191,1192);
SELECT * FROM dbo.Alson_Fintech_ProbeLastSeen WHERE SiteID IN(1191,1192);
SELECT TOP (100) * FROM dbo.Alson_Fintech_DiagnosticReceipts WHERE SiteID IN(1191,1192) ORDER BY GatewayReceivedAt DESC;
SELECT TOP (100) * FROM dbo.Alson_Fintech_Rows WHERE SiteID IN(1191,1192) ORDER BY TargetID DESC;
SELECT TOP (100) i.* FROM dbo.Alson_Fintech_Imports i
WHERE EXISTS(SELECT 1 FROM dbo.Omni_Fintech_AlsonSiteMaster m WHERE m.Omni_SiteID IN(1191,1192) AND m.MasterId COLLATE Latin1_General_100_BIN2=i.MasterId COLLATE Latin1_General_100_BIN2) ORDER BY i.UpdatedOn DESC;
SELECT TOP (100) f.* FROM dbo.Alson_Fintech_Frames f
WHERE EXISTS(SELECT 1 FROM dbo.Alson_Fintech_Rows r WHERE r.FrameHash=f.FrameHash AND r.SiteID IN(1191,1192)) ORDER BY f.SourceAt DESC;
SELECT TOP (100) b.* FROM dbo.Alson_Fintech_Batches b
WHERE EXISTS(SELECT 1 FROM dbo.Alson_Fintech_Imports i WHERE i.BatchID=b.BatchID AND EXISTS(SELECT 1 FROM dbo.Omni_Fintech_AlsonSiteMaster m WHERE m.Omni_SiteID IN(1191,1192) AND m.MasterId COLLATE Latin1_General_100_BIN2=i.MasterId COLLATE Latin1_General_100_BIN2))
OR b.BatchID='51662362-1009-453e-a08a-4e5bc183b38e' ORDER BY b.CreatedOn DESC;

