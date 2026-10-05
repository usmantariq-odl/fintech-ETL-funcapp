# Fintech ETL Azure Function

## Deploy from VS Code

Open `C:\Users\usmtariq\Desktop\fintech_etl_funcapp` as the VS Code folder. In the Azure Functions extension, choose **Deploy to Function App** and select this folder. The `.vscode` settings run the `publish (functions)` task with the local .NET 10 SDK, then deploy `bin/Release/net10.0/publish`. The project source folder is the folder to select; do not select `publish`, `bin`, or the old ZIP file. The publish output contains `host.json` and function metadata at its root. Private local settings, the Azure app settings export, and backlog logs are excluded.

Set the Azure Function App application settings separately using the private `azure-app-settings.local.json` file. Do not upload `local.settings.json` from VS Code: it has an empty local host storage setting and `EtlDryRun=true`. The Azure app must use its configured `AzureWebJobsStorage` and `EtlDryRun=false` for live processing. The timer starts scanning existing input ZIPs after deployment.

This .NET 10 isolated Azure Function runs every three minutes (`0 */3 * * * *`, UTC). It signs into the Azure Blob SFTP account as `fintechsftpdata.sftpadmin`, lists numeric site directories under `/` regardless of digit count, and reads complete ZIP files in `/<site ID>/input`. Site IDs must fit the database's positive SQL `INT` mapping. It uses the existing Fintech ETL mapping, CSV reader and DAL code to write SQL rows. The archive destinations at the SFTP container root are:

- Valid, configured ZIP: `/ProcessedFile/<site ID>/<original name>`.
- Regenerated ZIP: copied to `/RegeneratedFile/<site ID>/`, then moved to `/ProcessedFile/<site ID>/` after the hourly regeneration procedure succeeds.
- Site absent from the active mapping: `/notconfigured/<site ID>/<original name>` without processing its CSVs.
- ETL processing or data error: `/ExceptionFile/<site ID>/<original name>`, with the failure logged and its database status set to `Failed` when a record exists.
- Invalid ZIP name: `/invalidFile/<site ID>/<original name>`. A valid name begins with the full numeric site folder ID, has content after that ID, and ends in `.zip`. The original ETL assumed the first six filename characters were the site ID but did not have an `invalidFile` route or require a timestamp for ordinary ZIPs. The Function supports numeric IDs of other lengths as requested. Regenerated ZIPs still need a valid 14-digit `yyyyMMddHHmmss` timestamp after the ID.

An existing destination name goes under that site's `duplicate` subfolder with a unique suffix. SFTP failures, directly thrown SQL exceptions, and failed archive moves leave the ZIP in input for retry. The original regenerated stored procedure wrapper catches its SQL errors and marks them as ETL failures, so those ZIPs go to `ExceptionFile`. A ZIP already marked `Processed` in `Fintech_ProcessedFiles` is archived without inserting rows again after a prior move failure.

The Function reuses the original mapping, CSV readers, DAL inserts, and `Fintech_Regenerated_Files` stored procedure call. Its surrounding behavior is intentionally different: it accepts numeric site folders of any length, handles ZIPs rather than the original ZIP/TGZ/MagLink sources, validates ZIPs and PSL product mappings before insertion, processes sites in parallel, and adds `invalidFile` routing. Regenerated ZIP routing now follows the original ETL: copy to `RegeneratedFile`, then move to `ProcessedFile`.

The 2026-09-29 local routing test confirmed `ProcessedFile`, `ExceptionFile`, `invalidFile`, and `notconfigured` moves. A regenerated sample reached the original `Fintech_Regenerated_Files` procedure but timed out at both 30 and 180 seconds, so successful `RegeneratedFile` routing remains unverified. The original 30-second command timeout and error behavior were restored after the test.

The timer uses `EtlWorkerCount` parallel site workers, defaulting to `4`. It creates at most four long-lived workers per invocation; each worker reuses its own SFTP connection and ETL mapping as it moves between sites. Files within one site are processed in filename order. The `host.json` timeout is one day (`1.00:00:00`); an active run stops accepting new files after 23.5 hours to leave cleanup time. Runs usually finish much earlier. A timer invocation does not start another independent copy for each site, so `FUNCTIONS_WORKER_PROCESS_COUNT` does not control this ETL's site concurrency.

## Configuration

Configure Function App application settings from [local.settings.example.json](local.settings.example.json). `SftpPassword` is the admin password from the simulator's `config.toml`; it is present only in the ignored local `local.settings.json`. `SftpHostKeySha256` contains the verified host key fingerprint(s), separated by semicolons. `AzureWebJobsStorage` is the Function host's own storage connection string. The SFTP container is reached through the admin user's home directory, so paths begin with `/<site ID>`.

For the Azure App Service plan, select the **.NET 10 isolated** runtime and enable **Always On**. Set `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`, `FUNCTIONS_EXTENSION_VERSION=~4`, `AzureWebJobsStorage`, the SFTP settings, `FintechSqlConnectionString`, `EtlDryRun=false`, and `EtlWorkerCount=4` in application settings. Increase `EtlWorkerCount` only after measuring SQL and SFTP capacity. A 24-hour timeout is an upper bound, not a guarantee against platform restarts.

`FintechSqlConnectionString` **must** have `Initial Catalog=FintechStressTesting`. Startup validation rejects another catalog or an empty live connection string before any ZIP is processed. The private local setting now points to that database. Set `EtlDryRun=false` to enable processing after verifying its schema and site mappings. The SQL database needs the mapping tables, ETL tables, table types and stored procedures used by the copied DAL.

The original `App.config` was not copied: it has VM paths and enables writes to production and staging databases. The Function uses Azure application settings. Dual database insert is disabled in the copied DAL.

## Local checks

```powershell
& 'C:\Users\usmtariq\.dotnet10\dotnet.exe' build
& 'C:\Users\usmtariq\.dotnet10\dotnet.exe' run --no-build -- --scan-once --site-min 600000 --site-max 600010 --max-files 8
& 'C:\Users\usmtariq\.dotnet10\dotnet.exe' run --no-build -- --verify-sql
```

The scan command needs `EtlDryRun=true` and performs a read-only SFTP scan. The private local settings currently keep dry run enabled. To run the timer locally, install Azure Functions Core Tools, set `AzureWebJobsStorage`, change `EtlDryRun` to `false`, and run `func start`. Use the Function App's .NET 10 isolated runtime for deployment.

The extracted ZIP is capped at 1,000 entries and 250 MB total unpacked size. The Function processes sites in parallel and ZIPs for each site sequentially. The existing DAL handles each CSV write independently; if a later CSV fails, earlier SQL rows may already have been written. Review the stress database's duplicate handling before replaying such a ZIP.

## Live test findings

The local one-shot command uses the same method as the timer:

```powershell
& 'C:\Users\usmtariq\.dotnet10\dotnet.exe' run --no-build -- --process-once
```

`--site 600005 --max-files 1` narrows a local run to one ZIP; `--site-min` and `--site-max` select a local site range. The first live attempt exposed missing product mappings for customer `252`; these were added to `FintechStressTesting`, and the retried ZIP processed successfully. The Function rejects a ZIP with an unmapped PSL grade before inserting any CSV group or moving the ZIP. Existing partial rows from the first attempt remained associated with its failed `Fintech_ProcessedFiles` record; the legacy duplicate checks prevented re-inserting its PPD sale IDs on retry.

The stress database's alarm lookup had no site/time index. [sql/001_alarm_lookup_index.sql](sql/001_alarm_lookup_index.sql) was applied to `FintechStressTesting` and is needed if that database is rebuilt. Its sample alarm lookup now completes in under one second.

A read-only inventory initially found 22,481 ZIPs across 1,002 site input folders. Site `111222` has no input folder. The non-simulator site `111111` has two ZIPs and product rows, but its `FintechMappedCustomers` record is inactive; the ETL rejects it as unconfigured. The simulator ZIP for site `600005` processed successfully after the product mapping was added. Eight separate .NET 8 local processes are draining the simulator backlog while this .NET 10 build is prepared; their logs are under the ignored `backlog-runs` directory.
