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

Verification history: 57 offline regression scenarios passed, including the site hierarchy, `/Processed` routing, legacy checkpoint compatibility and HTTP user provisioning. A live temporary-user check passed for site access, sibling/CON isolation and duplicate rejection; the temporary user and empty site directories were removed afterward. Earlier read-only checks on `FintechStagDB` passed for schema, transaction enlistment and mapping load; simulator site `600005` had no active mapping there. Those results do not validate the current `FintechStagDB_NewArchitecture` database. No site mappings were changed. The CON directories were created and verified in Azure. Both ignored private settings files now select `FintechStagDB_NewArchitecture`; the CON login remains separate. Build/publish validation is recorded in [PARITY.md](PARITY.md).

On October 23, 2026, both ignored private settings files and the example were updated from `FintechStressTesting` to `FintechStagDB_NewArchitecture`. Apply the updated `FintechSqlConnectionString` in Azure to select it there. This configuration-only change does not require rebuilding or redeploying the application. The new database has not been checked or modified by this settings update.

On October 21, 2026, the requested database was changed back to `FintechStressTesting`, then the fixed database-name guard was removed at the user's request. Deploy this updated build once. Future database switches require only updating the Azure Function App environment variable `FintechSqlConnectionString` (`Initial Catalog=<database>` or `Database=<database>`) and applying the settings so the app restarts. No new build or separate database setting is required. The selected database must already contain the ETL tables, procedures, tracking columns and site mappings; live runs retain the existing schema validation. The scan log reports the configured database name. `ApplicationSettings__AzureSql` is not read by this ETL; use `FintechSqlConnectionString`. A connection-string change also switches database-backed checkpoints and provisioning locks to the selected database; checkpoint records from another database are not migrated. Switch after the active ETL run finishes so the restart does not interrupt processing. The optional index script is run manually in the intended application database and is not run automatically.

The initial CON SFTP preparation attempt failed because Azure returned **SSH/SFTP are not enabled for this account**. The separate CON permission grant succeeded after MFA sign-in. Subsequently, the user requested SFTP enablement; Azure confirmed `isSftpEnabled=true`, and both container connections passed read-only directory checks. No ETL run, deployment or database schema changes were initiated as part of these checks. The currently deployed Azure application settings were not changed; deploy the build and apply the updated private settings export when ready.

Production sign-off still requires a controlled end-to-end test of uploads/routing/regeneration, the MagLink source path if needed, and normal capacity/alert verification. Database selection follows `FintechSqlConnectionString`; secondary-database writes remain disabled.
