-- Ready-to-run rollback for transaction correction batch dfc9a7b8-7b41-4353-b626-e5c83ad68c71.
-- Only Alson-owned TXN rows for customer 227 / sites 1191,1192 are eligible.
-- Backup is retained in Alson_Fintech_Batches.Manifest; no target schema changes.
DECLARE @Action varchar(10)='ROLLBACK';
DECLARE @BatchID uniqueidentifier='dfc9a7b8-7b41-4353-b626-e5c83ad68c71';
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
  WHERE BatchID=@BatchID AND Kind='txn-product-fix';
  IF @manifest IS NULL OR JSON_VALUE(@manifest,'$.state')<>'applied'
   THROW 51002,'Applied correction batch not found',1;
  SELECT * INTO #Restore FROM OPENJSON(@manifest,'$.rows') WITH
   (TargetID int,SiteID int,OldGradeID int,NewGradeID int);
  IF EXISTS(SELECT 1 FROM #Restore WHERE SiteID NOT IN(1191,1192)) THROW 51003,'Unexpected backup site',1;
  IF EXISTS(SELECT 1 FROM #Restore b
   LEFT JOIN dbo.Fintech_Transactions t WITH(UPDLOCK,HOLDLOCK) ON t.ID=b.TargetID AND t.Site_ID=b.SiteID AND t.CreatedBy='AlsonETL'
   LEFT JOIN dbo.Alson_Fintech_Rows r WITH(UPDLOCK,HOLDLOCK) ON r.EntityType='TXN' AND r.TargetID=b.TargetID AND r.SiteID=b.SiteID
   WHERE t.ID IS NULL OR r.TargetID IS NULL OR t.Grade_ID IS NULL OR t.Grade_ID<>b.NewGradeID
    OR TRY_CONVERT(int,JSON_VALUE(r.TargetSnapshot,'$.Grade_ID'))<>b.NewGradeID
    OR JSON_VALUE(r.TargetSnapshot,'$.Grade_ID') IS NULL)
   THROW 51004,'A corrected row changed or disappeared; rollback refused',1;
  UPDATE t SET Grade_ID=b.OldGradeID FROM #Restore b JOIN dbo.Fintech_Transactions t WITH(INDEX(PK_Fintech_Transactions_History)) ON t.ID=b.TargetID AND t.Site_ID=b.SiteID OPTION(FORCE ORDER,LOOP JOIN);
  DECLARE @restored int=@@ROWCOUNT;
  UPDATE r SET TargetSnapshot=JSON_MODIFY(r.TargetSnapshot,'strict $.Grade_ID',b.OldGradeID)
   FROM dbo.Alson_Fintech_Rows r JOIN #Restore b ON r.TargetID=b.TargetID AND r.SiteID=b.SiteID WHERE r.EntityType='TXN';
  UPDATE dbo.Alson_Fintech_Batches SET Manifest=JSON_MODIFY(JSON_MODIFY(Manifest,'$.state','reverted'),'$.revertedAtUtc',CONVERT(varchar(33),SYSUTCDATETIME(),126)) WHERE BatchID=@BatchID;
  COMMIT;
  SELECT @BatchID BatchID,@restored RestoredRows;
  RETURN;
 END;
 IF EXISTS(SELECT 1 FROM dbo.Alson_Fintech_Batches WHERE BatchID=@BatchID) THROW 51005,'BatchID already used; select a new correction batch',1;
 SELECT r.FrameHash,r.MappingID,r.TargetID,r.SiteID,r.TargetSnapshot,t.Grade_ID OldGradeID,
        t.Pump_ID,t.Hose_ID,t.FC_SiteID,t.Article,f.MasterId,f.RawFrame
 INTO #Candidates
 FROM dbo.Alson_Fintech_Rows r WITH(UPDLOCK,HOLDLOCK)
 JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash AND f.CommandCode='00'
 JOIN dbo.Fintech_Transactions t WITH(UPDLOCK,HOLDLOCK,INDEX(PK_Fintech_Transactions_History),FORCESEEK) ON t.ID=r.TargetID AND t.Site_ID=r.SiteID AND t.CreatedBy='AlsonETL'
 JOIN dbo.Plant_Site s ON s.Site_Id=r.SiteID AND s.Customer_Id_FK=227 AND s.DeviceTypeId=3
 WHERE r.EntityType='TXN' AND r.SiteID IN(1191,1192)
 AND ((r.SiteID=1191 AND f.MasterId='81C3C8CBD19') OR (r.SiteID=1192 AND f.MasterId='81C3C8CE581'))
 OPTION(FORCE ORDER,LOOP JOIN);
 CREATE UNIQUE CLUSTERED INDEX IX_Candidates ON #Candidates(TargetID);
 SELECT c.TargetID,c.SiteID,c.OldGradeID,p.product_id NewGradeID,c.MappingID,c.TargetSnapshot,pm.AlsonGradeID NativeGradeID
 INTO #Resolved
 FROM #Candidates c
 JOIN dbo.Omni_Fintech_PumpMapping m ON m.ID=c.MappingID AND m.Omni_SiteID=c.SiteID AND m.FC_PumpID=c.Pump_ID AND m.FC_NozzleID=c.Hose_ID AND m.FC_SiteID=c.FC_SiteID
  AND m.MasterId COLLATE Latin1_General_100_BIN2=c.MasterId COLLATE Latin1_General_100_BIN2
 JOIN dbo.Omni_Fintech_TankMapping tm ON tm.Omni_SiteID=m.Omni_SiteID AND tm.Omni_AssetID=m.Omni_TankID AND tm.FC_TankID=m.FC_TankID AND tm.FC_SiteID=m.FC_SiteID
 JOIN dbo.Omni_Fintech_Products p ON p.OmniCustomerID_FK=227 AND p.product_id=tm.ProdID_FK
 JOIN dbo.Alson_Fintech_ProductMap pm ON pm.CustomerID=227 AND pm.OmniSiteID=c.SiteID AND pm.ProductRowID=p.Id
  AND pm.RawCode COLLATE Latin1_General_100_BIN2=JSON_VALUE(N'["'+REPLACE(STRING_ESCAPE(c.RawFrame,'json'),N',',N'","')+N'"]','$[5]') COLLATE Latin1_General_100_BIN2
 WHERE c.Article COLLATE Latin1_General_100_BIN2=p.product_name COLLATE Latin1_General_100_BIN2;
 CREATE UNIQUE CLUSTERED INDEX IX_Resolved ON #Resolved(TargetID);
 IF (SELECT COUNT(*) FROM #Resolved)<>(SELECT COUNT(*) FROM #Candidates)
  THROW 51009,'A transaction lacks a unique verified pump/tank/product/source-code mapping; nothing changed',1;
 SELECT * INTO #Owned FROM #Resolved WHERE OldGradeID IS NULL OR OldGradeID<>NewGradeID;
 IF EXISTS(SELECT 1 FROM #Owned WHERE OldGradeID<>NativeGradeID)
  THROW 51010,'A transaction has an unexpected previous grade; manual review required',1;
 CREATE UNIQUE CLUSTERED INDEX IX_Owned ON #Owned(TargetID);
 -- Refuse to overwrite manually edited values or unresolved/null previous product IDs.
 IF EXISTS(SELECT 1 FROM #Owned WHERE OldGradeID IS NULL OR NewGradeID<=0
  OR TRY_CONVERT(int,JSON_VALUE(TargetSnapshot,'$.Grade_ID'))<>OldGradeID
  OR JSON_VALUE(TargetSnapshot,'$.Grade_ID') IS NULL)
  THROW 51006,'Stored product differs from its Alson audit or is unresolved',1;
 DECLARE @backup nvarchar(max)=(SELECT TargetID,SiteID,OldGradeID,NewGradeID FROM #Owned ORDER BY TargetID FOR JSON PATH,INCLUDE_NULL_VALUES);
 INSERT dbo.Alson_Fintech_Batches(BatchID,Kind,Manifest)
 VALUES(@BatchID,'txn-product-fix',JSON_MODIFY(N'{"state":"applied"}','$.rows',JSON_QUERY(@backup)));
 UPDATE t SET Grade_ID=b.NewGradeID
 FROM #Owned b JOIN dbo.Fintech_Transactions t WITH(INDEX(PK_Fintech_Transactions_History)) ON t.ID=b.TargetID AND t.Site_ID=b.SiteID OPTION(FORCE ORDER,LOOP JOIN);
 DECLARE @updated int=@@ROWCOUNT;
 UPDATE r SET TargetSnapshot=JSON_MODIFY(r.TargetSnapshot,'strict $.Grade_ID',b.NewGradeID)
 FROM dbo.Alson_Fintech_Rows r JOIN #Owned b ON r.TargetID=b.TargetID AND r.SiteID=b.SiteID WHERE r.EntityType='TXN';
 IF @@ROWCOUNT<>@updated THROW 51007,'Audit update count differs from corrected rows',1;
 IF EXISTS(SELECT 1 FROM #Owned b JOIN dbo.Fintech_Transactions t ON t.ID=b.TargetID
  WHERE t.Grade_ID<>b.NewGradeID) THROW 51008,'Correction verification failed',1;
 SELECT SiteID,OldGradeID,NewGradeID,COUNT(*) CorrectedRows FROM #Owned GROUP BY SiteID,OldGradeID,NewGradeID ORDER BY SiteID,OldGradeID;
 IF @Action='PREVIEW' ROLLBACK TRANSACTION; ELSE COMMIT TRANSACTION;
 SELECT @Action Action,@BatchID BatchID,@updated CorrectedRows,CASE WHEN @Action='PREVIEW' THEN 'Rolled back preview' ELSE 'Committed with rollback backup' END Result;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;




