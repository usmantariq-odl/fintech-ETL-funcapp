# Fintech ETL Azure Function

The .NET 10 isolated timer scans Azure SFTP every three minutes. Numeric site IDs of any length are supported when they fit a positive SQL INT. Sites are discovered only under `/Unprocessed`; archives are read from `/Unprocessed/<site ID>/input` and processed sequentially per site, with four site workers by default. `/Unprocessed/<site ID>/output` is a sibling folder reserved for outgoing files and is not scanned by ETL.

The October 19, 2026 comparison found missing TGZ, price-response, CON, MagLink, deterministic CSV ordering and partial-success behavior. These are now implemented. See [PARITY.md](PARITY.md) for the comparison, intentional deployment differences and verification limits.

## Inputs and routing

ZIP, TGZ and TAR.GZ share one processing pipeline. CSV processing order matches the main ETL: RESFPR → PCA → PCD → GRP → PCH → PSL → TIN → TDD → PPD → TAS → other CSVs. Unknown CSVs are preserved, as in the main repo. CON CSVs are forwarded to their configured destinations, including for unconfigured sites.

Destinations at the SFTP container root:

| Result | Destination |
| --- | --- |
| Successful archive | `/Processed/<site ID>/<original name>` |
| Successful regenerated archive | Copy to `/RegeneratedFile/<site ID>/`, then move to `/Processed/<site ID>/` |
| Some CSVs failed | Filtered successful CSV archive in `/Processed/<site ID>/`; filtered failed CSV archive in `/ExceptionFile/<site ID>/` |
| All CSVs failed or archive is corrupt | `/ExceptionFile/<site ID>/<original name>` |
| Inactive/unmapped site | `/notconfigured/<site ID>/<original name>` after CON forwarding |
| Filename does not begin with its site folder ID | `/invalidFile/<site ID>/<original name>` |
| MagLink success / inactive site / data error | `/MagLinkProcessed/<site ID>/`, `/MagLinkNotConfigured/<site ID>/`, `/MagLinkException/<site ID>/` |

Partial-result archives retain their original extension and nested CSV paths. Input is deleted only after both filtered archives are published. An existing archive destination is preserved; subsequent versions go under `duplicate`. SQL, local I/O, CON delivery and SFTP failures leave input for retry. Regenerated files require a 14-digit `yyyyMMddHHmmss` timestamp immediately after the site ID; hourly recalculation uses the mapped Omni site and timestamp ±1 hour. The timestamp is validated before inserting rows.

The requested container layout is:

```text
sftp-data/
  Unprocessed/
    <site ID>/
      input/
      output/
  Processed/<site ID>/<original archive>
  ExceptionFile/<site ID>/<original archive>
  invalidFile/<site ID>/<original archive>
  notconfigured/<site ID>/<original archive>
  RegeneratedFile/<site ID>/<original archive>
```

On October 19, 2026, the user requested a reset of existing `sftp-data` folders while preserving site `111111`. That site was moved intact to `/Unprocessed/111111`; its existing empty `input` and `output` directories were verified before and after the move. The other 1,005 old top-level directories and their contents were removed, then the empty root archive folders above were created. `con-files` was outside the cleanup scope and was not changed. Live runs ensure `input` and `output` exist for discovered sites; dry runs do not create folders.

## Configuration

Start with [local.settings.example.json](local.settings.example.json). Private `local.settings.json` and `azure-app-settings.local.json` are ignored and excluded from publication. Production settings belong in the deployed Function App's application settings/environment variables, or managed secret references. Do not upload the private local settings file.

| Setting | Purpose |
| --- | --- |
| `AzureWebJobsStorage` | Function host storage; required by the timer host |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `SftpHost`, `SftpPort`, `SftpUsername`, `SftpPassword` | SFTP connection; port defaults to 22 |
| `SftpHostKeySha256` | Verified SHA256 host fingerprint; separate multiple fingerprints with `;` |
| `FintechSqlConnectionString` | Target database; current safety guard requires `Initial Catalog=FintechStagDB` |
| `EtlDryRun` | `true` lists eligible files without downloading, SQL processing or remote writes; `false` enables processing |
| `EtlWorkerCount` | Concurrent site workers, 1–32; default 4 |
| `EtlMinimumFileAgeSeconds` | Minimum input age; default 60 seconds |
| `ConSftpUsername` | Container-qualified CON login: `fintechsftpdata.con-files.sftpadmin`; uses the existing SFTP host/password/fingerprint |
| `ConSftpDestinations` | Absolute directories within the CON connection's container, separated by `;`; configured as `/Unprocessed` |
| `MagLinkSftpRoot` | Optional absolute SFTP root containing `<site ID>/stockLevels*.csv` and `<site ID>/deliveries*.csv`; empty disables discovery |

### CON container and folders

The selected container is `con-files` in `fintechsftpdata`. Its root has exactly the existing CON consumer's folder names: `ConfigurationUpdate`, `Discarded`, `Exception`, `Processed`, `Unprocessed`. These directories were created and verified through the Azure directory API on October 19, 2026.

Set these Azure Function App **Settings → Environment variables / Application settings**, already saved in the ignored local Azure settings export:

```text
ConSftpUsername=fintechsftpdata.con-files.sftpadmin
ConSftpDestinations=/Unprocessed
```

The function opens a separate connection for this container, while the input connection keeps its `sftp-data` home. The path is `/Unprocessed`, without a `/con-files` prefix. Incoming files land directly there; duplicates are preserved in `/Unprocessed/duplicate` with numbered suffixes. Files upload to a temporary name before rename. The other four directories belong to the downstream CON workflow; the ETL does not mark a delivered CON as processed before that consumer handles it. Container-qualified connection details follow [Microsoft's SFTP connection documentation](https://learn.microsoft.com/en-us/azure/storage/blobs/secure-file-transfer-protocol-support-connect).

The function ensures these directories on the first CON delivery per worker. `--prepare-con` can prepare them separately without ETL processing or SQL writes. On October 19, 2026, `sftpadmin` was granted Read, Write, List, Create and Delete permissions (`rwlcd`) for `con-files` after MFA sign-in. The existing `sftp-data` permissions (`rwdlcop`) and home directory (`sftp-data`) were verified unchanged. SFTP was then enabled on the storage account at the user's request. Read-only SFTP logins succeeded for both containers, including verification of all five CON directories.

For local testing, the same settings are under `Values` in private `local.settings.json`; its dry run remains enabled. If destinations are cleared, an archive containing a CON CSV stays in input for retry. Setting destinations without an explicit CON username is rejected to prevent delivery into the input container. Set `MagLinkSftpRoot` separately when that source path is known.

## Recovery and data integrity

SQL writes for one CSV and its successful checkpoint share a three-minute ambient SQL transaction. A data error rolls back that CSV; independent successful CSVs commit and form the processed portion. Price-response changes and MagLink bulk inserts use the same transaction boundary. MagLink uses typed bulk rows instead of interpolated SQL, supports more than 1,000 rows, and preserves the legacy epoch-1970 filter. Its parsing uses invariant culture.

The existing `Fintech_ProcessedFiles` table stores parent archive records, child CSV checkpoints (`<archive remote path>!/<relative CSV path>`), and an hourly-regeneration checkpoint. Data rows retain the parent archive's `ProcessedFileID`. Successful child checkpoints prevent repeating SQL writes after a platform restart or publication failure. Parent statuses include `Processed`, `Partial`, `Failed`, and `Retry`. Replaying a repaired file that already has a terminal record needs an explicit operator recovery decision; filenames must be immutable for each delivery. A new delivery must have a unique name.

SQL paths are canonical forward-slash paths; lookups also accept the old Windows-backslash form. Checkpoints under `/Unprocessed/<site>/input` also look up their previous `/<site>/input` identity, including child CSV checkpoints, so relocating a site does not itself repeat already committed SQL work. No SQL tracking records were deleted in the container cleanup. SQL queries no longer force stress-database-specific index names. Appropriate indexes still matter for throughput, including the alarm index in [sql/001_alarm_lookup_index.sql](sql/001_alarm_lookup_index.sql).

Live runs validate required schema objects and sequential connection enlistment first. SQL connection pooling and transaction enlistment must stay enabled. Session-owned SQL site locks prevent two instances of this version from processing the same site concurrently. Stop old ETL/backlog processes before cutover; they do not participate in these locks. Mapping caches refresh every 15 minutes.

Archive extraction rejects traversal, duplicate paths, links and special files. Limits: 1,000 entries, 100 MB per entry, 250 MB total extracted data and 250 MB compressed input. Input must remain unchanged during download. Producers should upload under a temporary name and atomically rename to the final name. Outputs also use temporary uploads and rename. Archive and CON publication is at least once: a crash after delivery can preserve an extra duplicate copy, while CSV SQL checkpoints prevent duplicate committed inserts. Downstream CON consumers must tolerate redelivery.

## Build and checks

Use the installed .NET 10 SDK. From this repository:

```powershell
$dotnet = 'C:\Users\usmtariq\.dotnet10\dotnet.exe'
& $dotnet build --nologo
& $dotnet run --project tests\FintechEtl.RegressionTests.csproj
& $dotnet bin\Debug\net10.0\FintechEtl.FunctionApp.dll --verify-sql
& $dotnet bin\Debug\net10.0\FintechEtl.FunctionApp.dll --scan-once --site 600005 --max-files 3
& $dotnet bin\Debug\net10.0\FintechEtl.FunctionApp.dll --prepare-con
```

`--verify-sql` checks 14 tables, six stored procedures, tracking columns, transaction connection reuse and mapping load without committed data writes. `--scan-once` requires `EtlDryRun=true` and performs read-only discovery. The offline regression suite does not connect to SQL or SFTP.

`--process-once` performs live writes using the timer's method and explicitly enables live processing. Optional local filters: `--site`, `--site-min`, `--site-max`, `--max-files`. Those filters apply only to local one-shot execution. Running the worker DLL directly avoids the Core Tools launch used by `dotnet run` for the function project.

## Deploy and production sign-off

Open this repository folder in VS Code and deploy its publish output using the existing `.vscode` tasks. Alternatively run `dotnet publish -c Release` and deploy `bin/Release/net10.0/publish` with `host.json` and function metadata at the root. The Function host needs its own storage configuration, .NET 10 isolated runtime and Always On for the existing App Service plan. The configured timeout is one day; new file work stops after 23.5 hours, and host cancellation is observed between files. Database transactions may roll back on host termination; checkpoints support restart recovery.

Current verification: 37 offline regression scenarios passed, including the new site hierarchy, `/Processed` routing and legacy checkpoint compatibility. Read-only checks on `FintechStagDB` passed for schema, transaction enlistment and mapping load. Simulator site `600005` has no active mapping in this database and would route to `notconfigured`; no site mappings were changed. The CON directories were created and verified in Azure. Both ignored private settings files select `FintechStagDB`, the separate CON login and `/Unprocessed` within `con-files`. Local dry run remains enabled. Build/publish validation is recorded in [PARITY.md](PARITY.md).

The initial CON SFTP preparation attempt failed because Azure returned **SSH/SFTP are not enabled for this account**. The separate CON permission grant succeeded after MFA sign-in. Subsequently, the user requested SFTP enablement; Azure confirmed `isSftpEnabled=true`, and both container connections passed read-only directory checks. No ETL run, deployment or database schema changes were initiated as part of these checks. The currently deployed Azure application settings were not changed; deploy the build and apply the updated private settings export when ready.

Production sign-off still requires a controlled end-to-end test of uploads/routing/regeneration, the MagLink source path if needed, and normal capacity/alert verification. The database guard now targets the requested staging database; secondary-database writes remain disabled.
