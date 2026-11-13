-- Default: preview only. Set @Action='APPLY' and a new @BatchID to correct.
-- Rollback: @Action='ROLLBACK' with the SAME correction @BatchID.
-- Only Alson-owned ATG rows for customer 227 / sites 1191,1192 are eligible.
-- Backup is retained in Alson_Fintech_Batches.Manifest; no target schema changes.
DECLARE @Action varchar(10)='PREVIEW';
DECLARE @BatchID uniqueidentifier='615b3a88-fb78-43a6-b171-476564c83884';
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME()<>'FintechStagDB_NewArchitecture' THROW 51000,'Staging database required',1;
IF @Action NOT IN('PREVIEW','APPLY','ROLLBACK') THROW 51000,'Unknown action',1;
BEGIN TRY
 BEGIN TRANSACTION;
 DECLARE @lock int;
 EXEC @lock=sys.sp_getapplock @Resource='AlsonFintech:Lifecycle',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=60000;
 IF @lock<0 THROW 51001,'Could not acquire Alson lifecycle lock',1;
 IF @Action='ROLLBACK'
 BEGIN
  DECLARE @manifest nvarchar(max);
  SELECT @manifest=Manifest FROM dbo.Alson_Fintech_Batches WITH(UPDLOCK,HOLDLOCK)
  WHERE BatchID=@BatchID AND Kind='tank-product-fix';
  IF @manifest IS NULL OR JSON_VALUE(@manifest,'$.state')<>'applied'
   THROW 51002,'Applied correction batch not found',1;
  SELECT * INTO #Restore FROM OPENJSON(@manifest,'$.rows') WITH
   (TargetID int,SiteID int,OldProductNumber int,NewProductNumber int);
  IF EXISTS(SELECT 1 FROM #Restore WHERE SiteID NOT IN(1191,1192)) THROW 51003,'Unexpected backup site',1;
  IF EXISTS(SELECT 1 FROM #Restore b
   LEFT JOIN dbo.Fintech_TankLevels t WITH(UPDLOCK,HOLDLOCK) ON t.ID=b.TargetID AND t.Site_ID=b.SiteID AND t.CreatedBy='AlsonETL'
   LEFT JOIN dbo.Alson_Fintech_Rows r WITH(UPDLOCK,HOLDLOCK) ON r.EntityType='ATG' AND r.TargetID=b.TargetID AND r.SiteID=b.SiteID
   WHERE t.ID IS NULL OR r.TargetID IS NULL OR t.Product_Number IS NULL OR t.Product_Number<>b.NewProductNumber
    OR TRY_CONVERT(int,JSON_VALUE(r.TargetSnapshot,'$.Product_Number'))<>b.NewProductNumber
    OR JSON_VALUE(r.TargetSnapshot,'$.Product_Number') IS NULL)
   THROW 51004,'A corrected row changed or disappeared; rollback refused',1;
  UPDATE t SET Product_Number=b.OldProductNumber FROM #Restore b JOIN dbo.Fintech_TankLevels t WITH(INDEX(PK_Fintech_TankLevels_History)) ON t.ID=b.TargetID AND t.Site_ID=b.SiteID OPTION(FORCE ORDER,LOOP JOIN);
  DECLARE @restored int=@@ROWCOUNT;
  UPDATE r SET TargetSnapshot=JSON_MODIFY(r.TargetSnapshot,'strict $.Product_Number',b.OldProductNumber)
   FROM dbo.Alson_Fintech_Rows r JOIN #Restore b ON r.TargetID=b.TargetID AND r.SiteID=b.SiteID WHERE r.EntityType='ATG';
  UPDATE dbo.Alson_Fintech_Batches SET Manifest=JSON_MODIFY(JSON_MODIFY(Manifest,'$.state','reverted'),'$.revertedAtUtc',CONVERT(varchar(33),SYSUTCDATETIME(),126)) WHERE BatchID=@BatchID;
  COMMIT;
  SELECT @BatchID BatchID,@restored RestoredRows;
  RETURN;
 END;
 IF EXISTS(SELECT 1 FROM dbo.Alson_Fintech_Batches WHERE BatchID=@BatchID) THROW 51005,'BatchID already used; select a new correction batch',1;
 SELECT t.ID TargetID,t.Site_ID SiteID,t.Product_Number OldProductNumber,p.product_id NewProductNumber,
        r.MappingID,r.TargetSnapshot
 INTO #Owned
 FROM dbo.Alson_Fintech_Rows r WITH(UPDLOCK,HOLDLOCK)
 JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash AND f.CommandCode='20'
 JOIN dbo.Plant_Site s ON s.Site_Id=r.SiteID AND s.Customer_Id_FK=227 AND s.DeviceTypeId=3
 JOIN dbo.Fintech_TankLevels t WITH(UPDLOCK,HOLDLOCK,INDEX(PK_Fintech_TankLevels_History),FORCESEEK) ON t.ID=r.TargetID AND t.Site_ID=r.SiteID AND t.CreatedBy='AlsonETL'
 JOIN dbo.Omni_Fintech_TankMapping m ON m.ID=r.MappingID AND m.Omni_SiteID=t.Site_ID AND m.FC_TankID=t.Tank_ID AND m.Omni_AssetID=t.Omni_AssetID AND m.MasterId COLLATE Latin1_General_100_BIN2=f.MasterId COLLATE Latin1_General_100_BIN2
 JOIN dbo.Alson_Fintech_ProbeConfig q ON q.TankMappingID=m.ID AND q.MasterId COLLATE Latin1_General_100_BIN2=f.MasterId COLLATE Latin1_General_100_BIN2
 JOIN dbo.Omni_Fintech_Products p ON p.OmniCustomerID_FK=s.Customer_Id_FK AND p.product_id=m.ProdID_FK
 WHERE r.EntityType='ATG' AND r.SiteID IN(1191,1192)
 AND ((r.SiteID=1191 AND f.MasterId='81C3C8CD7C3') OR (r.SiteID=1192 AND f.MasterId='81C3C8CD824'))
 AND (t.Product_Number IS NULL OR t.Product_Number<>p.product_id) OPTION(FORCE ORDER,LOOP JOIN);
 CREATE UNIQUE CLUSTERED INDEX IX_Owned ON #Owned(TargetID);
 -- Refuse to overwrite manually edited values or unresolved/null previous product IDs.
 IF EXISTS(SELECT 1 FROM #Owned WHERE OldProductNumber IS NULL OR NewProductNumber<=0
  OR TRY_CONVERT(int,JSON_VALUE(TargetSnapshot,'$.Product_Number'))<>OldProductNumber
  OR JSON_VALUE(TargetSnapshot,'$.Product_Number') IS NULL)
  THROW 51006,'Stored product differs from its Alson audit or is unresolved',1;
 DECLARE @backup nvarchar(max)=(SELECT TargetID,SiteID,OldProductNumber,NewProductNumber FROM #Owned ORDER BY TargetID FOR JSON PATH,INCLUDE_NULL_VALUES);
 INSERT dbo.Alson_Fintech_Batches(BatchID,Kind,Manifest)
 VALUES(@BatchID,'tank-product-fix',JSON_MODIFY(N'{"state":"applied"}','$.rows',JSON_QUERY(@backup)));
 UPDATE t SET Product_Number=b.NewProductNumber
 FROM #Owned b JOIN dbo.Fintech_TankLevels t WITH(INDEX(PK_Fintech_TankLevels_History)) ON t.ID=b.TargetID AND t.Site_ID=b.SiteID OPTION(FORCE ORDER,LOOP JOIN);
 DECLARE @updated int=@@ROWCOUNT;
 UPDATE r SET TargetSnapshot=JSON_MODIFY(r.TargetSnapshot,'strict $.Product_Number',b.NewProductNumber)
 FROM dbo.Alson_Fintech_Rows r JOIN #Owned b ON r.TargetID=b.TargetID AND r.SiteID=b.SiteID WHERE r.EntityType='ATG';
 IF @@ROWCOUNT<>@updated THROW 51007,'Audit update count differs from corrected rows',1;
 IF EXISTS(SELECT 1 FROM #Owned b JOIN dbo.Fintech_TankLevels t ON t.ID=b.TargetID
  WHERE t.Product_Number<>b.NewProductNumber) THROW 51008,'Correction verification failed',1;
 SELECT SiteID,OldProductNumber,NewProductNumber,COUNT(*) CorrectedRows FROM #Owned GROUP BY SiteID,OldProductNumber,NewProductNumber ORDER BY SiteID,OldProductNumber;
 IF @Action='PREVIEW' ROLLBACK TRANSACTION; ELSE COMMIT TRANSACTION;
 SELECT @Action Action,@BatchID BatchID,@updated CorrectedRows,CASE WHEN @Action='PREVIEW' THEN 'Rolled back preview' ELSE 'Committed with rollback backup' END Result;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;


