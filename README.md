# Auto site configuration Azure Function

The .NET 10 isolated Function runs every five minutes (`0 */5 * * * *`) and uses the original auto site configuration parser and SQL logic from `FintechAutoSiteConfiguration` at commit `fe0c7e7`. `RunOnStartup=false`, `UseMonitor=true`; each invocation completes one scan.

## Input and routing

Connect to `fintechsftpdata.blob.core.windows.net` using the CON-specific username `fintechsftpdata.con-files.sftpadmin`. This selects the `con-files` container. The Function reads `/Unprocessed/*.csv` through SFTP. It downloads temporary parser inputs, then renames the original remote file into the appropriate folder in that same container. No Azure Files mount is needed.

| Result | Container path |
| --- | --- |
| Successfully configured/activated | `/Processed/<FC site ID>/<filename>` |
| Configuration update for an active site | `/ConfigurationUpdate/<FC site ID>/<filename>` |
| Invalid file or processing error | `/Exception/<FC site ID>/<filename>` plus a `.txt` error sidecar |
| Older file after the site's newest file is processed | `/Discarded/<FC site ID>/<filename>` |
| Duplicate route, if requested by the original helper | `/Duplicate/<FC site ID>/<filename>` |

Approved reconfiguration reads the newest CSV in `/ConfigurationUpdate/<FC site ID>` using the original portal-status rules. Unknown/unapproved sites retain their input for a later scan. The input directory must exist; routing directories are created on demand. Folder names are case-sensitive. ETL should deliver CON CSVs to `/Unprocessed` using its own `ConSftpDestinations` setting.

The CON SFTP user needs list/read, create/write and rename/delete permissions in this container. SHA256 host-key verification is required. Do not run the original console scanner against the same files after cutover.

Existing output filenames are preserved; collisions receive a unique suffix. Files changed during download or before routing are retained. A transport failure aborts the scan and is excluded from the parser's data-error cleanup, preventing a storage outage from clearing a site's SQL configuration. There is no cross-service transaction between SQL and remote routing: interruption after SQL commits can replay processing and notifications. Filenames must be immutable and producers should upload to temporary names before renaming to `.csv`.

Error sidecars are explicitly created with create-new semantics before subsequent
appends. Azure SFTP rejects append-only opens for missing blobs; that previously
aborted a scan after moving its first failed CSV to Exception and left later inputs
pending. Each run logs the selected SQL database, and unknown/ineligible site IDs
are reported explicitly while their inputs remain available for a future scan.

## Private Azure settings

The auto-site Function and main ETL are separate applications: changing the main
ETL's database setting does not update this Function. Set
`ApplicationSettings__AzureSql` to the same intended database as the main ETL's
`FintechSqlConnectionString`. The database supplies site identity, configuration
permission and activation state; the same FC site ID can have different mappings
and flags in another database.

On October 30, 2026, the deployed `Fintech-Stag-AutoSiteConfig` connection was
corrected from `FintechStagDB` to `FintechStagDB_NewArchitecture`, matching the main
ETL and the portal's site data. The old database marked FC site `333446` active
(Omni site `1005`), causing the expected active-site ConfigurationUpdate route,
and did not contain FC site `2008`, leaving its inputs pending. In the intended
database those sites map to `1180` and `1181`, respectively, with configuration
allowed and activation disabled. No parser routing changes are required for that
database mismatch. Activation flags must continue to come from the intended
database, rather than being overridden to make a file process.

`azure-app-settings.local.json` contains the selected real settings in Azure Portal Advanced edit format: an array of `name`, `value`, `slotSetting` entries. It is Git-ignored and excluded from build output, publish output and the ZIP. Paste its contents into the new Function App's environment variables. It includes the supplied host storage/monitoring settings, CON SFTP credentials, the target SQL connection, remote folder paths, and the existing main application's email settings. Credentials are not included in this README or the example templates.

The original `FintechSqlConnectionString` value is mapped to `ApplicationSettings__AzureSql`, which the original SQL service uses. `SftpUsername`, `EtlDryRun`, `EtlWorkerCount` and `ConSftpDestinations` are omitted: they belong to the ETL application. `PollingIntervalInMinutes` is unnecessary because the Function timer controls scheduling.

| Setting | Purpose |
| --- | --- |
| `AzureWebJobsStorage` | Function host storage and timer locking, separate from the CON data account |
| `FUNCTIONS_WORKER_RUNTIME` / `FUNCTIONS_EXTENSION_VERSION` | `dotnet-isolated` / `~4` |
| `WEBSITE_RUN_FROM_PACKAGE` | `1` for Dedicated/Premium ZIP deployment |
| `SftpHost`, `SftpPort`, `SftpPassword` | CON SFTP server credentials |
| `ConSftpUsername` | Container-specific SFTP user; the ETL data-container username is not used |
| `SftpHostKeySha256` | Verified SHA256 fingerprints, separated by semicolons |
| `ApplicationSettings__AzureSql` | Target SQL connection |
| `ApplicationSettings__UnprocessedFolder` | `/Unprocessed` |
| `ApplicationSettings__ProcessedFolder` | `/Processed` |
| `ApplicationSettings__ExceptionFolder` | `/Exception` |
| `ApplicationSettings__DuplicateFolder` | `/Duplicate` |
| `ApplicationSettings__ConfigurationUpdateFolder` | `/ConfigurationUpdate` |
| `ApplicationSettings__DiscardedFolder` | `/Discarded` |
| `ApplicationSettings__EmailRecipients` | Comma-separated recipients from the original application |
| `AutoSiteEmailConnectionString` / `AutoSiteEmailSender` | Original email service connection and verified sender |

`local.settings.json` has the same data and host settings, including the supplied Azure host storage account. It is also private and excluded from publication. Local `func start` performs real processing; it was not run against these settings. Sanitized examples are supplied for future checkouts.

## Build, verify and deploy

Use Functions v4 with .NET 10 isolated on a Dedicated or Elastic Premium plan. Enable Always On on Dedicated. `host.json` retains the existing ETL Function App's one-day timeout. Open this folder in VS Code for the configured Azure Functions deploy task, or run:

```powershell
.\scripts\publish.ps1
& "$env:USERPROFILE\.dotnet10\dotnet.exe" run --project tests\FintechAutoSiteConfiguration.RegressionTests.csproj --configuration Release
```

The publish script finds .NET on PATH or in this machine's `.dotnet10` folder. It validates the generated timer metadata and private-settings exclusion, then creates `artifacts/fintech-autosite-functionapp.zip` with host metadata at the root, including hidden host dependencies.

For a read-only SFTP check that never resolves the parser or SQL service:

```powershell
& "$env:USERPROFILE\.dotnet10\dotnet.exe" bin\Release\net10.0\FintechAutoSiteConfiguration.FunctionApp.dll --list-input
```

After setting the new Function App's runtime and pasting the private settings JSON into its environment variables, deploy the ZIP through VS Code or:

```powershell
az functionapp deployment source config-zip --resource-group YOUR_RESOURCE_GROUP --name YOUR_FUNCTION_APP --src artifacts/fintech-autosite-functionapp.zip
```

No Azure resources were changed or deployed during preparation.

## Source parity and validation

`SOURCE-PARITY.json` records hashes for all 17 copied C# files: 14 remain byte-identical, including SQL, mappings, helper parsing logic and models. The three adaptations are:

- `ParserService`: one scan per timer invocation, injected file discovery/routing, and storage exceptions excluded from data-error cleanup.
- `FileHelperService`: injected SFTP routing and remote exception sidecars; fallback logs use writable temporary storage.
- `EmailService`: embedded credentials moved to environment settings, preserving recipients and failure handling.

The replacement entry point and timer wire scoped services. New `Storage` files implement the SFTP adapter. Application dependency versions are retained; the transitive PKCS package is pinned to patched `9.0.21`. Nine nullable-reference compiler warnings originate in the copied code.

Twelve offline storage regression scenarios passed, including Azure-compatible error-log creation/appending, all output routes, approved requeueing, collisions, unstable input, transport failures, lost acknowledgements, traversal rejection and temporary cleanup. Build/publish, source parity and ZIP exclusion checks are recorded in `artifacts/verification.json`. No live SQL processing, email delivery or remote file writes were performed.

The read-only SFTP check on October 19, 2026 reached the supplied account but Azure rejected it with **SSH/SFTP are not enabled for this account**. SFTP must be enabled on `fintechsftpdata` before the Function can access the CON container. Its folder contents and live end-to-end processing could not be verified through SFTP.

References: [Azure SFTP connection usernames](https://learn.microsoft.com/azure/storage/blobs/secure-file-transfer-protocol-support-connect), [timer behavior](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-timer), [isolated runtime support](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide).
