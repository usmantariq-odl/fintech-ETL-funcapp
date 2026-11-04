# Fintech ETL Azure Function

The .NET 10 isolated timer scans Azure SFTP every minute. Numeric site IDs of any length are supported when they fit a positive SQL INT. Sites are discovered only under `/Unprocessed`; archives are read from `/Unprocessed/<site ID>/input` and processed sequentially per site, with four site workers by default. `/Unprocessed/<site ID>/output` is a sibling folder reserved for outgoing files and is not scanned by ETL.

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

Partial-result archives retain their original extension and nested CSV paths. Input is deleted only after both filtered archives are published. An existing archive destination is preserved; subsequent versions go under `duplicate`. Transient SQL, local I/O, CON delivery and SFTP failures leave input for retry. Deterministic SQL data failures (including truncation 2628/8152) fail the affected CSV and produce Partial/Failed exception outputs. Regenerated files require a 14-digit `yyyyMMddHHmmss` timestamp immediately after the site ID; hourly recalculation uses the mapped Omni site and timestamp ±1 hour. The timestamp is validated before inserting rows.

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
| `FintechSqlConnectionString` | Selects the SQL server and database. Set `Initial Catalog=<database>` or `Database=<database>`; no database name is fixed in code. Enlist and Pooling must stay enabled. |
| `EtlDryRun` | `true` lists eligible files without downloading, SQL processing or remote writes; `false` enables processing |
| `EtlWorkerCount` | Concurrent site workers, 1–32; default 4 |
| `EtlMinimumFileAgeSeconds` | Minimum input age; default 60 seconds |
| `ConSftpUsername` | Container-qualified CON login: `fintechsftpdata.con-files.sftpadmin`; uses the existing SFTP host/password/fingerprint |
| `ConSftpDestinations` | Absolute directories within the CON connection's container, separated by `;`; configured as `/Unprocessed` |
| `MagLinkSftpRoot` | Optional absolute SFTP root containing `<site ID>/stockLevels*.csv` and `<site ID>/deliveries*.csv`; empty disables discovery |
| `SftpProvisioningSubscriptionId` | Azure subscription containing the SFTP storage account |
| `SftpProvisioningResourceGroup` | Resource group containing that account |
| `SftpProvisioningStorageAccount` | Storage account whose local users are provisioned; must match `SftpHost` and `SftpUsername` |

### Create a site SFTP user over HTTP

The same app now exposes `POST /api/sftp/users` (`CreateSftpUser`). It requires a Function access key in the `x-functions-key` header. Use HTTPS, keep that key on a trusted backend, and retain the returned password securely; neither requests nor responses containing credentials should be logged by the caller.

```http
POST https://<function-app>.azurewebsites.net/api/sftp/users
Content-Type: application/json
x-functions-key: <function-key>

{"username":"123456"}
```

`username` is also the site ID: a JSON string of digits representing an integer from 1 to 2147483647. Leading zeros are allowed and preserved in the returned site ID, SFTP username and folder paths. For example, `{"username":"007001"}` creates `user007001`, with login `fintechsftpdata.user007001` and home `sftp-data/Unprocessed/007001`; it creates that home's `input` and `output` directories. Keep the value quoted so its zeros are preserved. The endpoint adds the `user` prefix for the Azure SFTP account; send the numeric username without the prefix. A separate `siteId` is no longer accepted. All-zero IDs, extra fields, duplicate fields, invalid paths and bodies larger than 4096 bytes are rejected.

**Caller-chosen passwords are not supported by Azure Blob SFTP.** Azure generates the SFTP password and the endpoint returns it once. Sending a `password` field returns HTTP 400 with `success:false` and error code `CustomPasswordNotSupported`, before any provisioning. The submitted password is never applied, persisted or echoed. See [Microsoft's password limitation](https://learn.microsoft.com/en-us/azure/storage/blobs/secure-file-transfer-protocol-support#passwords).

Success is HTTP 201:

```json
{
  "success": true,
  "siteId": "123456",
  "username": "user123456",
  "loginUsername": "fintechsftpdata.user123456",
  "host": "fintechsftpdata.blob.core.windows.net",
  "port": 22,
  "homeDirectory": "sftp-data/Unprocessed/123456",
  "password": "<Azure-generated-password>",
  "error": null,
  "requestId": "<correlation-id>"
}
```

The endpoint creates `sftp-data/Unprocessed/123456/{input,output}`. The site and both child directories are owned by the new Azure local-user ID, have its unique primary group, and use `0700` permissions. The user has ACL authorization and **no container-wide permission scopes**, so access is confined to its own site. The common ancestors permit traversal, with no read/write permission for other users. Existing sibling owners and ACLs are retained; provisioning refuses to proceed if a sibling branch or the CON container root exposes access to other users. Keep any additional containers private as well. This follows [Azure's local-user ACL model](https://learn.microsoft.com/en-us/azure/storage/blobs/secure-file-transfer-protocol-support#access-control-lists-acls), including its requirement to set local-user ownership through SFTP.

Before returning the password, the endpoint logs in as the new user, verifies read/write/delete in `input` and `output`, verifies listing of the site, checks denial of ancestor/sibling listings and explicitly checks that a `con-files` login is denied. Azure authentication is disabled while the folders are being configured. Password access is enabled only after ownership/ACL checks. The password is generated by Azure and returned once with `Cache-Control: no-store`; the app does not persist or log it.

Failures return `success:false`, `password:null`, an `error` object and a `requestId`. For example, an existing username returns HTTP 409:

```json
{
  "success": false,
  "password": null,
  "error": {
    "code": "UserAlreadyExists",
    "message": "The SFTP username already exists. Its password and access were not changed."
  },
  "requestId": "<correlation-id>"
}
```

Other codes include `InvalidRequest`/`InvalidUsername`/`CustomPasswordNotSupported` (400), `SiteAlreadyExists`/`SiteAlreadyAssigned`/`ProvisioningBusy` (409), `UnsafeContainerAcl`/`ProvisioningNotConfigured` (503), and Azure/access verification failures (500/502). An existing site folder is never reassigned. Site assignment checks include users with homes in the former `sftp-data/<site>` layout; deleting an old folder does not remove its Azure local user. Existing users require a separate migration/password-recovery operation if their old home needs updating. Existing clients must change their request from `siteId` plus username to the numeric `username` only, and read failures from `error` instead of `exception`.

Provisioning requests serialize across app instances using a session SQL lock in the database selected by `FintechSqlConnectionString`. The endpoint requires that SQL connection even when `EtlDryRun=true`; the dry-run setting applies to the ETL timer, and does not disable this explicit onboarding endpoint. It takes no ETL data or schema action. Do not provision the same usernames concurrently through another administration tool; Azure's local-user PUT API is create-or-update. After a failure following creation, the app disables the new user's authentication, deletes only the empty directories created by that request, then removes the user. This keeps the directory owner ID resolvable during cleanup. `ProvisioningCleanupFailed` requires administrator inspection. An ambiguous Azure create response, a process crash, or a lost HTTP success response also requires inspecting the username/site before retrying; duplicate requests never reset an existing user's password.

Before deployment, enable the Function App's system-assigned managed identity and grant it these management actions **scoped to `fintechsftpdata`**: local-user read, write, delete and regeneratePassword. Blob data RBAC and account-key access are not required for this endpoint; folder/ACL operations reuse the pinned `sftpadmin` SFTP connection, which requires its existing Modify Ownership/Modify Permissions rights. [Azure managed roles and narrow assignment scopes](https://learn.microsoft.com/en-us/azure/role-based-access-control/role-assignments) apply independently of SFTP folder ACLs.

The deployment helper enables that identity, creates/assigns a custom role with only those four actions, and sets the three nonsecret resource settings:

```powershell
.\deployment\Configure-SftpProvisioning.ps1 -FunctionAppName '<your-ETL-function-app>'
```

If the Function App is in a different subscription/resource group, also supply
`-FunctionSubscriptionId` and `-FunctionResourceGroup`. `-SubscriptionId` continues
to select the storage account subscription for the role definition/assignment and
`SftpProvisioningSubscriptionId` setting. Both subscriptions must belong to the same tenant.

Run it with an Azure account permitted to create roles/assignments; allow RBAC propagation. The three settings are also saved in `local.settings.example.json` and both ignored private settings files. Standard `DefaultAzureCredential` uses the managed identity in Azure; local development uses a signed-in Azure CLI account. The existing SFTP and SQL secrets remain in the private files.

On October 20, 2026 the helper was run for `func-fintech-ETL-stag` in `Fintech-RG-Stag`,
subscription `0a26d492-a92f-46a4-8d6a-1c87ea1e0432`. Its system-assigned identity was enabled
(principal `d6fc1b79-295c-470c-ba2e-d0dec52c20c2`) and the three storage-target application
settings were applied and verified. The Azure function listing now includes both
`CreateSftpUser` and `FintechEtlTimer`. The storage-role step was rejected with
`AuthorizationFailed`: the signed-in account lacks `Microsoft.Authorization/roleDefinitions/write`
and effective IAM role-assignment write permission on the storage account. The role has not
been created/assigned by this setup attempt; an authorized Azure administrator must complete it.

The administrator can run only the storage IAM step from this repository, without needing
to change the Function App identity or settings again:

```powershell
.\deployment\Configure-SftpProvisioning.ps1 `
    -FunctionAppName 'func-fintech-ETL-stag' `
    -RoleSetupOnly `
    -ExistingPrincipalId 'd6fc1b79-295c-470c-ba2e-d0dec52c20c2'
```

The default storage subscription/resource group/account in that command are the verified
`fintechsftpdata` target. Role-only mode and separate-subscription routing have been checked
with a mocked CLI; that check makes no Azure changes. No code deployment, local-user creation,
or manual ETL execution was performed as part of this setup attempt.

The offline suite exercises the HTTP handler's response/body/cache rules, validation, duplicate rejection, safe rollback and lock behavior. An optional **live** integration check creates a uniquely named temporary user/site, verifies the actual Azure ACL restrictions and duplicate rejection, then removes its own empty test folders and user:

```powershell
& $dotnet run --project tests\FintechEtl.RegressionTests.csproj -- --live-provisioning-check
```

Normal regression runs perform no network access. No deployment or timer execution occurs in this integration check.

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

`Fintech_ProcessedFiles` receives archive records only (`ZIP`, `TGZ`, `TAR.GZ`). CSV completion/failure, standalone MagLink CSV and hourly-regeneration checkpoints are stored separately in `dbo.Fintech_EtlFileCheckpoints`. Archive-contained data rows retain the parent archive's `ProcessedFileID`; standalone MagLink data has no parent archive and uses a null ID. Parent statuses include `Processed`, `Partial`, `Failed`, and `Retry`.

Archive replay follows the original `FintechETL`: the same path and filename, even with a previous `Processed` status, does not block incoming CSVs. A PSL-only exception archive can therefore reuse the original filename and parent ID. The unchanged transaction DAL excludes rows already present for the mapped site + `Sale_ID`. Archive name collisions are preserved in the destination's `duplicate` subfolder after processing; filenames alone do not decide whether data is inserted.

Only an identical CSV payload with a successful, transactionally committed content checkpoint is skipped. A changed or newly added CSV is validated and processed, and a previously failed CSV is attempted again. `Partial` means the current input contains both successful and failed CSVs: successful CSVs go to the processed archive, failed CSVs to the exception archive. Replaying an exception subset recomputes its status from that attempt, so it becomes `Processed` when its CSVs succeed. Regeneration checkpoints identify the CSV content manifest, allowing aggregation to run after changed data. Standalone MagLink files also use content fingerprints.

Content fingerprints are encoded in the existing checkpoint primary key; no schema migration or new setting is needed. Older path-only checkpoints cannot prove completion of the current bytes, so previously unseen content is processed once through the existing row duplicate checks. Historical audit/checkpoint records remain untouched. Identical completed content still stays skipped if its database data is later manually removed; that requires an explicit recovery decision. Producers must continue uploading to a temporary filename and renaming atomically after the upload is complete.

Before a live timer scan, the app creates the checkpoint table if missing using the embedded, idempotent [deployment/Create-EtlFileCheckpoints.sql](deployment/Create-EtlFileCheckpoints.sql). This follows the database selected by `FintechSqlConnectionString`; no new environment setting is required. The SQL identity needs table-creation permission for the first run, or a database administrator can run that script beforehand and grant the runtime identity SELECT/INSERT/UPDATE on the table. If setup fails, the scan stops before touching input files. Dry runs and `--verify-sql` do not create the table. No existing application rows are migrated or removed.

SQL paths are canonical forward-slash paths; lookups also accept the old Windows-backslash form. Checkpoints under `/Unprocessed/<site>/input` also look up their previous `/<site>/input` identity, including child CSV checkpoints, so relocating a site does not itself repeat already committed SQL work. No SQL tracking records were deleted in the container cleanup. SQL queries no longer force stress-database-specific index names. Appropriate indexes still matter for throughput, including the alarm index in [sql/001_alarm_lookup_index.sql](sql/001_alarm_lookup_index.sql).

Live runs validate required schema objects and sequential connection enlistment first. SQL connection pooling and transaction enlistment must stay enabled. Session-owned SQL site locks prevent two instances of this version from processing the same site concurrently. Stop old ETL/backlog processes before cutover; they do not participate in these locks. Mapping caches refresh every 15 minutes.

Archive extraction rejects traversal, duplicate paths, links and special files. Limits: 1,000 entries, 100 MB per entry, 250 MB total extracted data and 250 MB compressed input. Input must remain unchanged during download. Producers should upload under a temporary name and atomically rename to the final name. Outputs also use temporary uploads and rename. Archive and CON publication is at least once: a crash after delivery can preserve an extra duplicate copy, while CSV SQL checkpoints prevent duplicate committed inserts. Downstream CON consumers must tolerate redelivery.

## Large transaction CSVs

PSL transaction inserts use batches of 1,000 rows on one SQL connection. The existing stored procedure, value conversions, Site_ID/Sale_ID duplicate rules and archive ProcessedFileID are preserved. Short CSV strings use bounded TVP metadata instead of advertising every field as nvarchar(max); genuine long fields remain untruncated. Existing-sale lookups use a hash set. Duplicate CSV records are still written, with one summary log instead of a message for every row.

Every SQL batch logs progress as **sent, awaiting CSV transaction commit**. All batches and the content checkpoint remain in one CSV transaction: any failed batch rolls back all new rows from that CSV. Earlier successful CSVs retain their existing independent commit behavior. This does not create partially committed chunks or mark an unfinished CSV Processed.

Optional Azure environment settings (defaults work without adding them):

| Setting | Default | Allowed range |
|---|---|---|
| `EtlSqlBatchSize` | `1000` | 1–5000 rows per transaction insert call |
| `EtlSqlCommandTimeoutSeconds` | `300` | 1–300 seconds per transaction insert call |
| `EtlCsvTransactionTimeoutSeconds` | `1200` | 30–1200 seconds for the whole CSV transaction |

The worker raises the process System.Transactions ceiling before creating a CSV scope, so the twenty-minute default is not silently shortened to the runtime's ten-minute ceiling. Updates apply to new scopes, not transactions already running. A saturated database can still time out; these settings do not provide unlimited retries or additional SQL capacity. No database schema/stored-procedure changes are needed. Deploy this main-branch build to apply the change; existing retry inputs are picked up automatically. Explicit Azure environment values override defaults: use `EtlSqlCommandTimeoutSeconds=300` and `EtlCsvTransactionTimeoutSeconds=1200` if those settings already exist. Deploy after the current invocation completes to avoid interrupting its transaction.

An opt-in `--large-csv-sql-check --fixture <archive.zip>` mode in the regression test project checks the actual PSL fixture and the configured database's transaction procedure conversion body against session-local temporary tables. It verifies later-batch SQL failure rollback, successful row counts/volumes/site/archive references and the atomic checkpoint. It does not write application tables, alter persistent schema, publish archives or send email. Its heap-table timing is diagnostic and does not predict production indexed-table throughput.

Verification on 4 November 2026: 96 offline regressions passed. The original `60000020260514171241.zip` fixture inserted all 572,165 source rows in 573 batches into temporary SQL tables, verified 2,675,973.800000 litres, correct site/archive references and its committed checkpoint. SQL insertion plus verification took 177.8 seconds. An injected SQL failure after an earlier successful batch left zero rows and zero completion checkpoints. These checks did not replay or alter the live archive. Deployment is still required.

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

Verification history: 57 offline regression scenarios passed, including the site hierarchy, `/Processed` routing, legacy checkpoint compatibility and HTTP user provisioning. A live temporary-user check passed for site access, sibling/CON isolation and duplicate rejection; the temporary user and empty site directories were removed afterward. Earlier read-only checks on `FintechStagDB` passed for schema, transaction enlistment and mapping load; simulator site `600005` had no active mapping there. Those results do not validate the current `FintechStagDB_NewArchitecture` database. No site mappings were changed. The CON directories were created and verified in Azure. Both ignored private settings files now select `FintechStagDB_NewArchitecture`; the CON login remains separate. Build/publish validation is recorded in [PARITY.md](PARITY.md).

On October 23, 2026, both ignored private settings files and the example were updated from `FintechStressTesting` to `FintechStagDB_NewArchitecture`. Apply the updated `FintechSqlConnectionString` in Azure to select it there. This configuration-only change does not require rebuilding or redeploying the application. The new database has not been checked or modified by this settings update.

On October 21, 2026, the requested database was changed back to `FintechStressTesting`, then the fixed database-name guard was removed at the user's request. Deploy this updated build once. Future database switches require only updating the Azure Function App environment variable `FintechSqlConnectionString` (`Initial Catalog=<database>` or `Database=<database>`) and applying the settings so the app restarts. No new build or separate database setting is required. The selected database must already contain the ETL tables, procedures, tracking columns and site mappings; live runs retain the existing schema validation. The scan log reports the configured database name. `ApplicationSettings__AzureSql` is not read by this ETL; use `FintechSqlConnectionString`. A connection-string change also switches database-backed checkpoints and provisioning locks to the selected database; checkpoint records from another database are not migrated. Switch after the active ETL run finishes so the restart does not interrupt processing. The optional index script is run manually in the intended application database and is not run automatically.

The initial CON SFTP preparation attempt failed because Azure returned **SSH/SFTP are not enabled for this account**. The separate CON permission grant succeeded after MFA sign-in. Subsequently, the user requested SFTP enablement; Azure confirmed `isSftpEnabled=true`, and both container connections passed read-only directory checks. No ETL run, deployment or database schema changes were initiated as part of these checks. The currently deployed Azure application settings were not changed; deploy the build and apply the updated private settings export when ready.

Production sign-off still requires a controlled end-to-end test of uploads/routing/regeneration, the MagLink source path if needed, and normal capacity/alert verification. Database selection follows `FintechSqlConnectionString`; secondary-database writes remain disabled.


## ETL error email alerts (ACS)

After long SQL work, the worker now probes the SFTP subsystem before archive-folder checks, publication and input removal. `IsConnected` alone is insufficient when SSH remains connected but its SFTP channel has closed. Transient failures disconnect and reconnect with up to three attempts and short backoff. If recovery still fails, input remains for the next scan and committed CSV checkpoints are retained. A disconnect after the probe is also retryable; archive rename ambiguity handling remains unchanged. SSH.NET's specific `The session is not open.` exception is log-only; unrelated application exceptions and authentication/permission failures still notify. No archive copy/move is blindly replayed as a group, so recovery does not bypass publication ordering or delete an unpublished input.

The ETL timer sends an individual dark, table-based HTML error notification with a plain-text alternative. It includes event counts, site ID (leading zeros preserved), input path, routing/retry disposition, UTC and Pakistan timestamps, invocation ID, configured SQL database, SFTP host and redacted exception/stack details. Per-CSV failures are included for `Partial` / `Failed` archives; the full errors also remain in Function logs.

| Case | Email behavior |
|---|---|
| Permanent timer/configuration/SQL schema failure | InvocationFailure; original invocation still fails; transient failures are log-only |
| Site connection/scan/lease failure | Routine temporary failures are log-only; credential/configuration failures notify |
| File download/SQL/CON delivery/publication failure retained for retry | Routine retries are log-only. Known permanent permission/configuration failures notify as ProcessingRequiresIntervention. |
| File moved to ExceptionFile / MagLinkException | FileProcessingFailure or Failed / Partial; includes failing CSV diagnostics where available |
| Oversized input moved to exception | OversizedInput |
| Invalid archive name moved to invalidFile | InvalidFilename; failed move is ArchiveMoveFailure |
| Notconfigured / MagLinkNotConfigured | Notify only if the destination site folder did **not** exist before publication. Existing folder suppresses email, **even if empty**. Successful publication creates the folder, suppressing later arrivals. Removing the folder allows another first-arrival notification. |
| Successful files, duplicate skips, busy-site lease skips, minimum-age skips | No email |
| Dry runs, host-requested cancellation, temporary-directory cleanup warnings | No email |

Every actionable permanent failure event is queued immediately, with **no 15-minute cooldown and no digest batching**. Two background persistence workers write separate durable event blobs, and two background ACS senders send one email per event. A `Partial` archive produces one email containing its failed CSV diagnostics, rather than one per failed CSV. Routine retries do not email. Files routed to exception or Partial outputs still notify immediately; another explicit replay ending in a permanent failure can notify again. Normal successful file processing does not wait for mail delivery. Sending is queued, so ACS throughput, network delays and backlog still affect delivery time; "immediate" means no intentional batching delay, not guaranteed instant inbox delivery.

The in-memory ingress queue holds up to 1,024 events and applies backpressure rather than silently dropping errors if it fills. The sender-ready queue holds 256 references; overflow stays in Blob Storage for discovery. Each event stores up to 6,000 diagnostic characters (first 20 failed CSV diagnostics collected per archive); full diagnostics remain in Function logs. Pending blobs in `fintech-etl-alerts/<app-and-slot-hash>/events/` under **AzureWebJobsStorage** are deleted after ACS acceptance. A finite per-event lease avoids simultaneous sends by multiple instances. Failed sends are retained and rediscovered on invocation startup and every minute during a long run. A short healthy invocation also performs startup recovery. On shutdown, queued events are persisted and senders receive a 30-second drain budget; unfinished persisted sends resume later.

Storage persistence is attempted three times. If all attempts fail, the loss of durable email storage is explicitly logged; use Function logs for that event. Delivery is at least once: a timeout or crash after ACS acceptance but before deleting the event can cause a repeated email. ACS `Succeeded` means accepted/out for delivery, not proof of inbox delivery. A stopped/crashed host cannot send its own alert; Azure Monitor availability alerts are needed for that separate case. This implementation does not read the obsolete `EtlErrorEmailCooldownMinutes` setting; remove it from Azure if previously added.

Add these settings to the **ETL** Function App environment (not the AutoSite app):

- `ACS_EMAIL_CONNECTION_STRING`: ACS connection string (secret).
- `EMAIL_FROM`: `fintechfuel@octopusdtl.com`.
- `EMAIL_RECIPIENTS`: `kamran.khalid@octopusdtl.com,khurram.mir@octopusdtl.com,ujaved@avanceon.ae,usman.tariq@octopusdtl.com,ameer.hamza@octopusdtl.com`.
- `EtlErrorEmailEnabled`: `true` (set `false` to disable).

`AzureWebJobsStorage` must be an account-key connection string with permission to create/write the private alert container and acquire blob leases. Alerts do not require changes to the ETL SQL schema. The existing ignored `local.settings.json` and `azure-app-settings.local.json` are populated locally. **`etl-email-app-settings.local.json` contains only the four email settings**, so merge those into the current Azure environment without replacing database or other ETL settings. It is ignored by Git and excluded from publishing; secrets are not embedded in code or packages.

To preview, build and run the DLL with `--preview-error-email`; it writes `artifacts/etl-error-email/Example-Email.html`. `--test-error-email` uses the same renderer and sends synthetic examples **only to usman.tariq@octopusdtl.com**, regardless of the production recipient setting. Neither command processes files or writes ETL SQL. `tests/FintechEtl.RegressionTests.csproj -- --email-outbox-check` checks real Blob outbox leasing, immediate individual sends, persistence and recovery with simulated send callbacks and removes its own test blob; it sends no email.


### Permanent failures only — 4 November 2026

Routine FileRetry, transient SQL timeouts/deadlocks/throttling, temporary I/O/SFTP disconnections and transient invocation/site-scan failures remain in Function logs without email. Old pending FileRetry notification blobs are discarded without mailing when recovered. Permanent file validation failures and Partial/Failed exception outputs still notify immediately. The existing first-notconfigured-folder notification remains enabled. Known authentication, permission and SQL configuration failures also notify because operator intervention is required.

SQL truncation (2628/8152), numeric/date conversion/overflow and required-field/foreign-key data failures are now nonretryable for the affected CSV. Earlier successfully committed CSVs remain committed and the archive is classified Partial when applicable. Unknown SQL errors remain retryable conservatively to avoid quarantining files because of an unrecognized infrastructure problem. No automatic alert escalation threshold is added for prolonged transient outages; use Function/Azure Monitor logs and availability monitoring. This change does not alter database column sizes.
