-- Additive Alson-only metadata. Neither Fintech target table is altered.
SET XACT_ABORT ON;
IF DB_NAME()<>N'FintechStagDB_NewArchitecture' THROW 51000,'Alson migration is staging only.',1;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'AlsonFintech:Schema',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51001,'Alson schema lock unavailable.',1;
IF OBJECT_ID(N'dbo.Alson_Fintech_Batches',N'U') IS NULL
CREATE TABLE dbo.Alson_Fintech_Batches (
 BatchID uniqueidentifier NOT NULL PRIMARY KEY, Kind varchar(20) NOT NULL,
 CreatedOn datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(), Manifest nvarchar(max) NULL
);
IF OBJECT_ID(N'dbo.Alson_Fintech_ProductMap',N'U') IS NULL
CREATE TABLE dbo.Alson_Fintech_ProductMap (
 CustomerID int NOT NULL, OmniSiteID int NOT NULL, RawCode varchar(8) COLLATE Latin1_General_100_BIN2 NOT NULL,
 ProductRowID int NOT NULL, AlsonGradeID int NULL, BatchID uniqueidentifier NOT NULL,
 PRIMARY KEY(CustomerID,OmniSiteID,RawCode), FOREIGN KEY(ProductRowID) REFERENCES dbo.Omni_Fintech_Products(Id),
 FOREIGN KEY(OmniSiteID) REFERENCES dbo.Plant_Site(Site_Id),
 FOREIGN KEY(BatchID) REFERENCES dbo.Alson_Fintech_Batches(BatchID)
);
IF COL_LENGTH(N'dbo.Alson_Fintech_ProductMap',N'AlsonGradeID') IS NULL
 ALTER TABLE dbo.Alson_Fintech_ProductMap ADD AlsonGradeID int NULL;
IF COL_LENGTH(N'dbo.Alson_Fintech_ProductMap',N'OmniSiteID') IS NULL
 THROW 51002,'Apply the reviewed Upgrade-AlsonSiteProducts migration for a legacy customer-only map.',1;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Alson_Fintech_ProductMap') AND name=N'UX_Alson_ProductMap_SiteGrade')
 EXEC(N'CREATE UNIQUE INDEX UX_Alson_ProductMap_SiteGrade ON dbo.Alson_Fintech_ProductMap(CustomerID,OmniSiteID,AlsonGradeID) WHERE AlsonGradeID IS NOT NULL');
IF OBJECT_ID(N'dbo.Alson_Fintech_ProbeConfig',N'U') IS NULL
CREATE TABLE dbo.Alson_Fintech_ProbeConfig (
 MasterId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 ProbeCode nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 TankMappingID int NOT NULL, FuelKind varchar(6) NULL, WaterKind varchar(6) NULL,
 FuelScale decimal(18,9) NULL, WaterScale decimal(18,9) NULL,
 MaxFuel decimal(18,3) NULL, MaxWater decimal(18,3) NULL,
 MinTemperature decimal(10,3) NOT NULL DEFAULT(-50), MaxTemperature decimal(10,3) NOT NULL DEFAULT(100),
 BatchID uniqueidentifier NOT NULL, PRIMARY KEY(MasterId,ProbeCode),
 FOREIGN KEY(TankMappingID) REFERENCES dbo.Omni_Fintech_TankMapping(ID),
 FOREIGN KEY(BatchID) REFERENCES dbo.Alson_Fintech_Batches(BatchID),
 CHECK(FuelKind IS NULL OR FuelKind IN ('HEIGHT','VOLUME')),
 CHECK(WaterKind IS NULL OR WaterKind IN ('HEIGHT','VOLUME')),
 CHECK(FuelScale IS NULL OR FuelScale>0), CHECK(WaterScale IS NULL OR WaterScale>0),
 CHECK(MaxFuel IS NULL OR MaxFuel>0), CHECK(MaxWater IS NULL OR MaxWater>=0)
);
IF OBJECT_ID(N'dbo.Alson_Fintech_Frames',N'U') IS NULL
CREATE TABLE dbo.Alson_Fintech_Frames (
 FrameHash binary(32) NOT NULL PRIMARY KEY, MasterId nvarchar(64) NOT NULL,
 CommandCode char(2) NOT NULL, RecordID nvarchar(64) NOT NULL,
 SourceAt datetime2(0) NOT NULL, RawFrame nvarchar(max) NOT NULL, RawBytes varbinary(max) NOT NULL,
 BatchID uniqueidentifier NOT NULL, CreatedOn datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
 FOREIGN KEY(BatchID) REFERENCES dbo.Alson_Fintech_Batches(BatchID)
);
IF OBJECT_ID(N'dbo.Alson_Fintech_Rows',N'U') IS NULL
CREATE TABLE dbo.Alson_Fintech_Rows (
 FrameHash binary(32) NOT NULL, EntityType char(3) NOT NULL,
 MappingID int NOT NULL, TargetID int NOT NULL, SiteID int NOT NULL, TargetSnapshot nvarchar(max) NOT NULL,
 PRIMARY KEY(FrameHash,EntityType,MappingID),
 FOREIGN KEY(FrameHash) REFERENCES dbo.Alson_Fintech_Frames(FrameHash),
 CHECK(EntityType IN ('TXN','ATG'))
 -- Deliberately no FK to raw targets; normal archiving may move those rows.
);
IF OBJECT_ID(N'dbo.Alson_Fintech_Imports',N'U') IS NULL
CREATE TABLE dbo.Alson_Fintech_Imports (
 SourceKey binary(32) NOT NULL PRIMARY KEY, BlobName nvarchar(1024) NOT NULL,
 SourceETag nvarchar(128) NULL, EnvelopeHash binary(32) NOT NULL,
 Envelope varbinary(max) NOT NULL, FrameHash binary(32) NULL,
 Outcome varchar(32) NOT NULL, ErrorText nvarchar(4000) NULL,
 BatchID uniqueidentifier NOT NULL, UpdatedOn datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
 FOREIGN KEY(BatchID) REFERENCES dbo.Alson_Fintech_Batches(BatchID)
);
COMMIT TRANSACTION;
