-- User confirmed totalizer -> Final_Volume. Only existing Alson rows at the two new sites.
-- Never updates grades, source frames, other sites or other transaction columns.
SET XACT_ABORT ON;
IF DB_NAME()<>'FintechStagDB_NewArchitecture' THROW 51000,'Wrong database',1;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource='AlsonFintech:Lifecycle',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51001,'Lifecycle lock unavailable',1;
SELECT r.FrameHash,r.MappingID,r.TargetID,r.SiteID,r.TargetSnapshot,
       TRY_CONVERT(float,JSON_VALUE(N'["'+REPLACE(STRING_ESCAPE(f.RawFrame,'json'),N',',N'","')+N'"]','$[9]')) Totalizer
INTO #OwnRows
FROM dbo.Alson_Fintech_Rows r JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash
WHERE r.EntityType='TXN' AND r.SiteID IN(1191,1192) AND f.CommandCode='00'
  AND f.MasterId IN('81C3C8CBD19','81C3C8CE581');
IF (SELECT COUNT(*) FROM #OwnRows)<>100 THROW 51002,'Unexpected test row count; review before backfill',1;
IF EXISTS(SELECT 1 FROM #OwnRows WHERE Totalizer IS NULL OR Totalizer<0)
 THROW 51003,'Invalid totalizer in audit',1;
IF EXISTS(
 SELECT 1 FROM #OwnRows r
 OUTER APPLY(SELECT(SELECT t.* FROM dbo.Fintech_Transactions t WITH(UPDLOCK,HOLDLOCK) WHERE t.ID=r.TargetID AND t.Site_ID=r.SiteID FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) J)x
 WHERE x.J IS NULL OR x.J COLLATE Latin1_General_100_BIN2<>r.TargetSnapshot COLLATE Latin1_General_100_BIN2
) THROW 51004,'Rows changed since audit snapshot; refusing automatic backfill',1;
IF EXISTS(SELECT 1 FROM #OwnRows r JOIN dbo.Fintech_Transactions t ON t.ID=r.TargetID AND t.Site_ID=r.SiteID WHERE t.Final_Volume IS NOT NULL AND t.Final_Volume<>r.Totalizer)
 THROW 51005,'Existing final volume conflicts with source; refusing overwrite',1;
UPDATE t SET Final_Volume=r.Totalizer
FROM dbo.Fintech_Transactions t JOIN #OwnRows r ON r.TargetID=t.ID AND r.SiteID=t.Site_ID
WHERE t.Final_Volume IS NULL;
DECLARE @updated int=@@ROWCOUNT;
UPDATE links SET TargetSnapshot=(SELECT t.* FROM dbo.Fintech_Transactions t WHERE t.ID=links.TargetID AND t.Site_ID=links.SiteID FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)
FROM dbo.Alson_Fintech_Rows links JOIN #OwnRows r ON r.FrameHash=links.FrameHash AND r.MappingID=links.MappingID
WHERE links.EntityType='TXN';
COMMIT TRANSACTION;
SELECT @updated UpdatedRows;
