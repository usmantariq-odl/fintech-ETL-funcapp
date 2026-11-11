param([switch]$Apply)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(-not $Apply){Write-Output 'Preview: add AlsonGradeID to the Alson map, set H=1/R=2/D=3, update only the 100 linked new-site transactions. Shared products unchanged.';return}
$settings=Get-Content (Join-Path $repo 'alson.local.json') -Raw|ConvertFrom-Json
$builder=[System.Data.SqlClient.SqlConnectionStringBuilder]::new($settings.Values.AlsonSqlConnectionString)
if($builder.InitialCatalog -ne 'FintechStagDB_NewArchitecture'){throw 'Staging only'}
$out=Join-Path $repo 'artifacts\alson\grades-update'
New-Item -ItemType Directory -Path $out -Force|Out-Null
if(Test-Path (Join-Path $out 'before.json')){throw 'Grade-update backup already exists; review before running again.'}
$manifestPath=Join-Path $repo 'artifacts\alson\setup\51662362-1009-453e-a08a-4e5bc183b38e.json'
$localBefore=Get-Content $manifestPath -Raw
$conn=[System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString);$conn.Open()
$tx=$conn.BeginTransaction([System.Data.IsolationLevel]::Serializable)
function Scalar([string]$text,[hashtable]$values=@{}){
 $cmd=$conn.CreateCommand();$cmd.Transaction=$tx;$cmd.CommandTimeout=90;$cmd.CommandText=$text
 foreach($name in $values.Keys){[void]$cmd.Parameters.AddWithValue('@'+$name,$values[$name])}
 try{return $cmd.ExecuteScalar()}finally{$cmd.Dispose()}
}
$localChanged=$false
try{
 if((Scalar 'SELECT DB_NAME()') -ne $builder.InitialCatalog){throw 'Wrong database'}
 $lock=Scalar "DECLARE @r int;EXEC @r=sys.sp_getapplock @Resource='AlsonFintech:Lifecycle',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;SELECT @r"
 if($lock -lt 0){throw 'Alson lifecycle busy'}
 $batch=[guid]'51662362-1009-453e-a08a-4e5bc183b38e'
 $originalManifest=[string](Scalar 'SELECT Manifest FROM dbo.Alson_Fintech_Batches WHERE BatchID=@batch' @{batch=$batch})
 if((($originalManifest|ConvertFrom-Json)|ConvertTo-Json -Depth 15 -Compress) -cne (($localBefore|ConvertFrom-Json)|ConvertTo-Json -Depth 15 -Compress)){throw 'Local/SQL rollback manifest mismatch'}
 $shared=[string](Scalar "SELECT(SELECT * FROM dbo.Omni_Fintech_Products WHERE OmniCustomerID_FK=227 ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES)")
 $mapping=[string](Scalar "SELECT(SELECT * FROM dbo.Alson_Fintech_ProductMap WHERE CustomerID=227 ORDER BY RawCode FOR JSON PATH,INCLUDE_NULL_VALUES)")
 $rows=[string](Scalar "SELECT(SELECT t.ID,t.Site_ID,t.Grade_ID,t.Article,r.TargetSnapshot FROM dbo.Fintech_Transactions t JOIN dbo.Alson_Fintech_Rows r ON r.TargetID=t.ID AND r.SiteID=t.Site_ID AND r.EntityType='TXN' WHERE t.Site_ID IN(1191,1192) ORDER BY t.ID FOR JSON PATH,INCLUDE_NULL_VALUES)")
 $hadColumn=[bool](Scalar "SELECT CASE WHEN COL_LENGTH('dbo.Alson_Fintech_ProductMap','AlsonGradeID') IS NULL THEN 0 ELSE 1 END")
 [ordered]@{Database=$builder.InitialCatalog;HadAlsonGradeID=$hadColumn;SharedProducts=$shared;ProductMap=$mapping;Transactions=$rows;ConfigurationManifest=$originalManifest;LocalConfigurationManifest=$localBefore}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $out 'before.json') -Encoding UTF8
 [void](Scalar (Get-Content (Join-Path $PSScriptRoot 'Configure-AlsonGradeIds.sql') -Raw))
 $updated=Scalar (Get-Content (Join-Path $PSScriptRoot 'Backfill-AlsonGradeIds.sql') -Raw)
 $sharedAfter=[string](Scalar "SELECT(SELECT * FROM dbo.Omni_Fintech_Products WHERE OmniCustomerID_FK=227 ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES)")
 if($sharedAfter -cne $shared){throw 'Shared product definitions changed'}
 $newSnapshot=[string](Scalar "SELECT(SELECT * FROM dbo.Alson_Fintech_ProductMap WHERE BatchID=@batch ORDER BY CustomerID,RawCode FOR JSON PATH,INCLUDE_NULL_VALUES)" @{batch=$batch})
 $manifest=$originalManifest|ConvertFrom-Json
 $manifest.CustomSnapshots.Alson_Fintech_ProductMap=$newSnapshot
 $manifestJson=$manifest|ConvertTo-Json -Depth 15
 [void](Scalar 'UPDATE dbo.Alson_Fintech_Batches SET Manifest=@manifest WHERE BatchID=@batch;SELECT 1' @{manifest=$manifestJson;batch=$batch})
 Set-Content $manifestPath $manifestJson -Encoding UTF8;$localChanged=$true
 $tx.Commit()
 [ordered]@{UpdatedTransactions=[int]$updated;SharedProductsUnchanged=$true;ChangedSites=@(1191,1192);AlsonGrades=@{H=1;R=2;D=3};ConfigurationManifestRefreshed=$true;CompletedOnUtc=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $out 'result.json') -Encoding UTF8
 Write-Output "Configured Alson H=1/R=2/D=3; updated $updated transactions. Shared products unchanged. Rollback snapshots refreshed."
}catch{
 try{$tx.Rollback()}catch{}
 if($localChanged){Set-Content $manifestPath $localBefore -Encoding UTF8}
 throw
}finally{$tx.Dispose();$conn.Dispose()}
