# Alson ETL (staging only)

Separate `AlsonEtlTimer`, once per minute. It is disabled unless `AlsonEtlEnabled=true`.
No deployment or Azure setting change is performed by the local runner/setup scripts.
Dover settings and processing paths are unchanged. Neither raw target schema is altered.

## Staging configuration created 11 November 2026

Customer 227 (PARCO GUNVOR): NEW Makkah Site_ID 1191 / FC_SiteID 560257,
NEW Manzoor Site_ID 1192 / FC_SiteID 560256. Existing inactive Makka FS and test site 1190 are untouched.

* Makkah DSP 81C3C8CBD19, ATG 81C3C8CD7C3; probes 24696,24697,24699,83939.
* Manzoor DSP 81C3C8CE581, ATG 81C3C8CD824; probes 00002,00004,00007,00009.
* Each observed raw dispenser channel has a separate pump asset and nozzle-1 asset.
  Numeric FC_PumpID keys 1..12 are explicitly allocated in the setup script, not hex conversion.
  `0@ -> 1`, `0A -> 2` agrees with the two pilot screen mappings. Other channel keys are
  local numeric identifiers, not a claim about physical dispenser grouping (pilot counts 4/5 units).
* Each probe is configured as its own tank; raw probe text is preserved in Alson_Fintech_ProbeConfig.
  FC_TankID uses the source numeric tank/probe code, while the generated asset ID is separate.
* Existing product rows reused: R -> grade 1 Hi Super; D -> grade 2 Diesel; H -> grade 3 Excellium.
* User confirmed fuel -> Product_Volume and water -> Water_Volume. Raw quantities map directly,
  with scale 1. Product_Height and Water_Height remain NULL. A configurable raw-value ceiling
  of 1,000,000 rejects obvious extreme readings; this is not a declared tank capacity.
* Probe product/capacity, physical dispenser grouping and nozzle-to-tank links are not established
  by pilot cfg. They remain NULL/unassigned. Sales can load without inventing those links.
  Complete them before accepting product-based stock/reconciliation and physical inventory views.
* Totalizer remains in raw audit; Final_Volume/Initial_Volume stay NULL until semantics confirmed.
* Unknown source fields remain NULL; target IsSampled uses its existing default 0.
* Existing IP column is left blank for the new masters, not dropped or populated with a fake IP.
  No existing configuration API/UI dependency is changed. Its old save flow requiring IP still
  needs a coordinated update before users edit these masters through that flow.

## Required settings

See `local.settings.example.json`. Use a separate `AlsonSqlConnectionString`; the loader refuses
any database other than FintechStagDB_NewArchitecture. It never falls back to Dover's connection.
`AlsonSourceNotBefore` is an explicit source-date lower bound. The prepared local/deployment
settings use 2026-11-06. Review this bound before processing the historical inbox.

Private ignored files: `alson.local.json` for the CLI, `alson-app-settings.local.json` for the
user's Azure appsettings import. The deployment file defaults to disabled + dry-run.
It references existing ACS sender/connection settings and uses AlsonEmailRecipients separately.
Do not commit, paste or bundle these credential-filled files. `.funcignore` excludes them.

## Dedup, retention and retry

Read final JSON under sftp-data/Alson/inbox; do not move/delete any blob. Skip known control/
connection filenames without loading them. Older PY-* filenames are parsed to identify type.
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

## Rollback

Configuration batch 51662362-1009-453e-a08a-4e5bc183b38e has a full snapshot manifest in
`artifacts/alson/setup/` and in dbo.Alson_Fintech_Batches. Each imported row has an exact snapshot
and target ID recorded in Alson_Fintech_Rows. Review/verify without changing rows:

```powershell
.\deployment\Revert-AlsonStaging.ps1 -ManifestPath .\artifacts\alson\setup\51662362-1009-453e-a08a-4e5bc183b38e.json -VerifyOnly
```

Stop/disable Alson first for an actual rollback, then pass -Apply. Add -RemoveSchema only to
remove the six additive metadata tables too, after verifying no independent Alson work remains.
Rollback checks original configuration and target snapshots and refuses changed, missing,
archived or untracked rows. It deletes only linked imported identities and recorded new
configuration IDs. It never resets identity counters or deletes old sites/rows. If later portal
work or reporting has created dependent data, review that dependency before rollback; this is
not a general restore of arbitrary future database edits.

## Portal acceptance still needed

An existing duplicate-check procedure uses site+sale number and can flag valid Alson sales.
An older hourly procedure excludes NULL Sale_Type. Their active callers/schedule must be
checked before rollout. Raw ingestion alone does not complete reconciliation or all portal
views while product/tank and nozzle/tank configuration is incomplete. No shared reporting job
or stored procedure is changed/run by this implementation.
