-- Alson-only mapping migration: sites can use different vendor product numbers.
SET XACT_ABORT ON;
IF DB_NAME()<>'FintechStagDB_NewArchitecture' THROW 51000,'Staging only',1;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.Alson_Fintech_ProductMap','OmniSiteID') IS NULL
BEGIN
 IF (SELECT COUNT(*) FROM dbo.Alson_Fintech_ProductMap)<>3 OR
    EXISTS(SELECT 1 FROM dbo.Alson_Fintech_ProductMap WHERE CustomerID<>227)
  THROW 51001,'Legacy product map has unexpected owners; manual migration required',1;
 ALTER TABLE dbo.Alson_Fintech_ProductMap ADD OmniSiteID int NULL;
 EXEC(N'UPDATE dbo.Alson_Fintech_ProductMap SET OmniSiteID=1192 WHERE CustomerID=227;
 ALTER TABLE dbo.Alson_Fintech_ProductMap ALTER COLUMN OmniSiteID int NOT NULL;');
 IF EXISTS(SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id=OBJECT_ID('dbo.Alson_Fintech_ProductMap'))
  THROW 51002,'Product-map key has external references; manual migration required',1;
 IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Alson_Fintech_ProductMap') AND name='UX_Alson_ProductMap_CustomerGrade')
  DROP INDEX UX_Alson_ProductMap_CustomerGrade ON dbo.Alson_Fintech_ProductMap;
 DECLARE @pk sysname=(SELECT name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.Alson_Fintech_ProductMap') AND type='PK');
 DECLARE @drop nvarchar(max)=N'ALTER TABLE dbo.Alson_Fintech_ProductMap DROP CONSTRAINT '+QUOTENAME(@pk);
 IF @pk IS NOT NULL EXEC sys.sp_executesql @drop;
 EXEC(N'ALTER TABLE dbo.Alson_Fintech_ProductMap ADD CONSTRAINT PK_Alson_ProductMap_Site PRIMARY KEY(CustomerID,OmniSiteID,RawCode);
 INSERT dbo.Alson_Fintech_ProductMap(CustomerID,OmniSiteID,RawCode,ProductRowID,AlsonGradeID,BatchID)
 SELECT CustomerID,1191,RawCode,ProductRowID,AlsonGradeID,BatchID FROM dbo.Alson_Fintech_ProductMap WHERE CustomerID=227 AND OmniSiteID=1192;');
END;
EXEC(N'
IF (SELECT COUNT(*) FROM dbo.Alson_Fintech_ProductMap WHERE CustomerID=227 AND OmniSiteID IN(1191,1192))<>6
 THROW 51003,''Expected six site/product mappings'',1;
UPDATE dbo.Alson_Fintech_ProductMap SET AlsonGradeID=NULL WHERE CustomerID=227 AND OmniSiteID IN(1191,1192);
UPDATE dbo.Alson_Fintech_ProductMap SET AlsonGradeID=CASE
 WHEN RawCode=''H'' THEN 1
 WHEN OmniSiteID=1191 AND RawCode=''D'' THEN 2
 WHEN OmniSiteID=1191 AND RawCode=''R'' THEN 3
 WHEN OmniSiteID=1192 AND RawCode=''R'' THEN 2
 WHEN OmniSiteID=1192 AND RawCode=''D'' THEN 3 END
WHERE CustomerID=227 AND OmniSiteID IN(1191,1192);');
IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.Alson_Fintech_ProductMap') AND name='FK_Alson_ProductMap_Site')
 EXEC(N'ALTER TABLE dbo.Alson_Fintech_ProductMap ADD CONSTRAINT FK_Alson_ProductMap_Site FOREIGN KEY(OmniSiteID) REFERENCES dbo.Plant_Site(Site_Id)');
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Alson_Fintech_ProductMap') AND name='UX_Alson_ProductMap_SiteGrade')
 EXEC(N'CREATE UNIQUE INDEX UX_Alson_ProductMap_SiteGrade ON dbo.Alson_Fintech_ProductMap(CustomerID,OmniSiteID,AlsonGradeID) WHERE AlsonGradeID IS NOT NULL');
COMMIT TRANSACTION;
