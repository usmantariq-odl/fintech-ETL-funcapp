-- Read only. Expected: eight tank groups, zero mismatches, one applied correction batch.
USE FintechStagDB_NewArchitecture;
SELECT t.Site_ID,t.Tank_ID,t.Product_ID,t.Product_Number,m.ProdID_FK ExpectedProductNumber,COUNT_BIG(*) ReadingRows,
 SUM(CONVERT(bigint,CASE WHEN t.Product_Number=m.ProdID_FK THEN 0 ELSE 1 END)) MismatchedRows
FROM dbo.Fintech_TankLevels t
JOIN dbo.Omni_Fintech_TankMapping m ON m.Omni_SiteID=t.Site_ID AND m.FC_TankID=t.Tank_ID AND m.Omni_AssetID=t.Omni_AssetID
WHERE t.Site_ID IN(1191,1192) AND t.CreatedBy='AlsonETL'
GROUP BY t.Site_ID,t.Tank_ID,t.Product_ID,t.Product_Number,m.ProdID_FK
ORDER BY t.Site_ID,t.Tank_ID;
SELECT BatchID,Kind,CreatedOn,JSON_VALUE(Manifest,'$.state') CorrectionState,
 (SELECT COUNT(*) FROM OPENJSON(b.Manifest,'$.rows')) BackedUpRows
FROM dbo.Alson_Fintech_Batches b WHERE BatchID='615b3a88-fb78-43a6-b171-476564c83884';
