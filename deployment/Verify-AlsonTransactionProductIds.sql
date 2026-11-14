-- Read-only verification for the two Alson sites. Expected: zero mismatches.
USE FintechStagDB_NewArchitecture;
SET NOCOUNT ON;
SELECT r.TargetID,r.MappingID,r.SiteID,t.Grade_ID,t.Article,t.Pump_ID,t.Hose_ID,t.FC_SiteID,r.TargetSnapshot,
 f.SourceAt,f.MasterId
INTO #Tx
FROM dbo.Alson_Fintech_Rows r
JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash AND f.CommandCode='00'
JOIN dbo.Fintech_Transactions t WITH(INDEX(PK_Fintech_Transactions_History),FORCESEEK) ON t.ID=r.TargetID AND t.Site_ID=r.SiteID AND t.CreatedBy='AlsonETL'
WHERE r.EntityType='TXN' AND r.SiteID IN(1191,1192)
OPTION(FORCE ORDER,LOOP JOIN);
SELECT c.*,p.product_id ExpectedGradeID,p.product_name ProductName
INTO #Mapped
FROM #Tx c
LEFT JOIN dbo.Omni_Fintech_PumpMapping m ON m.ID=c.MappingID AND m.Omni_SiteID=c.SiteID AND m.FC_PumpID=c.Pump_ID AND m.FC_NozzleID=c.Hose_ID AND m.FC_SiteID=c.FC_SiteID
 AND m.MasterId COLLATE Latin1_General_100_BIN2=c.MasterId COLLATE Latin1_General_100_BIN2
LEFT JOIN dbo.Omni_Fintech_TankMapping tm ON tm.Omni_SiteID=m.Omni_SiteID AND tm.FC_TankID=m.FC_TankID AND tm.Omni_AssetID=m.Omni_TankID AND tm.FC_SiteID=m.FC_SiteID
LEFT JOIN dbo.Omni_Fintech_Products p ON p.product_id=tm.ProdID_FK AND p.OmniCustomerID_FK=227;
SELECT SiteID,Grade_ID,ExpectedGradeID,ProductName,COUNT(*) Transactions,
 MIN(SourceAt) FirstSourceAt,MAX(SourceAt) LastSourceAt
FROM #Mapped GROUP BY SiteID,Grade_ID,ExpectedGradeID,ProductName ORDER BY SiteID,ExpectedGradeID;
SELECT (SELECT COUNT(*) FROM #Tx) CandidateRows,COUNT(*) MappingRows,
 SUM(CASE WHEN Grade_ID=ExpectedGradeID THEN 0 ELSE 1 END) MismatchedRows,
 SUM(CASE WHEN TRY_CONVERT(int,JSON_VALUE(TargetSnapshot,'$.Grade_ID'))=Grade_ID THEN 0 ELSE 1 END) AuditMismatches
FROM #Mapped;
SELECT BatchID,Kind,CreatedOn,JSON_VALUE(Manifest,'$.state') CorrectionState,
 (SELECT COUNT(*) FROM OPENJSON(b.Manifest,'$.rows')) BackedUpRows
FROM dbo.Alson_Fintech_Batches b WHERE BatchID='dfc9a7b8-7b41-4353-b626-e5c83ad68c71';
