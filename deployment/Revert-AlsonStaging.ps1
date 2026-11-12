param([Parameter(Mandatory=$true)][string]$ManifestPath,[switch]$Apply,[switch]$RemoveSchema,[switch]$VerifyOnly)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$manifest=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json
if($manifest.Database -ne 'FintechStagDB_NewArchitecture'){throw 'Manifest database mismatch'}
if(-not $Apply -and -not $VerifyOnly){Write-Output "Preview only: would revert batch $($manifest.BatchID), sites $($manifest.Sites.SiteID -join ','). Use -Apply only after stopping the Alson trigger.";return}
$settings=Get-Content (Join-Path $repo 'local.settings.json') -Raw|ConvertFrom-Json
$b=[System.Data.SqlClient.SqlConnectionStringBuilder]::new($settings.Values.ApplicationSettings__AzureSql)
if($b.InitialCatalog -ne $manifest.Database){throw 'Connection database mismatch'}
$conn=[System.Data.SqlClient.SqlConnection]::new($b.ConnectionString);$conn.Open();$tx=$conn.BeginTransaction([System.Data.IsolationLevel]::Serializable)
function Query([string]$text,[hashtable]$values=@{},[switch]$Scalar){
 $cmd=$conn.CreateCommand();$cmd.Transaction=$tx;$cmd.CommandTimeout=90;$cmd.CommandText=$text
 foreach($key in $values.Keys){[void]$cmd.Parameters.AddWithValue('@'+$key,$values[$key])}
 try{if($Scalar){return $cmd.ExecuteScalar()}else{[void]$cmd.ExecuteNonQuery()}}finally{$cmd.Dispose()}
}
try{
 if((Query 'SELECT DB_NAME()' -Scalar) -ne $manifest.Database){throw 'Wrong database'}
 $lock=Query "DECLARE @r int;EXEC @r=sys.sp_getapplock @Resource='AlsonFintech:Lifecycle',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;SELECT @r" -Scalar
 if($lock -lt 0){throw 'Alson is busy; stop it before rollback'}
 $batch=[guid]$manifest.BatchID
 $stored=Query 'SELECT Manifest FROM dbo.Alson_Fintech_Batches WITH(UPDLOCK,HOLDLOCK) WHERE BatchID=@batch AND Kind=''configuration''' @{batch=$batch} -Scalar
 if([string]::IsNullOrWhiteSpace([string]$stored)){throw 'Configuration batch missing'}
 $original=$stored|ConvertFrom-Json
 if(($original|ConvertTo-Json -Depth 15 -Compress) -cne ($manifest|ConvertTo-Json -Depth 15 -Compress)){throw 'Local manifest differs from original SQL manifest'}
 $allowed=@{Plant_Site='Site_Id';Assets='Asset_Id';Omni_Fintech_SiteMapping='ID';Omni_Fintech_AlsonSiteMaster='ID';Omni_Fintech_TankMapping='ID';Omni_Fintech_PumpMapping='ID';OPC_SourceTags='Source_Tag_Id'}
 foreach($row in $manifest.Objects){
  if(-not $allowed.ContainsKey($row.Table) -or $allowed[$row.Table] -ne $row.Column){throw 'Unexpected manifest table/key'}
  $current=Query "SELECT (SELECT * FROM dbo.[$($row.Table)] WITH(UPDLOCK,HOLDLOCK) WHERE [$($row.Column)]=@id FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)" @{id=[int]$row.ID} -Scalar
  if([string]$current -cne [string]$row.Snapshot){throw "Configuration row changed or missing: $($row.Table) ID $($row.ID). Refusing destructive rollback."}
 }
 foreach($table in @('Alson_Fintech_ProductMap','Alson_Fintech_ProbeConfig')){
  $order=if($table -eq 'Alson_Fintech_ProductMap'){
   if((Query "SELECT COL_LENGTH('dbo.Alson_Fintech_ProductMap','OmniSiteID')" -Scalar) -isnot [DBNull]){'CustomerID,OmniSiteID,RawCode'}else{'CustomerID,RawCode'}
  }else{'MasterId,ProbeCode'}
  $current=Query "SELECT (SELECT * FROM dbo.[$table] WITH(UPDLOCK,HOLDLOCK) WHERE BatchID=@batch ORDER BY $order FOR JSON PATH,INCLUDE_NULL_VALUES)" @{batch=$batch} -Scalar
  if([string]$current -cne [string]$manifest.CustomSnapshots.$table){throw "Custom configuration changed: $table"}
 }
 $ids=($manifest.Sites|ForEach-Object{[int]$_.SiteID}) -join ','
 foreach($kind in @(@{Code='TXN';Table='Fintech_Transactions'},@{Code='ATG';Table='Fintech_TankLevels'})){
  $extra=Query "SELECT COUNT(*) FROM dbo.[$($kind.Table)] t WITH(UPDLOCK,HOLDLOCK) WHERE t.Site_ID IN ($ids) AND NOT EXISTS(SELECT 1 FROM dbo.Alson_Fintech_Rows r WHERE r.EntityType=@kind AND r.TargetID=t.ID AND r.SiteID=t.Site_ID)" @{kind=$kind.Code} -Scalar
  if($extra -ne 0){throw "Untracked target rows exist in $($kind.Table); rollback needs review."}
  $rowsJson=Query "SELECT (SELECT TargetID,SiteID,TargetSnapshot FROM dbo.Alson_Fintech_Rows WHERE EntityType=@kind AND SiteID IN ($ids) FOR JSON PATH)" @{kind=$kind.Code} -Scalar
  foreach($row in ($rowsJson|ConvertFrom-Json)){
   $current=Query "SELECT (SELECT * FROM dbo.[$($kind.Table)] WITH(UPDLOCK,HOLDLOCK) WHERE ID=@id AND Site_ID=@site FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)" @{id=[int]$row.TargetID;site=[int]$row.SiteID} -Scalar
   if([string]$current -cne [string]$row.TargetSnapshot){throw "Imported row changed/archived/missing: $($kind.Table) ID $($row.TargetID). Refusing rollback."}
  }
 }
 # Guard untracked configuration, including later added assets or mappings.
 foreach($pair in @(@{Table='Assets';Site='Site_Id_FK'},@{Table='OPC_SourceTags';Site='Site__Id_FK'},@{Table='Omni_Fintech_TankMapping';Site='Omni_SiteID'},@{Table='Omni_Fintech_PumpMapping';Site='Omni_SiteID'},@{Table='Omni_Fintech_SiteMapping';Site='Omni_SiteID'},@{Table='Omni_Fintech_AlsonSiteMaster';Site='Omni_SiteID'})){
  $recorded=@($manifest.Objects|Where-Object Table -eq $pair.Table).Count
  $actual=Query "SELECT COUNT(*) FROM dbo.[$($pair.Table)] WITH(UPDLOCK,HOLDLOCK) WHERE [$($pair.Site)] IN ($ids)" -Scalar
  if($actual -ne $recorded){throw "Untracked later configuration in $($pair.Table)"}
 }
 $hasDiagnostics=(Query "SELECT OBJECT_ID('dbo.Alson_Fintech_DiagnosticReceipts','U')" -Scalar) -isnot [DBNull]
 if($hasDiagnostics){
  $bad=Query "SELECT COUNT(*) FROM dbo.Alson_Fintech_DiagnosticReceipts r LEFT JOIN dbo.Omni_Fintech_AlsonSiteMaster m ON m.ID=r.MasterMappingID WHERE r.SiteID IN ($ids) AND (m.ID IS NULL OR m.Omni_SiteID<>r.SiteID OR m.MasterId COLLATE Latin1_General_100_BIN2<>r.MasterId COLLATE Latin1_General_100_BIN2)" -Scalar
  if($bad -ne 0){throw 'Diagnostic ownership mismatch; review before rollback'}
 }
 if($VerifyOnly){$tx.Rollback();Write-Output 'Rollback verification passed: original manifest, configuration rows, linked target rows and diagnostic ownership match. No rows changed.';return}
 # Refer only to exact linked identities, never a blanket delete by date or site.
 Query "DELETE t FROM dbo.Fintech_Transactions t JOIN dbo.Alson_Fintech_Rows r ON r.TargetID=t.ID AND r.SiteID=t.Site_ID WHERE r.EntityType='TXN' AND r.SiteID IN ($ids)"
 Query "DELETE t FROM dbo.Fintech_TankLevels t JOIN dbo.Alson_Fintech_Rows r ON r.TargetID=t.ID AND r.SiteID=t.Site_ID WHERE r.EntityType='ATG' AND r.SiteID IN ($ids)"
 if($hasDiagnostics){
  Query "SELECT ReceiptKey INTO #OwnDiagnostics FROM dbo.Alson_Fintech_DiagnosticReceipts WHERE SiteID IN ($ids)"
  foreach($table in @('Alson_Fintech_AutoManualEvents','Alson_Fintech_DspConnectionEvents','Alson_Fintech_AtgConnectionEvents','Alson_Fintech_ProbeStatusEvents')){
   Query "DELETE e FROM dbo.[$table] e JOIN #OwnDiagnostics r ON r.ReceiptKey=e.ReceiptKey"
  }
  Query 'DELETE e FROM dbo.Alson_Fintech_DiagnosticReceipts e JOIN #OwnDiagnostics r ON r.ReceiptKey=e.ReceiptKey'
  Query "DELETE i FROM dbo.Alson_Fintech_Imports i JOIN dbo.Omni_Fintech_AlsonSiteMaster m ON m.MasterId COLLATE Latin1_General_100_BIN2=i.MasterId COLLATE Latin1_General_100_BIN2 WHERE m.Omni_SiteID IN ($ids) AND i.CommandCode IN('31','70','13','27')"
 }
 Query "DELETE i FROM dbo.Alson_Fintech_Imports i WHERE i.FrameHash IN (SELECT FrameHash FROM dbo.Alson_Fintech_Rows WHERE SiteID IN ($ids))"
 Query "SELECT DISTINCT FrameHash INTO #OwnFrames FROM dbo.Alson_Fintech_Rows WHERE SiteID IN ($ids);DELETE dbo.Alson_Fintech_Rows WHERE SiteID IN ($ids);DELETE f FROM dbo.Alson_Fintech_Frames f JOIN #OwnFrames x ON x.FrameHash=f.FrameHash WHERE NOT EXISTS(SELECT 1 FROM dbo.Alson_Fintech_Rows r WHERE r.FrameHash=f.FrameHash)"
 Query 'DELETE dbo.Alson_Fintech_ProbeConfig WHERE BatchID=@batch;DELETE dbo.Alson_Fintech_ProductMap WHERE BatchID=@batch' @{batch=$batch}
 foreach($table in @('Omni_Fintech_PumpMapping','Omni_Fintech_TankMapping','Omni_Fintech_AlsonSiteMaster','Omni_Fintech_SiteMapping','OPC_SourceTags','Assets','Plant_Site')){
  # Reverse creation order ensures nozzle children are removed before pumps.
  $rows=@($manifest.Objects|Where-Object Table -eq $table);[array]::Reverse($rows)
  foreach($row in $rows){Query "DELETE dbo.[$table] WHERE [$($row.Column)]=@id" @{id=[int]$row.ID}}
 }
 Query 'DELETE dbo.Alson_Fintech_Batches WHERE BatchID=@batch' @{batch=$batch}
 if($RemoveSchema){
  if(@($manifest.PreExistingAlsonTables).Count -ne 0){throw 'Alson tables existed before setup; refusing schema removal'}
  # Extra independent audit records prevent schema removal, avoiding loss of later work.
  if((Query 'SELECT COUNT(*) FROM dbo.Alson_Fintech_Frames' -Scalar) -ne 0 -or
     (Query 'SELECT COUNT(*) FROM dbo.Alson_Fintech_ProductMap' -Scalar) -ne 0 -or
     (Query 'SELECT COUNT(*) FROM dbo.Alson_Fintech_ProbeConfig' -Scalar) -ne 0 -or
     (Query "SELECT COUNT(*) FROM dbo.Alson_Fintech_Batches WHERE Kind='configuration'" -Scalar) -ne 0){throw 'Other Alson work exists; retain schema'}
  if($hasDiagnostics){
   if((Query 'SELECT COUNT(*) FROM dbo.Alson_Fintech_DiagnosticReceipts' -Scalar) -ne 0){throw 'Other diagnostic receipts exist; retain schema'}
   Query 'DROP VIEW IF EXISTS dbo.Alson_Fintech_DspLastSeen;DROP VIEW IF EXISTS dbo.Alson_Fintech_ProbeLastSeen;DROP TABLE dbo.Alson_Fintech_AutoManualEvents;DROP TABLE dbo.Alson_Fintech_DspConnectionEvents;DROP TABLE dbo.Alson_Fintech_AtgConnectionEvents;DROP TABLE dbo.Alson_Fintech_ProbeStatusEvents;DROP TABLE dbo.Alson_Fintech_DiagnosticReceipts;'
  }
  Query 'DROP TABLE dbo.Alson_Fintech_Imports;DROP TABLE dbo.Alson_Fintech_Rows;DROP TABLE dbo.Alson_Fintech_Frames;DROP TABLE dbo.Alson_Fintech_ProbeConfig;DROP TABLE dbo.Alson_Fintech_ProductMap;DROP TABLE dbo.Alson_Fintech_Batches;'
 }
 $tx.Commit();Write-Output "Reverted configuration batch $batch and its unchanged linked raw rows. Schema removed: $RemoveSchema"
}catch{try{$tx.Rollback()}catch{};throw}finally{$tx.Dispose();$conn.Dispose()}
