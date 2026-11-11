-- Source letter determines Alson Grade_ID. Only the two new sites' linked Alson rows.
-- All other transaction columns and every shared product row remain unchanged.
SET XACT_ABORT ON;
IF DB_NAME()<>'FintechStagDB_NewArchitecture' THROW 51000,'Staging only',1;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource='AlsonFintech:Lifecycle',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51001,'Alson busy',1;
SELECT r.FrameHash,r.MappingID,r.TargetID,r.SiteID,r.TargetSnapshot,m.AlsonGradeID
INTO #Owned FROM dbo.Alson_Fintech_Rows r
JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash
JOIN dbo.Plant_Site s ON s.Site_Id=r.SiteID AND s.Customer_Id_FK=227 AND s.DeviceTypeId=3
JOIN dbo.Alson_Fintech_ProductMap m ON m.CustomerID=s.Customer_Id_FK
 AND m.RawCode=JSON_VALUE(N'["'+REPLACE(STRING_ESCAPE(f.RawFrame,'json'),N',',N'","')+N'"]','$[5]') COLLATE Latin1_General_100_BIN2
WHERE r.EntityType='TXN' AND r.SiteID IN(1191,1192) AND f.CommandCode='00'
 AND f.MasterId IN('81C3C8CBD19','81C3C8CE581');
IF (SELECT COUNT(*) FROM #Owned)<>100 OR EXISTS(SELECT 1 FROM #Owned WHERE AlsonGradeID IS NULL)
 THROW 51002,'Expected 100 fully mapped Alson transactions',1;
IF EXISTS(SELECT 1 FROM #Owned r
 OUTER APPLY(SELECT(SELECT t.* FROM dbo.Fintech_Transactions t WITH(UPDLOCK,HOLDLOCK) WHERE t.ID=r.TargetID AND t.Site_ID=r.SiteID FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) J)x
 WHERE x.J IS NULL OR x.J COLLATE Latin1_General_100_BIN2<>r.TargetSnapshot COLLATE Latin1_General_100_BIN2)
 THROW 51003,'Transaction changed since its audit snapshot; refusing automatic backfill',1;
UPDATE t SET Grade_ID=r.AlsonGradeID
FROM dbo.Fintech_Transactions t JOIN #Owned r ON r.TargetID=t.ID AND r.SiteID=t.Site_ID
WHERE t.Grade_ID<>r.AlsonGradeID OR t.Grade_ID IS NULL;
DECLARE @updated int=@@ROWCOUNT;
UPDATE links SET TargetSnapshot=(SELECT t.* FROM dbo.Fintech_Transactions t WHERE t.ID=links.TargetID AND t.Site_ID=links.SiteID FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)
FROM dbo.Alson_Fintech_Rows links JOIN #Owned r ON r.FrameHash=links.FrameHash AND r.MappingID=links.MappingID
WHERE links.EntityType='TXN';
COMMIT TRANSACTION;
SELECT @updated UpdatedRows;
