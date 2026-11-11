-- Only the Alson-owned product mapping schema/data. Never alters shared product definitions.
SET XACT_ABORT ON;
IF DB_NAME()<>'FintechStagDB_NewArchitecture' THROW 51000,'Staging only',1;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource='AlsonFintech:Lifecycle',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51001,'Alson configuration busy',1;
IF COL_LENGTH(N'dbo.Alson_Fintech_ProductMap',N'AlsonGradeID') IS NULL
 ALTER TABLE dbo.Alson_Fintech_ProductMap ADD AlsonGradeID int NULL;
EXEC sys.sp_executesql N'
IF (SELECT COUNT(*) FROM dbo.Alson_Fintech_ProductMap WHERE CustomerID=227 AND RawCode IN(''H'',''R'',''D''))<>3
 THROW 51002,''Expected three customer227 Alson mappings'',1;
IF EXISTS(SELECT 1 FROM dbo.Alson_Fintech_ProductMap m
 LEFT JOIN dbo.Omni_Fintech_Products p ON p.Id=m.ProductRowID AND p.OmniCustomerID_FK=m.CustomerID
 WHERE m.CustomerID=227 AND m.RawCode IN(''H'',''R'',''D'')
 AND (p.Id IS NULL OR p.product_name<>CASE m.RawCode WHEN ''H'' THEN ''Excellium'' WHEN ''R'' THEN ''Hi Super'' WHEN ''D'' THEN ''Diesel'' END))
 THROW 51003,''Existing linked product name is incorrect; review first'',1;
UPDATE dbo.Alson_Fintech_ProductMap SET AlsonGradeID=CASE RawCode WHEN ''H'' THEN 1 WHEN ''R'' THEN 2 WHEN ''D'' THEN 3 END
WHERE CustomerID=227 AND RawCode IN(''H'',''R'',''D'');';
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Alson_Fintech_ProductMap') AND name=N'UX_Alson_ProductMap_CustomerGrade')
 EXEC(N'CREATE UNIQUE INDEX UX_Alson_ProductMap_CustomerGrade ON dbo.Alson_Fintech_ProductMap(CustomerID,AlsonGradeID) WHERE AlsonGradeID IS NOT NULL');
COMMIT TRANSACTION;
