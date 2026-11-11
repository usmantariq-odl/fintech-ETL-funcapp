param([switch]$Apply)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$evidence=Join-Path $repo 'artifacts\alson\setup'
New-Item -ItemType Directory -Path $evidence -Force|Out-Null
$settings=Get-Content (Join-Path $repo 'local.settings.json') -Raw|ConvertFrom-Json
$sql=$settings.Values.ApplicationSettings__AzureSql
$builder=[System.Data.SqlClient.SqlConnectionStringBuilder]::new($sql)
if($builder.InitialCatalog -ne 'FintechStagDB_NewArchitecture'){throw 'Setup is restricted to the new architecture staging database.'}
if(-not $Apply){Write-Output 'Preview only. Creates NEW Makkah/Manzoor sites for customer 227 and additive Alson metadata. Supply -Apply to execute.';return}
$conn=[System.Data.SqlClient.SqlConnection]::new($sql);$conn.Open()
try {
 $cmd=$conn.CreateCommand();$cmd.CommandText='SELECT DB_NAME()';if($cmd.ExecuteScalar() -ne 'FintechStagDB_NewArchitecture'){throw 'Wrong database'}
 $cmd.CommandText="SELECT name FROM sys.tables WHERE schema_id=SCHEMA_ID('dbo') AND name LIKE 'Alson_Fintech_%'"
 $r=$cmd.ExecuteReader();$existing=[System.Collections.Generic.List[string]]::new();while($r.Read()){$existing.Add($r.GetString(0))};$r.Close()
 # Schema changes are additive and independent from the existing configuration.
 $cmd.CommandText=Get-Content (Join-Path $PSScriptRoot 'Create-AlsonFintech.sql') -Raw;$cmd.CommandTimeout=90;[void]$cmd.ExecuteNonQuery()
 $batch=[guid]::NewGuid();$label='AlsonSetup:'+$batch.ToString('N')
 $transaction=$conn.BeginTransaction([System.Data.IsolationLevel]::Serializable)
 function Query([string]$text,[hashtable]$values=@{},[switch]$Scalar){
  $command=$conn.CreateCommand();$command.Transaction=$transaction;$command.CommandTimeout=60;$command.CommandText=$text
  foreach($key in $values.Keys){[void]$command.Parameters.AddWithValue('@'+$key,$(if($null -eq $values[$key]){[DBNull]::Value}else{$values[$key]}))}
  try{if($Scalar){return $command.ExecuteScalar()}else{[void]$command.ExecuteNonQuery()}}finally{$command.Dispose()}
 }
 $objects=[System.Collections.Generic.List[object]]::new();$sites=[System.Collections.Generic.List[object]]::new()
 function Track([string]$table,[string]$column,[int]$id){
  $row=Query "SELECT (SELECT * FROM dbo.[$table] WHERE [$column]=@id FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)" @{id=$id} -Scalar
  $objects.Add([ordered]@{Table=$table;Column=$column;ID=$id;Snapshot=[string]$row})
 }
 try{
  $lock=Query "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='AlsonFintech:Lifecycle',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;SELECT @r" -Scalar
  if($lock -lt 0){throw 'Could not acquire setup lock'}
  if((Query 'SELECT COUNT(*) FROM dbo.Customer WHERE Customer_Id=227' -Scalar) -ne 1){throw 'Customer 227 missing'}
  if((Query "SELECT COUNT(*) FROM dbo.Plant_Site WHERE Customer_Id_FK=227 AND Site_Name IN ('Makkah','Manzoor')" -Scalar) -ne 0){throw 'New site names already exist. Review instead of duplicating.'}
  if((Query 'SELECT COUNT(*) FROM dbo.Omni_Fintech_SiteMapping WHERE FC_SiteID IN (560257,560256)' -Scalar) -ne 0){throw 'External FC site IDs already mapped.'}
  if((Query "SELECT COUNT(*) FROM dbo.Omni_Fintech_AlsonSiteMaster WHERE MasterId IN ('81C3C8CBD19','81C3C8CD7C3','81C3C8CE581','81C3C8CD824')" -Scalar) -ne 0){throw 'Source master already mapped. Do not reassign automatically.'}
  Query "INSERT dbo.Alson_Fintech_Batches(BatchID,Kind) VALUES(@batch,'configuration')" @{batch=$batch}
  foreach($product in @(@{Code='R';Grade=1;Name='Hi Super'},@{Code='D';Grade=2;Name='Diesel'},@{Code='H';Grade=3;Name='Excellium'})){
   $id=Query 'SELECT Id FROM dbo.Omni_Fintech_Products WHERE OmniCustomerID_FK=227 AND product_id=@grade AND product_name=@name' @{grade=$product.Grade;name=$product.Name} -Scalar
   if($null -eq $id -or $id -is [DBNull]){throw 'Expected existing customer product missing'}
   if((Query 'SELECT COUNT(*) FROM dbo.Alson_Fintech_ProductMap WHERE CustomerID=227 AND RawCode=@code' @{code=$product.Code} -Scalar) -ne 0){throw 'Alson product mapping already exists; review its owner before setup'}
   Query 'INSERT dbo.Alson_Fintech_ProductMap(CustomerID,RawCode,ProductRowID,BatchID) VALUES(227,@code,@product,@batch)' @{code=$product.Code;product=$id;batch=$batch}
  }
  $definitions=@(
   @{Name='Makkah';Code=560257;DSP='81C3C8CBD19';ATG='81C3C8CD7C3';Probes=@('24696','24697','24699','83939');Codes=@('0@','0A','0B','0C','0D','0E','0F','0G','0H','0I','0J','0K')},
   @{Name='Manzoor';Code=560256;DSP='81C3C8CE581';ATG='81C3C8CD824';Probes=@('00002','00004','00007','00009');Codes=@('0@','0A','0B','0C','0D','0E','0F','0G','0H','0I','0P','0Q')}
  )
  foreach($site in $definitions){
   # Insert sites individually: the existing trigger handles only one inserted site.
   $siteID=[int](Query "INSERT dbo.Plant_Site(Site_Name,OriginalSiteName,Customer_Id_FK,DeviceTypeId,IsActive,Created_By,Created_Date,Description) VALUES(@name,@name,227,3,1,@label,SYSUTCDATETIME(),@description);SELECT CONVERT(int,SCOPE_IDENTITY())" @{name=$site.Name;label=$label;description="New Alson staging site; external pilot code $($site.Code). Physical channel-to-tank assignments pending."} -Scalar)
   Track 'Plant_Site' 'Site_Id' $siteID
   $siteMap=[int](Query 'INSERT dbo.Omni_Fintech_SiteMapping(Omni_SiteID,FC_SiteID,CreatedOn,CreatedBy) VALUES(@site,@fc,SYSUTCDATETIME(),@label);SELECT CONVERT(int,SCOPE_IDENTITY())' @{site=$siteID;fc=$site.Code;label=$label} -Scalar)
   Track 'Omni_Fintech_SiteMapping' 'ID' $siteMap
   foreach($master in @(@{Type='DSP';ID=$site.DSP},@{Type='ATG';ID=$site.ATG})){
    # Leave legacy mandatory IP blank; ETL does not use it. No shared schema/API removal.
    $masterMap=[int](Query "INSERT dbo.Omni_Fintech_AlsonSiteMaster(Omni_SiteID,DeviceType,MasterId,IpAddress,IsActive,CreatedOn,CreatedBy) VALUES(@site,@type,@master,'',1,SYSUTCDATETIME(),@label);SELECT CONVERT(int,SCOPE_IDENTITY())" @{site=$siteID;type=$master.Type;master=$master.ID;label=$label} -Scalar)
    Track 'Omni_Fintech_AlsonSiteMaster' 'ID' $masterMap
   }
   foreach($probe in $site.Probes){
    $asset=[int](Query 'INSERT dbo.Assets(Asset_Name,Site_Id_FK,AssetType,Created_By,Created_Date,Description) VALUES(@name,@site,@type,@label,SYSUTCDATETIME(),@description);SELECT CONVERT(int,SCOPE_IDENTITY())' @{name="Tank $probe";site=$siteID;type='24';label=$label;description="Alson tank/probe $probe; product and capacity unconfirmed."} -Scalar)
    Track 'Assets' 'Asset_Id' $asset
    $mapping=[int](Query 'INSERT dbo.Omni_Fintech_TankMapping(FC_TankID,FC_SiteID,Omni_AssetID,Omni_SiteID,CreatedOn,CreatedBy,MasterId) VALUES(@tank,@fc,@asset,@site,SYSUTCDATETIME(),@label,@master);SELECT CONVERT(int,SCOPE_IDENTITY())' @{tank=[int]$probe;fc=$site.Code;asset=$asset;site=$siteID;label=$label;master=$site.ATG} -Scalar)
    Track 'Omni_Fintech_TankMapping' 'ID' $mapping
    # User confirmed raw fuel/water map directly to existing volume columns.
    # Ceiling is an invalid-data guard, not an assertion of tank capacity.
    Query "INSERT dbo.Alson_Fintech_ProbeConfig(MasterId,ProbeCode,TankMappingID,FuelKind,WaterKind,FuelScale,WaterScale,MaxFuel,MaxWater,BatchID) VALUES(@master,@probe,@mapping,'VOLUME','VOLUME',1,1,1000000,1000000,@batch)" @{master=$site.ATG;probe=$probe;mapping=$mapping;batch=$batch}
   }
   $number=0
   foreach($code in $site.Codes){
    $number++ # Explicit allocated numeric channel key; never parse hex or infer a physical unit.
    $pump=[int](Query 'INSERT dbo.Assets(Asset_Name,Site_Id_FK,AssetType,Created_By,Created_Date,Description) VALUES(@name,@site,@type,@label,SYSUTCDATETIME(),@description);SELECT CONVERT(int,SCOPE_IDENTITY())' @{name="Dispenser channel $code";site=$siteID;type='25';label=$label;description="Alson address $code; physical dispenser grouping not established."} -Scalar)
    Track 'Assets' 'Asset_Id' $pump
    $nozzle=[int](Query 'INSERT dbo.Assets(Asset_Name,Site_Id_FK,AssetType,Asset_Parent_Id,Created_By,Created_Date,Description) VALUES(@name,@site,@type,@parent,@label,SYSUTCDATETIME(),@description);SELECT CONVERT(int,SCOPE_IDENTITY())' @{name="Channel $code nozzle 1";site=$siteID;type='26';parent=$pump;label=$label;description='Source nozzle 1; tank association unconfirmed.'} -Scalar)
    Track 'Assets' 'Asset_Id' $nozzle
    $mapping=[int](Query 'INSERT dbo.Omni_Fintech_PumpMapping(FC_NozzleID,FC_PumpID,FC_SiteID,Omni_SiteID,Omni_AssetPumpID,Omni_AssetNozzleID,MasterId,AlsonPumpCode) VALUES(1,@pump,@fc,@site,@asset,@nozzle,@master,@code);SELECT CONVERT(int,SCOPE_IDENTITY())' @{pump=$number;fc=$site.Code;site=$siteID;asset=$pump;nozzle=$nozzle;master=$site.DSP;code=$code} -Scalar)
    Track 'Omni_Fintech_PumpMapping' 'ID' $mapping
   }
   $tagJson=Query 'SELECT (SELECT Source_Tag_Id FROM dbo.OPC_SourceTags WHERE Site__Id_FK=@site FOR JSON PATH)' @{site=$siteID} -Scalar
   foreach($tag in ($tagJson|ConvertFrom-Json)){Track 'OPC_SourceTags' 'Source_Tag_Id' $tag.Source_Tag_Id}
   $sites.Add([ordered]@{Name=$site.Name;SiteID=$siteID;FCSiteID=$site.Code;DSP=$site.DSP;ATG=$site.ATG})
  }
  $custom=[ordered]@{}
  foreach($table in @('Alson_Fintech_ProductMap','Alson_Fintech_ProbeConfig')){
   $order=if($table -eq 'Alson_Fintech_ProductMap'){'CustomerID,RawCode'}else{'MasterId,ProbeCode'}
   $custom[$table]=[string](Query "SELECT (SELECT * FROM dbo.[$table] WHERE BatchID=@batch ORDER BY $order FOR JSON PATH,INCLUDE_NULL_VALUES)" @{batch=$batch} -Scalar)
  }
  $manifest=[ordered]@{BatchID=$batch;Database=$conn.Database;CustomerID=227;Sites=$sites.ToArray();Objects=$objects.ToArray();CustomSnapshots=$custom;PreExistingAlsonTables=$existing.ToArray();CreatedOn=[datetime]::UtcNow.ToString('O')}
  $json=ConvertTo-Json -InputObject $manifest -Depth 12
  Query 'UPDATE dbo.Alson_Fintech_Batches SET Manifest=@manifest WHERE BatchID=@batch' @{manifest=$json;batch=$batch}
  # Save before commit too: if local disk fails, SQL changes remain uncommitted.
  $path=Join-Path $evidence ($batch.ToString()+'.json');Set-Content -LiteralPath $path -Value $json -Encoding UTF8
  $transaction.Commit()
  Write-Output ('Configuration committed. Batch '+$batch);$sites.ToArray()|Format-Table
  Write-Output ('Rollback manifest: '+$path)
 }catch{try{$transaction.Rollback()}catch{};throw}finally{$transaction.Dispose()}
}finally{$conn.Dispose()}
