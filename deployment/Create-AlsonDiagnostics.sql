-- Additive Alson diagnostics only. No changes to the shared transaction/tank tables.
SET XACT_ABORT ON;
IF DB_NAME()<>'FintechStagDB_NewArchitecture' THROW 51000,'Staging only',1;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.Alson_Fintech_Frames','GatewayReceivedAt') IS NULL
 ALTER TABLE dbo.Alson_Fintech_Frames ADD GatewayReceivedAt datetimeoffset(7) NULL;
IF COL_LENGTH('dbo.Alson_Fintech_Imports','MasterId') IS NULL
 ALTER TABLE dbo.Alson_Fintech_Imports ADD MasterId nvarchar(64) NULL;
IF COL_LENGTH('dbo.Alson_Fintech_Imports','CommandCode') IS NULL
 ALTER TABLE dbo.Alson_Fintech_Imports ADD CommandCode char(2) NULL;
EXEC(N'UPDATE i SET MasterId=f.MasterId,CommandCode=f.CommandCode FROM dbo.Alson_Fintech_Imports i JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=i.FrameHash WHERE i.MasterId IS NULL;
UPDATE f SET GatewayReceivedAt=x.Received FROM dbo.Alson_Fintech_Frames f
CROSS APPLY(SELECT TOP(1) TRY_CONVERT(datetimeoffset(7),JSON_VALUE(CONVERT(varchar(max),i.Envelope),''$.gatewayTs'')) Received FROM dbo.Alson_Fintech_Imports i WHERE i.FrameHash=f.FrameHash AND i.Outcome=''imported'')x
WHERE f.GatewayReceivedAt IS NULL AND x.Received IS NOT NULL;');
UPDATE dbo.Alson_Fintech_Imports SET Outcome='pending_diagnostics'
WHERE Outcome='unsupported' AND
 (JSON_VALUE(CONVERT(varchar(max),Envelope),'$.rawFrame') LIKE '#31,%'
 OR JSON_VALUE(CONVERT(varchar(max),Envelope),'$.rawFrame') LIKE '#70,%'
 OR JSON_VALUE(CONVERT(varchar(max),Envelope),'$.rawFrame') LIKE '#13,%'
 OR JSON_VALUE(CONVERT(varchar(max),Envelope),'$.rawFrame') LIKE '#27,%');
IF OBJECT_ID('dbo.Alson_Fintech_DiagnosticReceipts','U') IS NULL
CREATE TABLE dbo.Alson_Fintech_DiagnosticReceipts(
 ReceiptKey binary(32) NOT NULL PRIMARY KEY,
 MasterMappingID int NOT NULL REFERENCES dbo.Omni_Fintech_AlsonSiteMaster(ID),
 MasterId nvarchar(64) NOT NULL,SiteID int NOT NULL REFERENCES dbo.Plant_Site(Site_Id),
 CommandCode char(2) NOT NULL CHECK(CommandCode IN('31','70','13','27')),
 MessageId nvarchar(128) NULL,GatewayReceivedAt datetimeoffset(7) NULL,
 FrameHash binary(32) NOT NULL,RawFrame nvarchar(max) NOT NULL,RawBytes varbinary(max) NOT NULL,
 BatchID uniqueidentifier NOT NULL REFERENCES dbo.Alson_Fintech_Batches(BatchID),
 RecordedAtUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);
IF OBJECT_ID('dbo.Alson_Fintech_AutoManualEvents','U') IS NULL
CREATE TABLE dbo.Alson_Fintech_AutoManualEvents(
 ReceiptKey binary(32) NOT NULL PRIMARY KEY REFERENCES dbo.Alson_Fintech_DiagnosticReceipts(ReceiptKey),
 MasterMappingID int NOT NULL REFERENCES dbo.Omni_Fintech_AlsonSiteMaster(ID),
 MasterId nvarchar(64) NOT NULL,SiteID int NOT NULL,
 RawDispenserCode nvarchar(64) NOT NULL,
 PumpMappingID int NULL REFERENCES dbo.Omni_Fintech_PumpMapping(ID),
 ModeCode int NOT NULL CHECK(ModeCode IN(1,3)),ModeName varchar(10) NOT NULL,
 FileNumber nvarchar(64) NULL,SlaveCode nvarchar(64) NULL,NozzleNo int NULL,
 SourceAt datetime2(0) NULL,GatewayReceivedAt datetimeoffset(7) NULL
);
IF OBJECT_ID('dbo.Alson_Fintech_DspConnectionEvents','U') IS NULL
CREATE TABLE dbo.Alson_Fintech_DspConnectionEvents(
 ReceiptKey binary(32) NOT NULL REFERENCES dbo.Alson_Fintech_DiagnosticReceipts(ReceiptKey),
 RawDispenserCode nvarchar(64) NOT NULL,
 MasterMappingID int NOT NULL REFERENCES dbo.Omni_Fintech_AlsonSiteMaster(ID),
 MasterId nvarchar(64) NOT NULL,SiteID int NOT NULL,
 PumpMappingID int NULL REFERENCES dbo.Omni_Fintech_PumpMapping(ID),
 RawStatus nvarchar(64) NOT NULL,GatewayReceivedAt datetimeoffset(7) NULL,
 PRIMARY KEY(ReceiptKey,RawDispenserCode)
);
IF OBJECT_ID('dbo.Alson_Fintech_AtgConnectionEvents','U') IS NULL
CREATE TABLE dbo.Alson_Fintech_AtgConnectionEvents(
 ReceiptKey binary(32) NOT NULL PRIMARY KEY REFERENCES dbo.Alson_Fintech_DiagnosticReceipts(ReceiptKey),
 MasterMappingID int NOT NULL REFERENCES dbo.Omni_Fintech_AlsonSiteMaster(ID),
 MasterId nvarchar(64) NOT NULL,SiteID int NOT NULL,
 RawStatus nvarchar(64) NOT NULL,GatewayReceivedAt datetimeoffset(7) NULL
);
IF OBJECT_ID('dbo.Alson_Fintech_ProbeStatusEvents','U') IS NULL
CREATE TABLE dbo.Alson_Fintech_ProbeStatusEvents(
 ReceiptKey binary(32) NOT NULL PRIMARY KEY REFERENCES dbo.Alson_Fintech_DiagnosticReceipts(ReceiptKey),
 MasterMappingID int NOT NULL REFERENCES dbo.Omni_Fintech_AlsonSiteMaster(ID),
 MasterId nvarchar(64) NOT NULL,SiteID int NOT NULL,ProbeCode nvarchar(64) NOT NULL,
 TankMappingID int NULL REFERENCES dbo.Omni_Fintech_TankMapping(ID),
 StatusType nvarchar(64) NOT NULL,StatusValue nvarchar(64) NOT NULL,
 GatewayReceivedAt datetimeoffset(7) NULL
);
IF COL_LENGTH('dbo.Alson_Fintech_ProbeStatusEvents','ConnectionState') IS NULL
 ALTER TABLE dbo.Alson_Fintech_ProbeStatusEvents ADD ConnectionState AS
 (CASE WHEN StatusType='STAT' AND StatusValue='0' THEN CONVERT(varchar(12),'Connected')
       WHEN StatusType='STAT' AND StatusValue='2' THEN CONVERT(varchar(12),'Disconnected') END);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Alson_Fintech_DspConnectionEvents') AND name='IX_Alson_Dsp_LastSeen')
 CREATE INDEX IX_Alson_Dsp_LastSeen ON dbo.Alson_Fintech_DspConnectionEvents(PumpMappingID,GatewayReceivedAt DESC);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Alson_Fintech_AutoManualEvents') AND name='IX_Alson_Mode_LastSeen')
 CREATE INDEX IX_Alson_Mode_LastSeen ON dbo.Alson_Fintech_AutoManualEvents(PumpMappingID,GatewayReceivedAt DESC);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Alson_Fintech_ProbeStatusEvents') AND name='IX_Alson_Probe_LastSeen')
 CREATE INDEX IX_Alson_Probe_LastSeen ON dbo.Alson_Fintech_ProbeStatusEvents(TankMappingID,GatewayReceivedAt DESC);
COMMIT TRANSACTION;
EXEC(N'CREATE OR ALTER VIEW dbo.Alson_Fintech_DspLastSeen AS
SELECT p.Omni_SiteID SiteID,p.MasterId,p.AlsonPumpCode,p.FC_PumpID,p.FC_NozzleID,
c.GatewayReceivedAt LastConnectionAtUtc,c.RawStatus LastConnectionStatus,
am.GatewayReceivedAt LastModeReceivedAtUtc,am.ModeName,am.SourceAt ModeDeviceTime,
d.GatewayReceivedAt LastSaleReceivedAtUtc,d.SourceAt LastSaleDeviceTime
FROM dbo.Omni_Fintech_PumpMapping p
OUTER APPLY(SELECT TOP(1) GatewayReceivedAt,RawStatus FROM dbo.Alson_Fintech_DspConnectionEvents e WHERE e.PumpMappingID=p.ID ORDER BY GatewayReceivedAt DESC)c
OUTER APPLY(SELECT TOP(1) GatewayReceivedAt,ModeName,SourceAt FROM dbo.Alson_Fintech_AutoManualEvents e WHERE e.PumpMappingID=p.ID ORDER BY GatewayReceivedAt DESC)am
OUTER APPLY(SELECT TOP(1) f.GatewayReceivedAt,f.SourceAt FROM dbo.Alson_Fintech_Rows r JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash WHERE r.EntityType=''TXN'' AND r.MappingID=p.ID ORDER BY f.GatewayReceivedAt DESC)d
WHERE NULLIF(p.MasterId,'''') IS NOT NULL AND NULLIF(p.AlsonPumpCode,'''') IS NOT NULL;');
EXEC(N'CREATE OR ALTER VIEW dbo.Alson_Fintech_ProbeLastSeen AS
SELECT m.Omni_SiteID SiteID,q.MasterId,q.ProbeCode,m.FC_TankID,m.Omni_AssetID,
c.GatewayReceivedAt LastStatusReceivedAtUtc,c.StatusType,c.StatusValue,c.ConnectionState,
d.GatewayReceivedAt LastReadingReceivedAtUtc,d.SourceAt LastReadingDeviceTime,
a.GatewayReceivedAt LastMasterConnectionAtUtc,a.RawStatus LastMasterConnectionStatus
FROM dbo.Alson_Fintech_ProbeConfig q JOIN dbo.Omni_Fintech_TankMapping m ON m.ID=q.TankMappingID
OUTER APPLY(SELECT TOP(1) GatewayReceivedAt,StatusType,StatusValue,ConnectionState FROM dbo.Alson_Fintech_ProbeStatusEvents e WHERE e.TankMappingID=m.ID ORDER BY GatewayReceivedAt DESC)c
OUTER APPLY(SELECT TOP(1) f.GatewayReceivedAt,f.SourceAt FROM dbo.Alson_Fintech_Rows r JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash WHERE r.EntityType=''ATG'' AND r.MappingID=m.ID ORDER BY f.GatewayReceivedAt DESC)d
OUTER APPLY(SELECT TOP(1) GatewayReceivedAt,RawStatus FROM dbo.Alson_Fintech_AtgConnectionEvents e WHERE e.MasterId COLLATE Latin1_General_100_BIN2=q.MasterId AND e.SiteID=m.Omni_SiteID ORDER BY GatewayReceivedAt DESC)a;');

