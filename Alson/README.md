# Alson ETL (staging only)

Separate `AlsonEtlTimer`, once per minute. It is disabled unless `AlsonEtlEnabled=true`.
The local runner/setup scripts do not deploy. On 11 November 2026, the reviewed ZIP was
deployed to func-fintech-ETL-stag at the user's request. The live host reported Running,
AlsonEtlTimer disabled, and both existing functions enabled. Pre-existing non-Alson settings
were verified unchanged. Original deployed code/settings are backed up under artifacts/alson/deployment.
Dover settings and processing paths are unchanged. Neither raw target schema is altered.
Updated deployment verified 12 November 2026: active package matched the reviewed worker DLL,
host Running, Alson disabled, existing functions enabled, application settings unchanged.
130 offline regression scenarios, live finance/diagnostic checks, and eight real diagnostic
files (ten events) passed. The 100 sales and 100 tank readings remain; archive bytes verified.

## Staging configuration created 11 November 2026

Customer 227 (PARCO GUNVOR): NEW Makkah Site_ID 1191 / FC_SiteID 560257,
NEW Manzoor Site_ID 1192 / FC_SiteID 560256. Existing inactive Makka FS and test site 1190 are untouched.

* Makkah DSP 81C3C8CBD19, ATG 81C3C8CD7C3; physical tanks 1–4 map to probes
  24696,24699,24697,83939 in fixed payload order. Products Hi Super/Hi Super/Hi Super/Diesel;
  capacities 22423/22423/13365/13365 litres, from the Makkah onboarding form.
* Manzoor DSP 81C3C8CE581, ATG 81C3C8CD824; tanks 1–4 map to probes
  00002,00009,00004,00007. Products Hi Super/Excellium/Hi Super/Diesel;
  capacities 31259/13401/13401/13401 litres, from the Manzoor onboarding form.
* 24 nozzle/channel mappings now link to their physical tanks. Explicit channel lookup preserves
  raw identifiers: 0@→1, 0A→2, …, 0K→12, 0P→17, 0Q→18. No hex guessing.
  Physical dispenser-unit grouping remains unconfirmed; the channel assets are not unit groupings.
* ProductMap is site-specific: (CustomerID,OmniSiteID,RawCode). Makkah H=1 Excellium,
  D=2 Diesel,R=3 Hi Super; Manzoor H=1 Excellium,R=2 Hi Super,D=3 Diesel.
  Shared customer products remain canonical 1 Hi Super,2 Diesel,3 Excellium; ProductRowID
  links to their unchanged rows. Raw Grade_ID and ATG Product_Number use the site's Alson grade.
  TankMapping.ProdID_FK remains canonical. Dashboard/API changes are separate follow-up.
* Fuel→Product_Volume and water→Water_Volume, scale 1. Heights remain NULL.
  Probe configuration now bounds volumes by actual tank capacity. A changed fixed probe
  position or sale/tank product mismatch is held as pending_mapping for review.
* User confirmed totalizer -> Final_Volume on 11 November. Initial_Volume remains NULL.
  The existing 100 transactions were backfilled from their audited raw frames after snapshot checks.
* Unknown source fields remain NULL; target IsSampled uses its existing default 0.
* Existing IP column is left blank for the new masters, not dropped or populated with a fake IP.
  No existing configuration API/UI dependency is changed. Its old save flow requiring IP still
  needs a coordinated update before users edit these masters through that flow.

## Required settings

See `local.settings.example.json`. Use a separate `AlsonSqlConnectionString`; the loader refuses
any database other than FintechStagDB_NewArchitecture. It never falls back to Dover's connection.
`AlsonSourceNotBefore` is an explicit source-date lower bound. The prepared local/deployment
settings use 2026-11-06. Review this bound before processing the historical inbox.
The live deployment also sets AzureWebJobs.AlsonEtlTimer.Disabled=true. To activate later,
set that host flag to false AND AlsonEtlEnabled=true / AlsonEtlDryRun=false.

Private ignored files: `alson.local.json` for the CLI, `alson-app-settings.local.json` for the
user's Azure appsettings import. The deployment file defaults to disabled + dry-run.
It references existing ACS sender/connection settings and uses AlsonEmailRecipients separately.
Do not commit, paste or bundle these credential-filled files. `.funcignore` excludes them.

## Dedup, retention and retry

Read final JSON under sftp-data/Alson/inbox. After a successful SQL commit (or confirmed
duplicate), move the exact original JSON to sftp-data/Alson/Processed with its filename intact.
Upload only if the destination is absent, verify the complete envelope SHA-256, then delete
the inbox source conditionally on its verified ETag. Never overwrite a conflicting destination.
An interrupted move resumes from the successful SQL audit without reinserting business rows.
Dry-runs do not move files. Invalid, excluded, pending-mapping and unsupported inputs remain
in inbox. The telemetry archive is separate from Alson/outbox/Processed price-command results.
Commands 31 (auto/manual), 70 (DSP connection), 13 (ATG connection), and 27 (probe status)
are retained in separate diagnostic event tables, related through master mapping/site and
optional pump/tank mapping. Older PY-* filenames are also parsed. Other controls stay unsupported.
Validate raw bytes/checksum, strict dates and corrected ATG field order. Exact raw-frame hash
dedup is independent from the pilot database and target Sale_ID. Different dispensers with the
same Sale_ID are valid. All target rows, raw frame, row snapshots and source audit commit in one
SQL transaction. Same blob with altered bytes fails and keeps its original audit.

Unsupported/out-of-scope inputs have no business rows. Invalid source envelopes are retained
in SQL audit with outcome invalid, not automatically replayed. Missing mapping is retryable.
Unexpected infrastructure/SQL errors abort the scan and remain retryable. No schema migration
is run by the timer; deploy the reviewed additive migration separately if needed.

The scanner has a 4-minute runtime budget and bounded candidates; one host will not execute
overlapping invocations of the same timer. SQL locks/hash keys also serialize parallel replay.
Dry-run performs read-only configuration validation and no target identity consumption.

## Error emails

AlsonErrorEmailEnabled, AlsonEmailRecipients, existing ACS_EMAIL_CONNECTION_STRING and EMAIL_FROM.
Staging label/database are explicit, independent of Dover's EtlEnvironment. A dedicated
fintech-alson-alerts container holds retryable 15-minute digest buckets. Sent buckets retain a
marker to limit repeated alerts. A healthy invocation retries pending emails. Logs/SQL contain
the detailed per-file outcomes. No emails in dry-run or the local CLI. ACS acceptance followed
by a failure to save its sent marker can cause a later duplicate notification.

## Local bounded validation

Use the installed .NET 10 executable; .NET 8 cannot build this project. Functions Core Tools
is not needed for the explicit CLI. Run from the repository directory:

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" bin\Debug\net10.0\FintechEtl.FunctionApp.dll --alson-scan --sales 100 --atg-frames 25 --manifest artifacts\alson\preview.json
# After reviewing preview; inserts into STAGING only:
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" bin\Debug\net10.0\FintechEtl.FunctionApp.dll --alson-scan --sales 100 --atg-frames 25 --apply --manifest artifacts\alson\import.json
```

CLI is capped at 100 sales and 25 four-probe frames (=100 tank rows for the configured sources),
up to 50 sales and 13 ATG frames per master to exercise both sites. Its local time budget is 15
minutes; the deployed timer remains at 4. A partial batch exits with code 2 and reports actual
counts. It does not imply that the remaining quota was loaded. Re-running selects not-yet-
handled source files; it does not automatically replay exactly the same batch. Use recorded
source names for replay tests.

To archive previously committed files without ingesting any additional data:

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" bin\Release\net10.0\FintechEtl.FunctionApp.dll --alson-archive-processed --manifest artifacts\alson\archive-plan.json
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" bin\Release\net10.0\FintechEtl.FunctionApp.dll --alson-archive-processed --apply --manifest artifacts\alson\archive-move.json
```

This explicit catch-up command checks only successful/duplicate SQL audit entries for the
allowed masters. It does not insert/update SQL rows. Its manifest records original/destination
paths and envelope hashes for verification or later file restoration. SQL BlobName retains the
original inbox path for lineage; archived content is at the same suffix under Alson/Processed/.

## Rollback

Configuration batch 51662362-1009-453e-a08a-4e5bc183b38e has a full snapshot manifest in
`artifacts/alson/setup/` and in dbo.Alson_Fintech_Batches. Each imported row has an exact snapshot
and target ID recorded in Alson_Fintech_Rows. Review/verify without changing rows:

```powershell
.\deployment\Revert-AlsonStaging.ps1 -ManifestPath .\artifacts\alson\setup\51662362-1009-453e-a08a-4e5bc183b38e.json -VerifyOnly
```

Stop/disable Alson first for an actual rollback, then pass -Apply. Add -RemoveSchema only to
remove the eleven additive Alson tables and two diagnostic views too, after verifying no independent Alson work remains.
Rollback checks original configuration and target snapshots and refuses changed, missing,
archived or untracked rows. It deletes only linked imported identities and recorded new
configuration IDs. It never resets identity counters or deletes old sites/rows. If later portal
work or reporting has created dependent data, review that dependency before rollback; this is
not a general restore of arbitrary future database edits.

## Portal acceptance still needed

An existing duplicate-check procedure uses site+sale number and can flag valid Alson sales.
An older hourly procedure excludes NULL Sale_Type. Their active callers/schedule must be
checked before rollout. Raw ingestion alone does not complete reconciliation or all portal
views. Physical dispenser-unit grouping and portal acceptance remain outstanding. No shared reporting job
or stored procedure is changed/run by this implementation.

## Diagnostics added 12 November 2026

Apply Create-AlsonDiagnostics.sql before enabling the updated code. The additive tables are
Alson_Fintech_DiagnosticReceipts, Alson_Fintech_AutoManualEvents,
Alson_Fintech_DspConnectionEvents, Alson_Fintech_AtgConnectionEvents and
Alson_Fintech_ProbeStatusEvents. Raw status, raw bytes, source master and gateway receipt time
are preserved. STAT 0 means Connected; STAT 2 means Disconnected; other values stay unknown.
No timeout-based offline classification is applied. An old historical receipt is not evidence
of current connectivity. DspLastSeen and ProbeLastSeen views expose connection/mode versus data
timestamps without assigning timeout states. Missing gateway time remains NULL.

Diagnostic dedup uses master+command+messageId (source key when absent), so repeated Alive/con
frames with different receipt IDs are retained. A retried receipt is imported once. Reuse of
a message ID with changed raw bytes fails without replacing its original audit. Unknown channels
remain as raw events with NULL equipment mapping. These events never insert financial rows.
Successfully committed diagnostic files also move to Alson/Processed with exact bytes intact.

Read-only verification queries: deployment/Check-AlsonSites.sql. Local diagnostic scan requires
an explicit reviewed JSON filename list via --alson-diagnostics-scan --inputs <file>; preview
by default, --apply commits and archives. Local email sending remains disabled.
Onboarding snapshots and exact row rollback evidence: artifacts/alson/onboarding-20261112/.

