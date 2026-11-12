param([switch]$Apply)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$out=Join-Path $repo 'artifacts\alson\onboarding-20261112'
$plan=Get-Content (Join-Path $out 'onboarding-plan.json') -Raw|ConvertFrom-Json
if(-not $Apply){$plan|Select-Object Name,SiteID,@{n='Tanks';e={$_.Tanks.Count}},@{n='Channels';e={$_.Channels.Count}}|Format-Table;return}
if(Test-Path (Join-Path $out 'before-update.json')){throw 'Onboarding backup already exists; review before reapplying'}
$settings=Get-Content (Join-Path $repo 'alson.local.json') -Raw|ConvertFrom-Json
$b=[System.Data.SqlClient.SqlConnectionStringBuilder]::new($settings.Values.AlsonSqlConnectionString)
if($b.InitialCatalog -ne 'FintechStagDB_NewArchitecture'){throw 'Staging only'}
$conn=[System.Data.SqlClient.SqlConnection]::new($b.ConnectionString);$conn.Open()
$tx=$conn.BeginTransaction([System.Data.IsolationLevel]::Serializable)
$manifestPath=Join-Path $repo 'artifacts\alson\setup\51662362-1009-453e-a08a-4e5bc183b38e.json'
$localBefore=Get-Content $manifestPath -Raw;$localChanged=$false
function Scalar([string]$text,[hashtable]$values=@{}){
 $cmd=$conn.CreateCommand();$cmd.Transaction=$tx;$cmd.CommandTimeout=90;$cmd.CommandText=$text
 foreach($name in $values.Keys){[void]$cmd.Parameters.AddWithValue('@'+$name,$(if($null -eq $values[$name]){[DBNull]::Value}else{$values[$name]}))}
 try{return $cmd.ExecuteScalar()}finally{$cmd.Dispose()}
}
try{
 if((Scalar 'SELECT DB_NAME()') -ne $b.InitialCatalog){throw 'Wrong database'}
 $lock=Scalar "DECLARE @r int;EXEC @r=sys.sp_getapplock @Resource='AlsonFintech:Lifecycle',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;SELECT @r"
 if($lock -lt 0){throw 'Alson lifecycle busy'}
 $batch=[guid]'51662362-1009-453e-a08a-4e5bc183b38e'
 $originalManifest=[string](Scalar 'SELECT Manifest FROM dbo.Alson_Fintech_Batches WHERE BatchID=@batch' @{batch=$batch})
 if((($originalManifest|ConvertFrom-Json)|ConvertTo-Json -Depth 15 -Compress) -cne (($localBefore|ConvertFrom-Json)|ConvertTo-Json -Depth 15 -Compress)){throw 'Rollback manifests differ'}
 $manifest=$originalManifest|ConvertFrom-Json
 foreach($row in $manifest.Objects){
  $current=[string](Scalar "SELECT(SELECT * FROM dbo.[$($row.Table)] WHERE [$($row.Column)]=@id FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)" @{id=[int]$row.ID})
  if($current -cne [string]$row.Snapshot){throw "Configuration changed since snapshot: $($row.Table) $($row.ID)"}
 }
 foreach($kind in @(@{Code='TXN';Table='Fintech_Transactions'},@{Code='ATG';Table='Fintech_TankLevels'})){
  $guard=Scalar "SELECT COUNT(*) FROM dbo.Alson_Fintech_Rows r OUTER APPLY(SELECT(SELECT t.* FROM dbo.[$($kind.Table)] t WHERE t.ID=r.TargetID AND t.Site_ID=r.SiteID FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) J)x WHERE r.SiteID IN(1191,1192) AND r.EntityType=@kind AND (x.J IS NULL OR x.J COLLATE Latin1_General_100_BIN2<>r.TargetSnapshot COLLATE Latin1_General_100_BIN2)" @{kind=$kind.Code}
  if($guard -ne 0){throw "Target rows changed since snapshot: $($kind.Table)"}
 }
 if((Scalar 'SELECT COUNT(*) FROM dbo.Alson_Fintech_Rows WHERE SiteID IN(1191,1192)') -ne 200){throw 'Expected 200 linked test rows'}
 $shared=[string](Scalar 'SELECT(SELECT * FROM dbo.Omni_Fintech_Products WHERE OmniCustomerID_FK=227 ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES)')
 $mapping=[string](Scalar 'SELECT(SELECT * FROM dbo.Alson_Fintech_ProductMap ORDER BY CustomerID,RawCode FOR JSON PATH,INCLUDE_NULL_VALUES)')
 $links=[string](Scalar 'SELECT(SELECT * FROM dbo.Alson_Fintech_Rows WHERE SiteID IN(1191,1192) ORDER BY SiteID,EntityType,TargetID FOR JSON PATH,INCLUDE_NULL_VALUES)')
 [ordered]@{Database=$b.InitialCatalog;OriginalManifest=$originalManifest;OriginalLocalManifest=$localBefore;SharedProducts=$shared;ProductMap=$mapping;RowLinks=$links;Plan=$plan}|ConvertTo-Json -Depth 15|Set-Content (Join-Path $out 'before-update.json') -Encoding UTF8
 [void](Scalar (Get-Content (Join-Path $PSScriptRoot 'Upgrade-AlsonSiteProducts.sql') -Raw))
 $tankMappings=@{}
 foreach($site in $plan){
  foreach($tank in $site.Tanks){
   $record=[string](Scalar "SELECT(SELECT m.ID,m.Omni_AssetID FROM dbo.Alson_Fintech_ProbeConfig q JOIN dbo.Omni_Fintech_TankMapping m ON m.ID=q.TankMappingID WHERE q.MasterId=@master AND q.ProbeCode=@probe AND m.Omni_SiteID=@site FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)" @{master=$site.ATG;probe=$tank.ProbeCode;site=[int]$site.SiteID})
   $m=$record|ConvertFrom-Json;if(-not $m.ID){throw 'Missing probe configuration'}
   $product=Scalar 'SELECT product_id FROM dbo.Omni_Fintech_Products WHERE OmniCustomerID_FK=227 AND product_name=@name' @{name=$tank.Product}
   if($null -eq $product -or $product -is [DBNull]){throw 'Missing canonical product'}
   [void](Scalar 'UPDATE dbo.Omni_Fintech_TankMapping SET FC_TankID=@tank,ProdID_FK=@product,Capacity=@capacity WHERE ID=@id AND Omni_SiteID=@site;SELECT @@ROWCOUNT' @{tank=[int]$tank.Number;product=[int]$product;capacity=[double]$tank.Capacity;id=[int]$m.ID;site=[int]$site.SiteID})
   [void](Scalar 'UPDATE dbo.Alson_Fintech_ProbeConfig SET MaxFuel=@capacity,MaxWater=@capacity WHERE MasterId=@master AND ProbeCode=@probe;SELECT @@ROWCOUNT' @{capacity=[decimal]$tank.Capacity;master=$site.ATG;probe=$tank.ProbeCode})
   $description="Physical tank $($tank.Number); probe $($tank.ProbeCode); fixed payload position $($tank.Number); $($tank.Product); capacity $($tank.Capacity) L; form dimensions $($tank.DimensionsMm) mm; nozzle colour $($tank.NozzleColour)."
   [void](Scalar 'UPDATE dbo.Assets SET Asset_Name=@name,Description=@description WHERE Asset_Id=@asset AND Site_Id_FK=@site;SELECT @@ROWCOUNT' @{name="Tank $($tank.Number)";description=$description;asset=[int]$m.Omni_AssetID;site=[int]$site.SiteID})
   $tankMappings["$($site.SiteID):$($tank.Number)"]=@{Asset=[int]$m.Omni_AssetID;Product=[int]$product;Name=$tank.Product}
  }
  foreach($channel in $site.Channels){
   $record=[string](Scalar 'SELECT(SELECT ID,Omni_AssetPumpID,Omni_AssetNozzleID FROM dbo.Omni_Fintech_PumpMapping WHERE Omni_SiteID=@site AND MasterId=@master AND AlsonPumpCode=@code AND FC_NozzleID=1 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)' @{site=[int]$site.SiteID;master=$site.DSP;code=$channel.Code})
   $m=$record|ConvertFrom-Json;if(-not $m.ID){throw 'Missing channel mapping'}
   $tank=$tankMappings["$($site.SiteID):$($channel.Tank)"]
   if($tank.Name -ine $channel.Product){throw 'Form nozzle product disagrees with assigned tank'}
   [void](Scalar 'UPDATE dbo.Omni_Fintech_PumpMapping SET FC_PumpID=@pump,FC_TankID=@tank,Omni_TankID=@asset WHERE ID=@id AND Omni_SiteID=@site;SELECT @@ROWCOUNT' @{pump=[int]$channel.Number;tank=[int]$channel.Tank;asset=$tank.Asset;id=[int]$m.ID;site=[int]$site.SiteID})
   [void](Scalar 'UPDATE dbo.Assets SET Asset_Name=@name,Description=@description WHERE Asset_Id=@asset AND Site_Id_FK=@site;SELECT @@ROWCOUNT' @{name="Dispenser channel $($channel.Number) ($($channel.Code))";description="Raw channel $($channel.Code); documented dispenser $($channel.Number); physical dispenser-unit grouping not specified.";asset=[int]$m.Omni_AssetPumpID;site=[int]$site.SiteID})
   [void](Scalar 'UPDATE dbo.Assets SET Asset_Name=@name,Description=@description WHERE Asset_Id=@asset AND Site_Id_FK=@site;SELECT @@ROWCOUNT' @{name="Dispenser $($channel.Number) nozzle 1";description="$($channel.Product); linked physical tank $($channel.Tank) per onboarding form.";asset=[int]$m.Omni_AssetNozzleID;site=[int]$site.SiteID})
  }
 }
 [void](Scalar @'
UPDATE t SET Grade_ID=pm.AlsonGradeID,Pump_ID=p.FC_PumpID
FROM dbo.Fintech_Transactions t JOIN dbo.Alson_Fintech_Rows r ON r.TargetID=t.ID AND r.SiteID=t.Site_ID AND r.EntityType='TXN'
JOIN dbo.Alson_Fintech_Frames f ON f.FrameHash=r.FrameHash JOIN dbo.Omni_Fintech_PumpMapping p ON p.ID=r.MappingID
JOIN dbo.Alson_Fintech_ProductMap pm ON pm.CustomerID=227 AND pm.OmniSiteID=t.Site_ID
 AND pm.RawCode=JSON_VALUE(N'["'+REPLACE(STRING_ESCAPE(f.RawFrame,'json'),N',',N'","')+N'"]','$[5]') COLLATE Latin1_General_100_BIN2
WHERE t.Site_ID IN(1191,1192);
IF @@ROWCOUNT<>100 THROW 51010,'Unexpected sales backfill count',1;
UPDATE t SET Tank_ID=m.FC_TankID,Product_Number=pm.AlsonGradeID,Product_ID=p.product_name
FROM dbo.Fintech_TankLevels t JOIN dbo.Alson_Fintech_Rows r ON r.TargetID=t.ID AND r.SiteID=t.Site_ID AND r.EntityType='ATG'
JOIN dbo.Omni_Fintech_TankMapping m ON m.ID=r.MappingID
JOIN dbo.Omni_Fintech_Products p ON p.product_id=m.ProdID_FK AND p.OmniCustomerID_FK=227
JOIN dbo.Alson_Fintech_ProductMap pm ON pm.ProductRowID=p.Id AND pm.CustomerID=227 AND pm.OmniSiteID=t.Site_ID
WHERE t.Site_ID IN(1191,1192);
IF @@ROWCOUNT<>100 THROW 51011,'Unexpected ATG backfill count',1;
UPDATE r SET TargetSnapshot=(SELECT t.* FROM dbo.Fintech_Transactions t WHERE t.ID=r.TargetID AND t.Site_ID=r.SiteID FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)
FROM dbo.Alson_Fintech_Rows r WHERE r.SiteID IN(1191,1192) AND r.EntityType='TXN';
UPDATE r SET TargetSnapshot=(SELECT t.* FROM dbo.Fintech_TankLevels t WHERE t.ID=r.TargetID AND t.Site_ID=r.SiteID FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)
FROM dbo.Alson_Fintech_Rows r WHERE r.SiteID IN(1191,1192) AND r.EntityType='ATG';SELECT 1;
'@)
 if([string](Scalar 'SELECT(SELECT * FROM dbo.Omni_Fintech_Products WHERE OmniCustomerID_FK=227 ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES)') -cne $shared){throw 'Shared product definitions changed'}
 foreach($row in $manifest.Objects){
  $row.Snapshot=[string](Scalar "SELECT(SELECT * FROM dbo.[$($row.Table)] WHERE [$($row.Column)]=@id FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)" @{id=[int]$row.ID})
 }
 $manifest.CustomSnapshots.Alson_Fintech_ProductMap=[string](Scalar 'SELECT(SELECT * FROM dbo.Alson_Fintech_ProductMap WHERE BatchID=@batch ORDER BY CustomerID,OmniSiteID,RawCode FOR JSON PATH,INCLUDE_NULL_VALUES)' @{batch=$batch})
 $manifest.CustomSnapshots.Alson_Fintech_ProbeConfig=[string](Scalar 'SELECT(SELECT * FROM dbo.Alson_Fintech_ProbeConfig WHERE BatchID=@batch ORDER BY MasterId,ProbeCode FOR JSON PATH,INCLUDE_NULL_VALUES)' @{batch=$batch})
 $manifestJson=$manifest|ConvertTo-Json -Depth 15
 [void](Scalar 'UPDATE dbo.Alson_Fintech_Batches SET Manifest=@manifest WHERE BatchID=@batch;SELECT 1' @{manifest=$manifestJson;batch=$batch})
 Set-Content $manifestPath $manifestJson -Encoding UTF8;$localChanged=$true
 $tx.Commit()
 [ordered]@{Sites=@(1191,1192);TanksConfigured=8;ChannelsLinked=24;SalesUpdated=100;TankReadingsUpdated=100;SharedProductsUnchanged=$true;CompletedOnUtc=[datetime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $out 'result.json') -Encoding UTF8
 Write-Output 'Configured 8 tanks, 24 nozzle/tank links and site-specific grades; corrected 100 sales and 100 ATG rows. Shared products unchanged.'
}catch{try{$tx.Rollback()}catch{};if($localChanged){Set-Content $manifestPath $localBefore -Encoding UTF8};throw}finally{$tx.Dispose();$conn.Dispose()}
