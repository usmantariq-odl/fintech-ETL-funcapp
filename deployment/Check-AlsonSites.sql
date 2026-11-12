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
SELECT * FROM dbo.Fintech_Transactions WHERE Site_ID IN(1191,1192) ORDER BY Site_ID,ID;
SELECT * FROM dbo.Fintech_TankLevels WHERE Site_ID IN(1191,1192) ORDER BY Site_ID,ID;
SELECT * FROM dbo.Alson_Fintech_AutoManualEvents WHERE SiteID IN(1191,1192) ORDER BY GatewayReceivedAt DESC;
SELECT * FROM dbo.Alson_Fintech_DspConnectionEvents WHERE SiteID IN(1191,1192) ORDER BY GatewayReceivedAt DESC;
SELECT * FROM dbo.Alson_Fintech_AtgConnectionEvents WHERE SiteID IN(1191,1192) ORDER BY GatewayReceivedAt DESC;
-- ConnectionState: STAT 0=Connected, 2=Disconnected, other statuses=NULL.
SELECT * FROM dbo.Alson_Fintech_ProbeStatusEvents WHERE SiteID IN(1191,1192) ORDER BY GatewayReceivedAt DESC;
-- Last received connection/mode versus last data, no timeout classification.
SELECT * FROM dbo.Alson_Fintech_DspLastSeen WHERE SiteID IN(1191,1192);
SELECT * FROM dbo.Alson_Fintech_ProbeLastSeen WHERE SiteID IN(1191,1192);
SELECT * FROM dbo.Alson_Fintech_DiagnosticReceipts WHERE SiteID IN(1191,1192);
SELECT * FROM dbo.Alson_Fintech_Rows WHERE SiteID IN(1191,1192);
SELECT i.* FROM dbo.Alson_Fintech_Imports i
WHERE EXISTS(SELECT 1 FROM dbo.Omni_Fintech_AlsonSiteMaster m WHERE m.Omni_SiteID IN(1191,1192) AND m.MasterId COLLATE Latin1_General_100_BIN2=i.MasterId COLLATE Latin1_General_100_BIN2);
SELECT f.* FROM dbo.Alson_Fintech_Frames f
WHERE EXISTS(SELECT 1 FROM dbo.Alson_Fintech_Rows r WHERE r.FrameHash=f.FrameHash AND r.SiteID IN(1191,1192));
SELECT b.* FROM dbo.Alson_Fintech_Batches b
WHERE EXISTS(SELECT 1 FROM dbo.Alson_Fintech_Imports i WHERE i.BatchID=b.BatchID AND EXISTS(SELECT 1 FROM dbo.Omni_Fintech_AlsonSiteMaster m WHERE m.Omni_SiteID IN(1191,1192) AND m.MasterId COLLATE Latin1_General_100_BIN2=i.MasterId COLLATE Latin1_General_100_BIN2))
OR b.BatchID='51662362-1009-453e-a08a-4e5bc183b38e';
