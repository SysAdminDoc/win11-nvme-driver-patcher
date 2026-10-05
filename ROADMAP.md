# Roadmap — win11-nvme-driver-patcher

Actionable work only. Historical and completed roadmap material is archived in CHANGELOG.md; blocked work is kept in Roadmap_Blocked.md.

## Audit Findings — 2026-08-10

Baseline at audit time: `dotnet build` clean (1 warning: xUnit2031 at `tests/NVMeDriverPatcher.Tests/ControlSetMirroringTests.cs:108`), 1166/1166 tests pass, `Validate-ReleaseVersions.ps1` / `Validate-DocumentationFacts.ps1` / `Validate-BuildRulesFreshness.ps1` all pass (rules stale in 21 days). No pre-existing failures.

### P2

### P3

- [ ] P3 — Preflight passes SafeBoot GUID keys that Windows write-protects
  Category: correctness
  Where: `CriticalEnvironmentProbeService.ProbeSafeBoot`, `SafeBootStateService.Classify`, `RealSafeBootRegistry.Read`
  Problem: AccessDenied is only detected when reading fails. On 24H2 26100.9550 the GUID keys are owned by TrustedInstaller and readable by everyone, with Windows' `NvmeDisk` in the default value, so preflight reports `ConflictingDefault` and passes while apply's write of `Storage Disks` would be refused even as SYSTEM.
  Evidence: 2026-10-05 VM run on 26100.9550: `reg delete` and a .NET write were refused as SYSTEM; preflight printed `Minimal=ConflictingDefault`, Pass. Apply is build-gated on every build known to ship these keys today, so nothing reaches the write yet.
  Fix: Probe writability (open with SetValue rights without writing) and classify a refused open as AccessDenied, or treat an existing TrustedInstaller-owned key as already registered and leave it out of the write set and the journal.
  Acceptance: Preflight on 26100.9550 names the keys as Windows-owned, and a forced apply there doesn't fail partway through on them.
  Confidence: Confirmed
  Effort: M

- [ ] P3 — Full profile's 156965516 makes DISM /ScanHealth report store corruption (#19)
  Category: product
  Where: `AppConfig.GetFeatureIDsForProfile(PatchProfile.Full)`
  Problem: On 24H2 26100.9550, override 156965516 alone made DISM flag 192 reverse-delta payloads as corrupt while it was set (SFC clean, cleared on removal). The Full profile writes it, and binding on that build needed 3244671118 + 1853569164 + 156965516 anyway, a set this tool doesn't write.
  Evidence: VM matrix 2026-10-05 (repo CLAUDE.md, vault log 2026-10). The warning now ships in the GUI, CLI help, docs and README.
  Fix: Decide whether Full keeps 156965516, makes it a separate opt-in, or drops it. Needs a reading on real hardware of what it does for performance.
  Acceptance: The Full profile either doesn't write 156965516 or asks for it separately with the DISM caveat attached.
  Confidence: Confirmed
  Effort: S

- [ ] P3 — CLI output loses non-ASCII characters when redirected
  Category: correctness
  Where: `src/NVMeDriverPatcher.Cli/Program.cs` startup; visible in `dry-run` ("Before → After") and every string with an em dash
  Problem: The CLI never sets `Console.OutputEncoding`, so redirected output is written in the OEM code page. Run as SYSTEM with stdout redirected on 26100.9550, `dry-run` printed "Before  After" with the arrow gone.
  Evidence: 2026-10-05 VM run.
  Fix: Write UTF-8 without BOM when stdout is redirected (check first that it doesn't switch the parent console's code page), or keep CLI text ASCII-only, which the em dash sweep of CLI strings wants anyway.
  Acceptance: `NVMeDriverPatcher.Cli dry-run > plan.md` produces a UTF-8 file with the arrow intact.
  Confidence: Confirmed
  Effort: S

- [ ] P3 — `DryRunService.PlanUninstall` has no caller and previews a whole-key delete
  Category: cleanup
  Where: `DryRunService.PlanUninstall`
  Problem: Nothing calls it. It lists any existing SafeBoot GUID key as `DELETE (subkey)` with `Storage Disks` as its value, the issue #13 pattern that removal no longer follows (journal restore, default-value-only fallback).
  Fix: Delete it, or rebuild it on `SafeBootStateService.ManagedKeysFor` and `PlanRestore` if an uninstall preview is wanted.
  Acceptance: No code path previews deleting a Windows-owned SafeBoot key.
  Confidence: Confirmed
  Effort: S

- [ ] P3 — NVMe Identify has never been seen working on a real drive; pass-through may be refused for Identify
  Category: correctness
  Where: `src/NVMeDriverPatcher.Core/Services/NvmeIdentifyService.cs` (`Query`); consumers are the CLI `identify` command, `ApstInspectorService` (Identify power states) and `DiagnosticsService`
  Problem: Until the protocol-status fix, the request used IOCTL code 0x2DD4C0 (function 0x530, not IOCTL_STORAGE_PROTOCOL_COMMAND) and left CommandSpecific at 0, so every Query most likely failed inside DeviceIoControl and callers quietly fell back. The corrected request (0x2DD3C0, NVMe admin CommandSpecific) is pinned against winioctl.h by tests but hasn't run on hardware. Microsoft's NVMe guide shows Identify going through IOCTL_STORAGE_QUERY_PROPERTY and describes protocol-command pass-through for vendor-specific commands, so stornvme/nvmedisk may still refuse this request.
  Evidence: The ReturnStatus gate now reports any refusal as a failure instead of an empty identity, so nothing breaks either way; the open question is whether Identify data ever arrives.
  Fix: From an elevated shell run `NVMeDriverPatcher.Cli identify` on a stornvme-bound and an nvmedisk-bound drive. If the request is refused (Win32 error or a non-success protocol status), switch `Query` to IOCTL_STORAGE_QUERY_PROPERTY with StorageAdapterProtocolSpecificProperty / NVMeDataTypeIdentify / CNS controller, which also works on a handle opened with no access rights.
  Acceptance: `identify` prints the real model, serial, firmware and VID on at least one drive per driver, and the APST inspector shows Identify power states.
  Confidence: Likely
  Effort: M

- [ ] P3 — WinRE `winre.wim` backups (0.5–1 GB each) accumulate unboundedly and no cleanup path knows about them
  Category: reliability
  Where: `src/NVMeDriverPatcher.Core/Services/WinReDriverInjectionService.cs:180-195` (writes `workingDir\backups\winre.wim.<stamp>.bak`); `src/NVMeDriverPatcher.Core/Services/CleanDataService.cs:22-79` (no target matches the `backups\` subdirectory)
  Problem: Each `winre-inject --apply` adds a timestamped multi-GB backup with no retention cap; even "purge everything" `CleanDataService.Clean` leaves them (its summary then under-reports what remains). Largest artifact the app writes, outside every retention mechanism.
  Evidence: Both services read; `CleanDataService` targets enumerated (logs/etl/db/bundles/staging/`Pre_*_Backup_*.reg` only).
  Fix: Keep the most recent N (2) WinRE backups with a prune in the injection service; add a `backups` target to `CleanDataService` that preserves the newest.
  Acceptance: Third `--apply` leaves ≤ 2 `.bak` files; `clean-data` reports and sweeps the directory.
  Confidence: Verified
  Effort: S

- [ ] P3 — Fixed-name `.tmp` sibling writes race across the four processes in five services; `SaveBaseline` additionally propagates unhandled
  Category: reliability
  Where: `src/NVMeDriverPatcher.Core/Services/AutoBenchmarkService.cs:49-57`; `BenchmarkService.cs:522-530`; `MaintenanceWindowService.cs:53-55`; `CompatTelemetryService.cs:147-149`; `TuningProfileIoService.cs:31-33`
  Problem: All use `path + ".tmp"` with exclusive create; concurrent GUI + SYSTEM scheduled-CLI writers collide — one update silently lost (most sites swallow the IOException), and `SaveBaseline` throws to its caller and can strand the `.tmp`. The correct pattern (PID+GUID temp + global mutex) exists in `ConfigDurabilityService`/`EventLogWatchdogService`. `benchmark_results.json` also does an unlocked read-modify-write that can drop a concurrent entry.
  Evidence: All five sites read; contrast pattern confirmed.
  Fix: Switch the five to PID+GUID temp names; wrap `benchmark_results.json` read-modify-write in the config mutex (or a dedicated one); add try/catch to `SaveBaseline` with a logged failure.
  Acceptance: Concurrency test (parallel writers) never loses both writes nor leaves `.tmp` residue.
  Confidence: Verified
  Effort: S

- [ ] P3 — `BypassIoHistory` is the only DB table with no prune; schema-upgrade DB backups also accumulate
  Category: reliability
  Where: `src/NVMeDriverPatcher.Core/Services/DataService.cs:379-406` (writer; prunes at `:304-377` cover Telemetry/Snapshots/Benchmarks only); `src/NVMeDriverPatcher/App.xaml.cs:84-86`; `AppDatabaseUpgradeService.BuildBackupPath` (`database-backups\*.db`, unbounded per upgrade)
  Problem: Documented retention design covers three of four tables; `BypassIoHistory` grows forever (low rate today — 2×volume-count rows per install/uninstall — but any future writer inherits the leak). Upgrade backups have no retention either.
  Evidence: Prune sites enumerated; startup prune calls read.
  Fix: Add a `PruneBypassIoHistory` (retain N latest per volume or M days) called with the other three; cap `database-backups` at the newest 3.
  Acceptance: Startup prune trims a seeded oversized `BypassIoHistory`; upgrade leaves ≤ 3 backups.
  Confidence: Verified
  Effort: S

- [ ] P3 — SafeBoot journal restore re-types non-REG_SZ defaults and expands REG_EXPAND_SZ, breaking the byte-for-byte claim
  Category: correctness
  Where: `src/NVMeDriverPatcher.Core/Services/SafeBootStateService.cs:325-330` (`Read` uses `key.GetValue(name)` — expands), `:362-365` (`ApplyRestore` always writes `RegistryValueKind.String`)
  Problem: A pre-existing REG_EXPAND_SZ (or other-kind) SafeBoot default is restored expanded and re-typed; the ledger's `SafeBootSnapshotsEqual` (`MutationLedgerService.cs:791-798` compares Kind) then flags a permanent baseline difference on every restore. Real-world incidence low (SafeBoot defaults are REG_SZ driver-group names).
  Evidence: Both methods read.
  Fix: Capture with `RegistryValueOptions.DoNotExpandEnvironmentNames`; restore with the recorded Kind.
  Acceptance: Round-trip test with a REG_EXPAND_SZ fixture default restores kind and raw data exactly.
  Confidence: Verified (mechanism)
  Effort: S

- [ ] P3 — `DetectBcdTestSigningEnabled` drains process pipes synchronously and sequentially with no effective timeout
  Category: reliability
  Where: `src/NVMeDriverPatcher.Core/Services/PreflightService.cs:671-679` (`ReadToEnd()` stdout then stderr before `WaitForExit(10_000)`)
  Problem: The exact hang shape the repo's own CLAUDE.md rule prohibits — a stderr-filling child deadlocks both processes and preflight hangs forever. Every other launcher in the safety path drains asynchronously. Practical trigger rare (bcdedit output small).
  Evidence: Site read; contrast with PatchService/BitLockerRecoveryService launchers.
  Fix: Use the async-drain + bounded-wait + kill-on-timeout helper the other services use.
  Acceptance: Code matches the async pattern; the bare-pattern grep in review finds no sync `ReadToEnd` before `WaitForExit` in `src/`.
  Confidence: Verified (pattern)
  Effort: S

- [ ] P3 — `MutationLedgerService.RestoreOriginalState` has no owner-active guard against a concurrent in-flight apply
  Category: reliability
  Where: `src/NVMeDriverPatcher.Core/Services/MutationLedgerService.cs:499-520` (restore), contrast `:189-195` (`Prepare` refuses on live owner)
  Problem: A second elevated process running `remove` (or fallback-failure recovery) restores the baseline while the owning process is between `PrepareRegistryPatch` and `CommitAll`, interleaving writes on boot-critical keys. End state converges (non-monotonic phase check refuses the writer's `MarkApplied`), but the interleaving window on SafeBoot/control-set keys is avoidable. (This is the still-live remnant of RESEARCH.md's `:26` process-lock claim.)
  Evidence: Both paths read.
  Fix: Check `IsOwnerActive` in `RestoreOriginalState`/`Uninstall` and refuse with a "another operation is in flight" error, or hold the ledger mutex across the Install write phase.
  Acceptance: Unit test with a fake live-owner ledger: restore refuses.
  Confidence: Verified (requires deliberate concurrent mutation)
  Effort: S

- [ ] P3 — GUI: large dead ViewModel surface still computed every refresh; user-facing features silently vanished in the redesign
  Category: maintainability
  Where: `src/NVMeDriverPatcher/ViewModels/`: `ReadinessChecks`/`LeftChecks`/`RightChecks` + `PreflightCheckVM` tooltips (`MainViewModel.cs:467-506`), `Drives`/`DriveRowVM` (`RowViewModels.cs:63-106`), `RegistryFlags`/`SafeBootFlags` (`:804-836`), `AttentionNotes` cluster (`:1089-1167`), `DirectStorageImpactText/Severity/PanelVisible` (`:156-159, 1046-1087`), `SkipWarnings` (no toggle anywhere yet described by `OptionsSummaryText`, `MainViewModel.Settings.cs:22-24`), `ChangePlanSteps`, `RiskSummaryColor`, `ActionReadinessText/Color` (bound only inside collapsed XAML)
  Problem: None of these are bound in any view (grep-verified); registry/WMI projections run on every refresh for nothing. Product decision folded in: the per-check readiness list, drive table with SMART/NATIVE-LEGACY badges, per-flag view, and the gaming-impact panel were real features whose only surfaces were removed.
  Evidence: Grep across `Views/` for each binding path.
  Fix: Decide per cluster: re-surface (drive table and readiness list have real value — pairs with the OverviewGrid item) or delete the VM code and its refresh cost. Remove the `SkipWarnings` sentence from `OptionsSummaryText` unless a toggle ships.
  Acceptance: Every remaining VM public member is bound somewhere; refresh no longer computes unbound projections.
  Confidence: Verified
  Effort: M

- [ ] P3 — GUI dead-code cluster from the redesign
  Category: maintainability
  Where: `Themes/DarkTheme.xaml:1078-1109` (`WorkspaceTabControl` unused); `Views/MainWindow.xaml.cs:213, 215` (`Minimize_Click`/`Close_Click` unreferenced); `MainWindow.xaml:289` (`MaximizeRestoreButton` permanently Collapsed while `UpdateWindowPresentation` still updates it); `UpdateAdaptiveLayout` (xaml.cs:422) unconditionally collapses `MainContentSplitter`; `Commands.cs:767-768` (`ToggleSettingsCommand`/`SettingsPanelVisible` unused); `App.xaml:12-13` (`SettingsToggle`, `StrToColor` converters unused)
  Problem: Orphaned styles/handlers/commands mislead maintenance and mask which features are actually reachable.
  Evidence: Grep per symbol.
  Fix: Delete each (restore the splitter only if the resize feature is wanted back).
  Acceptance: Repo-wide grep finds no unreferenced symbols from this list; build clean.
  Confidence: Verified
  Effort: S

- [ ] P3 — GUI synchronous I/O on the UI thread per refresh/tab switch
  Category: perf
  Where: `ViewModels/MainViewModel.Workspace.cs:12-187` (`UpdateOperationalHistory`: directory enumeration + three SQLite reads + registry read, run on tab switch, after every command, and inside the preflight render `Dispatcher.Invoke`); `MainViewModel.cs:565, 898` (`BenchmarkService.GetHistory` read twice per preflight)
  Problem: Jank on slow ProgramData disks; duplicated history read per preflight.
  Evidence: Call sites traced.
  Fix: Move `UpdateOperationalHistory` data gathering to a background task marshaling results back; cache the benchmark-history read within one refresh cycle.
  Acceptance: UI thread does no SQLite/directory I/O during tab switch (verify with a dispatcher-blocking assertion or profiler).
  Confidence: Likely (not profiled)
  Effort: M

- [ ] P3 — Core and CLI prose still writes "SafeBoot" as one word
  Category: docs
  Where: `src/NVMeDriverPatcher.Core/Services/PreflightService.cs:124` (shown in the GUI readiness list), `RecoveryProofGateService.cs:170-177` (label "SafeBoot entries", logged by the GUI fallback gate), `RecoverySafetyGateService.cs:71`, `DryRunService.cs:86-96, 184-185`, `PatchService.cs:85-106` (component names), `MutationLedgerService.cs:214, 556, 604-608`, `CriticalEnvironmentProbeService.cs:293-306`; `src/NVMeDriverPatcher.Cli/CliCommandRegistry.cs:131, 210`, `Program.cs:342`; README prose at `:118, :132, :242`
  Problem: The GUI and `SafeBootUpgradeService` now say "Safe Boot" in prose and keep `SafeBoot\Minimal` for literal registry paths, but these Core and CLI strings still glue the word together, and some of them surface in the GUI next to the corrected text.
  Evidence: `MicrocopyTests.UserFacingText_SpellsSafeBootAsTwoWords` scans only `src/NVMeDriverPatcher` plus `SafeBootUpgradeService.cs`; the listed strings are outside it.
  Fix: Rewrite each to "Safe Boot" (or to the literal `SafeBoot\...` path where it names a key), update the tests that assert the old labels (`RecoveryProofGateServiceTests.cs:117`, `RecoverySafetyGateServiceTests.cs:18-24`, `SafeBootRemovalAccessTests`), then widen the scan to `NVMeDriverPatcher.Core` and `NVMeDriverPatcher.Cli`.
  Acceptance: The widened scan passes; CLI help and README match the GUI spelling.
  Confidence: Verified
  Effort: S

- [ ] P3 — `verify-payload --json` bypasses the versioned `CliEnvelope`; `bypassio --json --history` silently drops the history diff
  Category: correctness
  Where: `src/NVMeDriverPatcher.Cli/Program.cs:248-266` (hand-serialized anonymous object, no `schemaVersion`/`command` wrapper); `:488-493` (returns current snapshot before the `showHistory` branch)
  Problem: Exactly one JSON command deviates from the documented envelope shape; and the JSON+history flag combination loses the pre/post diff that the text path prints.
  Evidence: Both sites read; contrast with `CliJson.Serialize` usage elsewhere.
  Fix: Route `verify-payload` through `CliJson.Serialize`; include the history diff in the bypassio JSON payload when `--history` is passed.
  Acceptance: `CliJsonTests` cover both (envelope fields present; history array populated).
  Confidence: Verified
  Effort: S

- [ ] P3 — dll-hosted runs register `dotnet.exe` as the persistent binary for scheduled tasks and the service
  Category: correctness
  Where: `src/NVMeDriverPatcher.Cli/Program.cs:704` (`register-tasks`), `src/NVMeDriverPatcher.Watchdog/Program.cs:71` (`/install`) — both use `Environment.ProcessPath`
  Problem: Under `dotnet NVMeDriverPatcher.Cli.dll` (the repo's own documented way to run the CLI non-elevated), `ProcessPath` is dotnet.exe, so BootVerify/WatchdogSweep tasks or the service get registered pointing at bare `dotnet.exe` with no dll argument — jobs that fail forever.
  Evidence: `ProcessPath` semantics + call sites read.
  Fix: Refuse registration when `ProcessPath` filename isn't the app exe, with a message naming the published exe to use.
  Acceptance: `dotnet ...Cli.dll register-tasks` exits non-zero with the guidance; exe-hosted registration unchanged.
  Confidence: Likely
  Effort: S

- [ ] P3 — Tray: machine-wide single-instance mutex, and synchronous WMI on the UI thread every 30 s
  Category: ux
  Where: `src/NVMeDriverPatcher.Tray/Program.cs:15, 26-27` (`Global\` mutex — second RDP/fast-user-switch session gets no icon, silent exit 0), `:60-64, 79-104` (WinForms timer runs `PatchVerificationService.Evaluate` incl. `TryGetLastBootTime` + `DriveService.TestNativeNVMeActive` WMI synchronously — menu stutters during each poll)
  Problem: Per-session agent behaves per-machine; periodic UI-thread stalls.
  Evidence: Both read.
  Fix: Switch to `Local\` mutex; move polling to a worker thread and marshal results back.
  Acceptance: Two sessions each get a tray icon; context menu stays responsive during polls.
  Confidence: Verified
  Effort: S

- [ ] P3 — Config downgrade warning from `ConfigMigrationService.Migrate` is never shown
  Category: reliability
  Where: `src/NVMeDriverPatcher.Cli/Program.cs` (the `migrationSummary` returned by `ConfigMigrationService.Migrate` is assigned and never used); `src/NVMeDriverPatcher.Core/Services/ConfigMigrationService.cs:21-28`; the GUI never calls `Migrate` at all
  Problem: When config.json carries a schema newer than this build (a downgrade, or an older CLI run against a newer GUI's config), `Migrate` leaves the file alone and returns a summary written so callers can warn. Nothing prints or logs it, so the user never learns that settings this build doesn't understand are being ignored.
  Evidence: Found while fixing the swallowed migration exception; the summary is the only output of the downgrade branch.
  Fix: When `config.ConfigVersion > ConfigMigrationService.CurrentSchemaVersion`, write the summary as a `[WARNING]` on stderr in the CLI and surface it in the GUI activity log (plus the event log once it's initialized).
  Acceptance: A config.json with `ConfigVersion` above the current schema produces one visible warning per run in the CLI and the GUI; a current or older config produces none.
  Confidence: Verified
  Effort: S

- [ ] P3 — CHANGELOG versions 5.4.0/5.5.0 have no git tags; 5.3.0 was released with no CHANGELOG entry; stray malformed tag `v.3.0.0`
  Category: docs
  Where: `CHANGELOG.md:35, 54` (5.5.0/5.4.0 entries); git tags (`v5.2.0` → `v5.6.0` jump, `v.3.0.0` typo tag); commit 95bbf11 "chore: release v5.3.0" with no `[5.3.0]` section
  Problem: A user cannot map CHANGELOG entries to downloadable releases; the malformed tag pollutes tag listings.
  Evidence: `git tag` + CHANGELOG headers + `git log` compared directly.
  Fix: Backfill tags `v5.4.0`/`v5.5.0` on their release commits (pattern: the RES-Slim v0.28/v0.29 backfill); add a brief `[5.3.0]` entry (content from commit 95bbf11's release); delete tag `v.3.0.0` (or document it). Note: tag pushes are release actions — do them in an implementation session, not this audit.
  Acceptance: Every `[x.y.z]` CHANGELOG section ≥ 5.0.0 has a matching `vx.y.z` tag and vice versa.
  Confidence: Verified
  Effort: S

- [ ] P3 — Repo hygiene: required release artifact is tracked-but-ignored; tracked ROADMAP.md links untracked Roadmap_Blocked.md; AGENTS.md tracking claim false
  Category: docs
  Where: `.gitignore:20` (`NVMe_Driver_Patcher.ps1` — tracked AND ignored; `release-artifacts.json` marks it required); `ROADMAP.md:3` (links `Roadmap_Blocked.md`, which is gitignored/untracked — dangling in clones); `AGENTS.md` ("README.md — the ONLY .md tracked in git" — CHANGELOG.md, RESEARCH.md, ROADMAP.md and four `packaging/**/README.md` are tracked)
  Problem: `git clean -fdX` deletes the required legacy release artifact; delete-then-re-add of it silently fails without `-f`; the blocked-items link 404s for anyone cloning; the AGENTS.md hygiene claim misleads agents.
  Evidence: `git check-ignore -v` + `git ls-files` runs (documented above).
  Fix: Add `!NVMe_Driver_Patcher.ps1` after the ignore block; either track Roadmap_Blocked.md or drop the link from tracked ROADMAP.md text; correct the AGENTS.md sentence.
  Acceptance: `git check-ignore NVMe_Driver_Patcher.ps1` exits 1; no tracked file links an untracked one; AGENTS.md matches `git ls-files '*.md'`.
  Confidence: Verified
  Effort: S

- [ ] P3 — Legacy script: Refresh runs preflight synchronously on the UI thread
  Category: perf
  Where: `NVMe_Driver_Patcher.ps1` `BtnRefresh` click handler (calls `Invoke-PreflightChecks`, `Get-NVMeHealthData` and `Get-StorageDiskMigration` inline, then repaints the check dots and labels); the background runspace and `DispatcherTimer` poll exist only in the SECTION 19 `Add_ContentRendered` startup handler, which carries its own copy of the same check-to-dot/label mapping
  Problem: Clicking Refresh freezes the window for the 5-20 s the DISM/CIM/fsutil probes take, the freeze the v3.4.6 background runspace removed from startup.
  Evidence: Handler read; every probe runs on the dispatcher thread.
  Fix: Extract the startup runspace launch, poll and result marshaling into one function (for example `Start-BackgroundPreflight` with a completion script block) and call it from both ContentRendered and BtnRefresh, sharing one repaint helper. Keep `$funcNames` complete; `LegacyScriptArtifactTests.BackgroundPreflightRunspace_CarriesEveryScriptFunctionItsFunctionsCall` gates it.
  Acceptance: Refresh keeps the window responsive; `Validate-LegacyPowerShellBoundary.ps1` and the legacy script tests pass.
  Confidence: Verified
  Effort: M

- [ ] P3 — `Validate-LegacyPowerShellBoundary.ps1` enumerates what it guards; `-Status` writes HKLM via `CreateEventSource` unnoticed
  Category: testing
  Where: `scripts/Validate-LegacyPowerShellBoundary.ps1:59-89` (missing: `reg.exe`/`reg add`/`regedit /s`, `Set-Item`, `Copy-ItemProperty`, `Rename-ItemProperty`; `New-Item` check fires only when the extent mentions `RegistryPath|SafeBoot...`; .NET check catches only `SetValue`, not `CreateSubKey` or `Microsoft.Win32.Registry`-via-variable); `NVMe_Driver_Patcher.ps1:510-522, 540` (`Initialize-EventLogSource` runs unconditionally — first `-Silent -Status` creates an HKLM event-log source key, a machine mutation on a pure status query the gate cannot see)
  Problem: The read/recover-only boundary is a release gate; as written it certifies mutation shapes it doesn't enumerate (this pass found the SafeBoot GUID-key deletion it never flagged).
  Evidence: Gate patterns vs. script content compared.
  Fix: Add the missing command/member patterns; make the `New-Item` check unconditional for HKLM paths; either gate `Initialize-EventLogSource` behind non-status modes or whitelist it explicitly with a comment; self-check the gate by reintroducing each real defect shape (the SafeBoot `Remove-Item` above is the first fixture).
  Acceptance: Gate fails against the current script's SafeBoot deletion before that P1 fix lands, and against each fixture shape.
  Confidence: Verified
  Effort: M

- [ ] P3 — Environment-dependent tests whose interesting branch never runs on a clean host; suite side-effects on the real machine
  Category: testing
  Where: `tests/NVMeDriverPatcher.Tests/RegistryBackupTests.cs:23-30, 41-50` (expected output derived from live HKLM; the "present → dword restore / issue-#13 never-delete" branch has no fixture coverage anywhere); `PatchServiceTests.cs:20-31` (`ProbeRemovalResidue` vacuous on clean hosts, acknowledged in comment); `FeatureStoreWriterServiceTests.cs:189` (vacuously-true assert on unconfigured hosts); `RecoveryProofGateServiceTests.cs:12-29, 51-56` (creates the real `%ProgramData%\NVMePatcher` directory + probe files on the dev machine); `SafeBootRemovalAccessTests.cs:27, 118-159` (deny-ACL'd HKCU keys orphaned if a run crashes between ACE and Dispose); build warning xUnit2031 at `ControlSetMirroringTests.cs:108` (pre-existing baseline)
  Problem: Safety-critical contracts (backup restore of present values, residue formatting) are permanently uncovered on the machines that actually run the suite; two suites leave real-machine residue.
  Evidence: Each test read; branch coverage reasoning per file.
  Fix: Add fixture-driven variants using the HKCU-tree technique `SafeBootRemovalAccessTests` already uses; point `RecoveryProofGateService` tests at a temp working dir; wrap the deny-ACE test in a finally-based ACL restore plus a startup sweep of orphaned GUID keys; fix the xUnit2031 `Assert.Single` overload.
  Acceptance: New fixtures exercise the present-value backup branch and residue formatting deterministically; suite run leaves no new keys/dirs outside temp; build warning-free.
  Confidence: Verified
  Effort: M

- [ ] P3 — C# bare-name gate: per-line exemption can mask a co-located launch
  Category: testing
  Where: `tests/NVMeDriverPatcher.Tests/SystemToolPathServiceTests.cs:58-60, 222-228` (`NonLaunchToolLiteral` exempts the entire line on `Path.Combine(`/`.Equals(`/`File.Exists` etc.)
  Problem: `new ProcessStartInfo("dism.exe") { WorkingDirectory = Path.Combine(dir) }` passes the gate — the same masking class that defeated it before 2026-08-02. No current occurrence in `src/` (grep-verified), so this is gate hardening, not a live defect.
  Evidence: Regex behavior traced against the constructed counter-example.
  Fix: Scope the exemption to the literal's context (token-level match) instead of the whole line; add the counter-example to the gate's self-check.
  Acceptance: Self-check fails on the counter-example with the old regex, passes with the new.
  Confidence: Verified
  Effort: S

- [ ] P3 — FeatureStore exact restore cannot clear the priority-8 User override when the baseline held a non-priority-8 configuration
  Category: correctness
  Where: `src/NVMeDriverPatcher.Core/Services/FeatureStoreWriterService.cs:533-557` (`BuildRestoreUpdate`: `Found=true` branch re-asserts at the baseline's priority; only `Found=false` issues the Operation-4 reset)
  Problem: If the pre-fallback configuration existed at a priority other than 8 (plausible for Microsoft/EKB-set velocity IDs on Insider builds), the fallback's priority-8 override is never reset; `ProbeConfigurationDifferences` then reports a permanent baseline difference and every uninstall retry fails identically (honest, but unrecoverable without manual `vivetool /reset`).
  Evidence: Restore builder read; probe path traced.
  Fix: For `Found=true` entries with priority != 8, emit a priority-8 Operation-4 reset first, then re-assert the baseline configuration.
  Acceptance: Unit test with a priority-4 baseline fixture: restore plan contains the reset followed by the re-assert.
  Confidence: Likely (environment-dependent precondition; mechanism verified)
  Effort: S

- [ ] P3 — Bare CLI exe download ships without its native SQLite library
  Category: packaging
  Where: `src/NVMeDriverPatcher.Cli/NVMeDriverPatcher.Cli.csproj` (no `IncludeNativeLibrariesForSelfExtract`; the GUI csproj sets it), `packaging/release-artifacts.json` (`cli` uploads `publish/cli/NVMeDriverPatcher.Cli.exe` alone)
  Problem: A local `dotnet publish -r win-x64 -p:PublishSingleFile=true` of the CLI leaves `e_sqlite3.dll` loose beside `NVMeDriverPatcher.Cli.exe`. The release uploads the exe by itself, so a direct CLI download has no SQLite native library and every command that touches `DataService` (snapshots, benchmark history) hits `DllNotFoundException` or a swallowed save failure. The MSI is unaffected because it ships the GUI publish folder. The data JSON files had the same gap and are now embedded in Core.
  Evidence: Publish layout probe on 2026-10-05 listed `e_sqlite3.dll` beside the CLI exe.
  Fix: Set `IncludeNativeLibrariesForSelfExtract=true` in the CLI csproj (and the Tray and Watchdog ones if they ever load SQLite), then re-run the publish probe and confirm only the exe, pdbs and `admx` remain. Check `SqliteVersionTests` still pins the native asset.
  Acceptance: A CLI exe copied alone into an empty folder completes a DataService read and write without a missing-DLL error.
  Confidence: Likely (layout verified; runtime failure inferred from SQLitePCLRaw native loading)
  Effort: S

- [ ] P3 — BypassIO verdict uses one storage driver for every volume
  Category: correctness
  Where: `src/NVMeDriverPatcher.Core/Services/BypassIoInspectorService.cs` (`Inspect` reads `ReadDeviceServiceEvidence()` once; `SelectStorageService` picks the highest-priority service present anywhere on the machine, nvmedisk > stornvme > storahci > ...)
  Problem: Every fixed volume inherits that one machine-wide service. A SATA (storahci) or USB data volume on a PC whose boot drive is on stornvme reports "Enabled", so `BuildGamingImpactSummary` lists it as a BypassIO volume, and a mixed nvmedisk/stornvme machine reports every volume as nvmedisk. The system-drive verdict (`DriveService.GetBypassIOStatus`) is right only when the system drive's controller happens to be the top-priority service.
  Evidence: Code read on 2026-10-05; `BuildVolumeInfo` takes the single `BypassIoDeviceEvidence` for all letters.
  Fix: Map each volume to its own controller: volume letter to disk number (`MSFT_Partition.DiskNumber`, already queried that way in `BenchmarkService`), disk to its PnP device, then walk `CM_Get_Parent` to the storage controller and read that node's `DEVPKEY_Device_Service`. Pass the per-volume service into `BuildVolumeInfo`; fall back to "Unknown" (not the machine-wide pick) when the walk fails. Keep the registry and fsutil evidence as they are.
  Acceptance: Unit test with a two-volume fixture (C: on stornvme, D: on storahci) reports C: Enabled and D: Disabled with stack storahci.sys; the gaming-impact summary names only C:. A live run on a mixed NVMe+SATA machine matches Device Manager.
  Confidence: Verified (mechanism); hardware confirmation pending
  Effort: M

### Unaudited — needs a pass

- [ ] P3 — Areas this audit did not cover
  Category: docs
  Where: (scope note)
  Problem: (a) `packaging/telemetry-receiver/` was only shallowly re-checked against its prior audit, not re-audited in depth. (b) The WPF GUI was audited by code-trace only — it requires elevation this environment cannot grant, so no live pixel/theme/screenshot verification was performed; the theme findings above are verified from resource dictionaries, but a live three-theme visual sweep (especially nested surfaces: dialogs over the workspace, chart tooltips, toasts) remains unexecuted. (c) Live service behavior (P0 watchdog finding, SCM restart semantics) is verified by trace, not by installing the service. (d) The `claude-security` scan follow-up remains parked in Roadmap_Blocked.md.
  Evidence: Session constraints (non-elevated agent shell; audit-only pass).
  Fix: On the next elevated session: run the GUI in all three themes on the isolated display and screenshot the surfaces named above; run `Test-WatchdogService.ps1` with an extended liveness window; give telemetry-receiver a dedicated pass.
  Acceptance: Each sub-item either confirms clean or produces new ROADMAP entries.
  Confidence: Verified (as a scope statement)
  Effort: M

## Research-Driven Additions — 2026-08-11

Evidence and full reasoning in RESEARCH.md (2026-08-11 pass). No item here duplicates the
2026-08-10 audit findings above; where they touch the same file, the relationship is noted inline.

### P1


### P2

- [ ] P2 — Ship the Intune Check/Remediate proactive-remediation pair
  Why: `packaging/intune/` ships only `Detect-NVMeDriverPatcher.ps1` plus MSI wrapping instructions. Intune's proactive-remediation contract is a *pair*, and a competing repo already ships one for this exact tweak — fleet operators currently have to write the remediation half themselves.
  Evidence: `packaging/intune/README.md`; https://github.com/jhochwald/PowerShell-collection (`Check-`/`Remediate-EnablingNvmeNativeDrivers.ps1`).
  Touches: `packaging/intune/` (new `Remediate-*.ps1`), `packaging/intune/README.md`, `packaging/release-artifacts.json`, `scripts/New-ArtifactManifest.ps1`, `InstallerContentTests`.
  Acceptance: A detect/remediate pair ships in the Intune zip; the remediation calls the CLI and honors `BuildActionPolicyService` (never mutates on a `none-known`/stale-rules build); exit codes match Intune's contract.
  Complexity: S

- [ ] P2 — Detect and repair damage left by third-party debloat scripts
  Why: FR33THY "Ultimate" (631★) applies a 5th override value `3244671118` this tool does not know, and its revert runs `reg delete HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft /f`, destroying the entire policy subtree. Users arrive with orphaned SafeBoot entries and a wiped Policies tree, and the tool currently reads that as an ordinary clean state.
  Evidence: https://github.com/FR33THYFR33THY/Ultimate/blob/main/8%20Advanced/19%20NVME%20Faster%20Driver.ps1; repo-wide grep for `3244671118` returns 0 files. Related to the blocked "debloat tools break feature-management prerequisites" item in Roadmap_Blocked.md, but this is registry-state detection and needs no VM repro.
  Touches: `Services/PreflightService.cs`, `Services/RegistryService.cs` (classify), `Services/PatchService.cs` residue probe, `Models/AppConfig.cs` (known-foreign IDs).
  Acceptance: Preflight names a foreign override value or a SafeBoot entry with no matching override as third-party residue, with a remediation hint; a fixture test covers the wiped-`Policies\Microsoft` shape.
  Complexity: M

- [ ] P2 — Telemetry receiver: pin wrangler, migrate off the unsafe rate-limit binding, refresh compatibility date
  Why: `packaging/telemetry-receiver/package.json` declares no dependencies and there is no lockfile, so builds float to whatever `npx` resolves; the worker still uses `[[unsafe.bindings]]` for rate limiting although `[[ratelimits]]` has been stable since wrangler 4.36.0; `compatibility_date` is 2026-04-19. (Narrower and independent of the "telemetry-receiver needs a dedicated pass" scope note above.)
  Evidence: `packaging/telemetry-receiver/package.json`, `wrangler.toml`; https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/.
  Touches: `packaging/telemetry-receiver/package.json`, `wrangler.toml`, `README.md`, `TelemetryReceiverSummaryTests`.
  Acceptance: `wrangler` is a pinned devDependency with a committed lockfile; both limiters use `[[ratelimits]]`; `wrangler deploy --dry-run` succeeds.
  Complexity: S

### P3

- [ ] P3 — Prove BypassIO support from the bound driver's INF instead of inferring it
  Why: BypassIO is gated by a storage driver declaring `STORAGE_SUPPORTED_FEATURES_BYPASS_IO`; if the declaration is absent, BypassIO on that volume is blocked outright and DirectStorage silently falls back. Reading whether the bound driver's INF declares it converts the gaming-impact warning from heuristic to proof. Depends on the P2 locale fix landing first.
  Evidence: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/bypassio; `Services/BypassIoInspectorService.cs`.
  Touches: `Services/BypassIoInspectorService.cs`, `Services/DriveService.cs`, CLI `bypassio` JSON.
  Acceptance: The gaming-impact summary states whether the currently bound storage driver declares BypassIO support, sourced from the INF rather than from `fsutil` prose.
  Complexity: M

- [ ] P3 — Build rules: record that 26H1 has no Hotpatch and 26H2 is an enablement package over 25H2
  Why: 26H1 is an OEM-only ARM-targeted release without Hotpatch, and 26H2 ships as an enablement package on the 25H2 servicing branch — so build-number logic keyed on `26200.x` keeps working while the *reported version string* changes. Cheap pre-emptive correctness for the gate that decides whether apply is permitted.
  Evidence: https://techcommunity.microsoft.com/blog/windows-itpro-blog/what-to-know-about-windows-11-version-26h1/4491941; `src/NVMeDriverPatcher.Core/windows_build_rules.json`.
  Touches: `windows_build_rules.json`, `Services/WindowsBuildRulesService.cs`, `WindowsBuildRulesServiceTests`.
  Acceptance: A 26H2-reporting host resolves the same rule as its 25H2 build number; the 26H1 rule summary states the Hotpatch exception.
  Complexity: S

- [ ] P3 — First-run expectation gate for the non-enthusiast audience
  Why: The tool is now mirrored by MajorGeeks, which brings users who did not read the README to a program whose measured benefit at desktop queue depths is near zero and whose 4K random write is slightly worse — while the downside is a boot-critical driver swap. The confirmation dialog explains risk well but never states "this may do nothing for you".
  Evidence: https://www.majorgeeks.com/files/details/nvme_driver_patcher_for_windows_11.html; https://www.storagereview.com/review/windows-server-native-nvme; `ViewModels/MainViewModel.cs` `BuildConfirmMessage`.
  Touches: `ViewModels/MainViewModel.cs` (`BuildConfirmMessage` GOOD TO KNOW tier), `Services/DocsService.cs`, README "What Does This Do?".
  Acceptance: The confirmation's expected-gains text distinguishes high-queue-depth workloads from ordinary desktop use and names the write regression; the README does the same above the fold. Pairs with the QD1 benchmark item so the claim is measurable on the user's own machine.
  Complexity: S

- [ ] P3 — Fix the Chocolatey/Scoop blocked item's mechanism (it names a workflow file that cannot exist)
  Why: The blocked item in Roadmap_Blocked.md says to add `choco push` and a Scoop bucket PR step to `.github/workflows/release.yml`. This repo has no `.github/workflows/` at all and build/release CI is banned by policy, so the item as written is unimplementable even once its credentials arrive.
  Evidence: `Roadmap_Blocked.md:48-53`; `.github/` contains only issue templates.
  Touches: `Roadmap_Blocked.md`, `scripts/Build-ReleaseArtifacts.ps1`, `scripts/Update-PackageManifests.ps1`.
  Acceptance: The blocked item describes a local publish step in the release builder gated on the credentials, and no longer references a GitHub Actions workflow.
  Complexity: S

- [ ] P3 — Plan the xunit v3 migration
  Why: NuGet marks `xunit` 2.9.3 deprecated ("Legacy") with `xunit.v3` as the alternative; v3's `TestContext.Current.CancellationToken` would replace the hand-rolled bounded-`WaitForExit` pattern documented in CLAUDE.md and tracked in the P2 `ReadToEnd()` item above. Not urgent — 2.9.3 has no CVE — but the suite is this repo's primary safety evidence and should not sit on a deprecated runner indefinitely.
  Evidence: `dotnet list package --deprecated` on `tests/NVMeDriverPatcher.Tests`; https://xunit.net/docs/getting-started/v3/migration.
  Touches: `tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj` and the whole suite.
  Acceptance: The suite runs green on xunit.v3 with the same test count and no new environment side effects; the shared bounded-process helper uses the framework cancellation token.
  Complexity: L
