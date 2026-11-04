# Auto site configuration Azure Function

The .NET 10 isolated Function runs every five minutes (`0 */5 * * * *`) and is based on the original auto site configuration parser and SQL logic from `FintechAutoSiteConfiguration` at commit `fe0c7e7`. `RunOnStartup=false`, `UseMonitor=true`; each invocation completes one scan.

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

Existing output filenames are preserved; collisions receive a unique suffix. Files changed during download or before routing are retained. A transport failure aborts the scan and leaves input available for retry; neither storage errors nor validation errors clear a site's SQL configuration. There is no cross-service transaction between SQL and remote routing: interruption after SQL commits can replay processing and notifications. Filenames must be immutable and producers should upload to temporary names before renaming to `.csv`.

Error sidecars are explicitly created with create-new semantics before subsequent
appends. Azure SFTP rejects append-only opens for missing blobs; that previously
aborted a scan after moving its first failed CSV to Exception and left later inputs
pending. Each run logs the selected SQL database, and unknown/ineligible site IDs
are reported explicitly while their inputs remain available for a future scan.

## Safe reconfiguration

The Function validates the proposed configuration before deleting or creating any
assets: required grades, tanks and pumps, unique positive IDs, positive tank
capacities, nozzle IDs, hose counts, product/grade consistency and complete tank
and pump/nozzle mappings. Existing customer product name/ID checks remain in
force; this does not add aliases or bypass a product mismatch. Mapping builders
are exercised against an in-memory proposed asset set before SQL replacement.

Replacement uses a single SQL connection and transaction for every delete,
asset/area creation, mapping write and requested activation. The site row is
locked until commit so competing replacements serialize. Any failed step or
exception rolls back the whole replacement. Configuration cleanup is prohibited
outside that transaction and never deletes the existing `FintechMappedCustomers`
activation record. An already-active site's activation timestamp is retained.
The parser's error handler records the finding and routes the CON to Exception
with a `.txt` sidecar; it does not clear working configuration. Portal error status
and activation are separate: an Error status does not itself deactivate a site.

An approved `In Progress` update can now be processed while the site stays active.
An unapproved CON for an active site still goes to ConfigurationUpdate. If no
update is present yet, approval stays pending so a later file can be processed.
Malformed filenames also cannot reuse the previous input's site identity.

**Companion API limitation:** the current `FintechFuelAPI` implementation of
`FCMappingRepository.ReconfigureSite` sets `IsActive=false` before this Function
runs. That separate repository is unchanged. This Function preserves the
activation state it receives and cannot restore an activation already removed by
the API. To keep a site operational throughout the portal workflow, the API must
queue `In Progress` without deactivating it. Deploy this Function first, then that
API adjustment. Previously deleted configurations require a separately reviewed
recovery; this fix does not reconstruct them or reactivate sites automatically.

SQL and SFTP are not one transaction. A routing failure after SQL commit retains
the input for retry; it cannot undo a committed replacement.

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

### Reconfiguration verification

The default regression command runs 29 reconfiguration scenarios plus the 12
storage scenarios. It covers pre-write rejection, failures at each replacement
stage, preservation of an active site, activation of a new site, approval routing,
malformed XML/filenames, and the parser error path. SQL and email are faked in
these offline tests.

To verify actual SQL commit/rollback without modifying application tables, opt in
with private local settings configured:

```powershell
& "$env:USERPROFILE\.dotnet10\dotnet.exe" run --project tests\FintechAutoSiteConfiguration.RegressionTests.csproj --configuration Release -- --live-transaction-check
```

This check uses only a session-local temporary table, verifies a failed partial
replacement restores configuration and activation, and verifies a valid update
commits. It does not process any real CON or send email.


## Immediate AutoSite error emails

AutoSite error notifications use the same professional HTML layout as the ETL error emails, with a plain-text alternative. Each error event is queued immediately for its own email, without a digest or cooldown. Two persistence workers and two ACS senders run in the background. An error email includes the original remote CON path, FC site ID (including filename leading zeros), UTC/PKT occurrence time, invocation ID, SQL database, SFTP host, disposition and redacted diagnostic details. Available exception details include validation findings and stack/inner exceptions.

Covered cases:

- Invalid CON filename/site ID moved to `Exception/Invalid_FC_SITE_ID`.
- Malformed CON XML, mandatory-field/product/tank/pump/nozzle validation rejection, or configuration replacement/rollback failure moved to `Exception/<site>`.
- No eligible site metadata/mapping: input is retained and a MissingSiteMapping email is sent only once per FC site, database and app/slot. A durable notification marker suppresses later files, repeated scans and queued duplicate notifications. AutoSite has no business `notconfigured` folder; the marker is in private alert storage.
- Unexpected false replacement/status-update result.
- SQL, SFTP, input-discovery, exception-sidecar/publication and timer invocation failures. Fatal storage failures carry the current remote input/site to the invocation-level notification so they are reported once, rather than also sending the legacy error email.

Existing success and `ConfigurationUpdate` approval notifications retain their current behavior and recipient settings. Approved reconfiguration validation/transaction/rollback behavior and the five-minute timer schedule are unchanged.

Configure these four environment settings on the **AutoSite Function App**:

| Setting | Value |
|---|---|
| `ACS_EMAIL_CONNECTION_STRING` | Full ACS connection string; secret / Key Vault reference |
| `EMAIL_FROM` | `fintechfuel@octopusdtl.com` |
| `EMAIL_RECIPIENTS` | `kamran.khalid@octopusdtl.com,khurram.mir@octopusdtl.com,ujaved@avanceon.ae,usman.tariq@octopusdtl.com,ameer.hamza@octopusdtl.com` |
| `AutoSiteErrorEmailEnabled` | `true`; use `false` to disable only these error alerts |

For compatibility, missing standard ACS/sender/recipient settings fall back to existing `AutoSiteEmailConnectionString`, `AutoSiteEmailSender`, and `ApplicationSettings__EmailRecipients`. The ignored **autosite-email-app-settings.local.json** contains only the four alert settings, populated locally; merge those into the existing Azure environment rather than replacing unrelated settings. All `*.local.json` files are excluded from Git and deployment. No credential is embedded in code or packages.

Pending alerts use individual private blobs under `fintech-autosite-alerts/<app-and-slot-hash>/events/` in **AzureWebJobsStorage** (account-key connection string with create/write/lease permissions required). This is separate from the CON/data containers and does not change the SQL schema. After ACS acceptance the event blob is deleted. Failed sends remain for startup/one-minute recovery; a finite per-event lease prevents simultaneous delivery. The ingress queue is bounded to 1,024 events with backpressure during very large error storms. Ready references are bounded to 256; overflow stays durable for discovery. Full diagnostic logs remain in the Function and existing exception sidecars; each email event is limited to 6,000 diagnostic characters.

Storage persistence retries three times, then logs the failure explicitly. If the host dies before persistence, or storage stays unavailable, the email cannot be guaranteed. Shutdown persists queued events and allows 30 seconds to drain senders; pending stored events resume on a later invocation. Delivery is at least once: a timeout/crash between ACS acceptance and deletion can repeat an email. ACS `Succeeded` means accepted for delivery; network, backlog and ACS throughput affect actual arrival. A stopped host or startup validation failure before invocation requires a separate Azure Monitor availability alert.

Build then run the application DLL with `--preview-error-email` to write `artifacts/autosite-error-email/Example-Email.html`. `--test-error-email` sends a synthetic template test only to **usman.tariq@octopusdtl.com**; neither command reads SQL or processes CON files. The regression suite covers parser error routing, preserved activation/rollback, remote path context, duplicate-error suppression and credential-safe HTML. `--email-outbox-check` on the regression test project uses a private temporary Blob scope to test immediate individual sending and recovery with simulated send callbacks; no mail or business data writes occur, and its test blobs are removed.


### MissingSiteMapping notification suppression — 4 November 2026

The first successfully accepted MissingSiteMapping email creates a `Sent=true` marker under `fintech-autosite-alerts/<app-and-slot-hash>/notified/missing-site/`. Its key includes the configured database and canonical numeric FC ID, so `001057` and `1057` are the same site while a different target database is a separate notification scope. The displayed email retains the original FC ID. Markers persist across deployments and restarts. A per-marker lease prevents concurrent instances from both notifying. Within an invocation, repeated files for the same missing site are also coalesced before entering the outbox.

Notification failure leaves the marker unsent and its event pending, allowing a later successful email. Existing queued MissingSiteMapping events go through the same marker check; after one succeeds the remaining duplicates are deleted without email. Historical emails sent before this feature have no marker, so the first post-deployment notification may send once before future notifications are suppressed. No live markers were pre-seeded.

CON files remain in their existing input folders and every scan still checks current database metadata. No SQL mappings, activation state or business routing are changed by suppression. Once notified, the marker has no automatic expiry; the same site/database will not notify again unless its specific marker is explicitly reset. Other error categories keep their existing notification behavior. An ACS acceptance followed by a host/storage failure before writing the marker still has the usual at-least-once delivery limitation.


### Function-only recovery of a failed reconfiguration — 4 November 2026

The portal/API is unchanged. An approved reconfiguration (`IsConfigurationAllowed=true`, `AutoConfigurationStatus=In Progress`) may arrive with `FintechMappedCustomers.IsActive=0` because the portal deactivated it. Before reading/replacing the CON, AutoSite checks that the site/customer already has an activation row, assets, tank mappings and pump mappings.

On validation or replacement failure, the existing validation-before-write and transaction-rollback behavior preserves the old configuration. If that preexisting configuration was present, the Function sets **IsActive=1** on the existing activation row after rollback, keeps Auto Config Status **Error**, routes the failed CON to **Exception/<siteid>**, and includes the recovery outcome in the error email. It does not create an activation row/pointer or change activation timestamps as a side effect of this recovery. The UPDATE rechecks that the old configuration still exists.

This is the requested fallback policy for a failed reconfiguration with existing configuration; the Function cannot independently reconstruct the activation value before the portal request. A first-time site with no preserved configuration is never activated by this error path. An activation-recovery failure is logged and included in the error email rather than falsely claiming the site is active; normal exception publication is still attempted. No API changes or database-schema changes are required. Deploy the updated AutoSite Function for this behavior. Existing Error files are not automatically replayed or retroactively reactivated just by deployment.
