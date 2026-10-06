# Roadmap — win11-nvme-driver-patcher

Actionable work only. Historical and completed roadmap material is archived in CHANGELOG.md; blocked work is kept in Roadmap_Blocked.md.

## Audit Findings — 2026-08-10

Baseline at audit time: `dotnet build` clean (1 warning: xUnit2031 at `tests/NVMeDriverPatcher.Tests/ControlSetMirroringTests.cs:108`), 1166/1166 tests pass, `Validate-ReleaseVersions.ps1` / `Validate-DocumentationFacts.ps1` / `Validate-BuildRulesFreshness.ps1` all pass (rules stale in 21 days). No pre-existing failures.

### P2

### P3

- [ ] P3 — `winre-inject --apply` adds the same stornvme package again on every run
  Category: correctness
  Where: `WinReDriverInjectionService.BuildPlan` (the `/Add-Driver` step), `ApplyAsync`
  Problem: Every apply runs `dism /Add-Driver` with the Driver Store stornvme package, and DISM stages it as a new `oem<N>.inf` each time even when the image already carries that exact DriverVer. On the 24H2 rig three runs grew `winre.wim` from 510 MB to 515 MB to 517 MB, and a fourth would grow it again. The second and later runs also take a full backup and a mount for nothing.
  Evidence: VM run of three consecutive applies (2026-10-05): sizes 510, 515, 517 MB; `dism /Get-Drivers` on the image shows `oem0.inf` after the first injection.
  Fix: Before mounting for real, read the image's driver list (`dism /Image:<mount> /Get-Drivers` after a read-only mount, or `/Get-ImageInfo` plus the package list) and compare the stornvme DriverVer against the Driver Store package. Same or newer in the image: report "already current" and stop without a backup or commit. Older: inject, and remove the superseded `oem<N>.inf` copy (`/Remove-Driver`) so the image keeps one copy.
  Acceptance: Two applies in a row leave the image's size and SHA-256 unchanged after the first; the second run says the image is current; a staged newer package replaces the older copy rather than sitting beside it.
  Confidence: Verified
  Effort: M

- [ ] P3 — Laptop warnings blame APST, which StorNVMe doesn't use
  Category: correctness
  Where: `PreflightService.cs:199` ("APST broken"), `DryRunService.cs:125`, `MainViewModel.cs:483`, `:1147`, `:1318`, CLI `Program.cs:1300`, `DiagnosticsService.cs:594`, `ApstInspectorService.ModernStandbyApstWarning`, README "Laptop/power warning" and the risk table, `TuningProfile` keys `NoLowPowerTransitions` and `ApstIdleTimeout`
  Problem: Microsoft's StorNVMe power management page says StorNVMe doesn't use the drive's APST; it picks non-operational states itself from the power plan's NVMe idle timeouts and latency tolerances. The warnings say nvmedisk "breaks" or "disables" APST, and the tuning profile carries two stornvme values Microsoft doesn't document and no machine we've read has. The ~15% battery figure has no cited source in the repo.
  Evidence: Found while rebuilding the APST inspector on the power plan settings (it now reads "Windows idles this drive to PS3 after 200 ms and PS4 after 2000 ms" on a Samsung PM9C1b under stornvme). A string dump of stornvme.sys (djdallmann/GamingPCSetup, registrykeys_stornvme.txt) lists the Parameters\Device values the driver reads: the power ones are `IdlePowerMode`, `MedPowerResumeLatency`, `MedPowerFxIdleTimeout`, `MedPowerD3IdleTimeout`, `LowestPowerResumeLatency`, `LowestPowerFxIdleTimeout` and `LowestPowerD3IdleTimeout`. `NoLowPowerTransitions`, `AutonomousPowerStateTransitionEnabled`, `ApstIdleTimeout` and `PowerState<N>_IdleTimeUs` aren't in it, so the tuning profile's two keys and the inspector's per-state reads name values stornvme never looks at. The inspector no longer lets the two "off" values decide the verdict; the tuning profile still writes them.
  Fix: Reword the laptop warnings around what's known (stornvme's idle states come from the power plan; how nvmedisk idles the drive isn't documented), cite or drop the 15% figure, and either source the two tuning keys or stop writing them.
  Acceptance: No user-facing text says nvmedisk breaks or disables APST; every battery figure shown has a source; the tuning profile writes only documented stornvme values or says it can't confirm them.
  Confidence: Likely
  Effort: S

- [ ] P3 — Remove restores a baseline captured over a v5.0.0 patch, leaving that version's flags set
  Category: correctness
  Where: `PatchService.Uninstall` (ledger branch, `MutationLedgerService.RestoreOriginalState`), `MutationLedgerService.RestoreOriginalStateCore`
  Problem: The ledger arrived in v5.1.0. A machine patched by v5.0.0 Full and then upgraded gets a baseline that records 735209102, 1853569164 and 156965516 as pre-existing, so Remove's "exact pre-mutation state" writes all three back, logs "[Registry] Registry override residue: 3 value(s) remain" from `InspectLiveRegistryOverrideOwnership`, and still reports REMOVED and verified. Apply's leftover sweep now presumes those flags are this tool's when the baseline also holds the primary flag (`PatchService.FindUnplannedOverrides`, mid-life rule); Remove doesn't.
  Evidence: Code reading during the round-3 review of the leftover sweep; the residue summary and the SUCCESS line come from the same Uninstall branch.
  Fix: Apply the mid-life rule in the ledger restore too: when the baseline holds the primary flag for a subkey, delete this tool's override values there instead of writing them back, say so in the log, and make the REMOVED verdict depend on `InspectLiveRegistryOverrideOwnership` finding none of this tool's values.
  Acceptance: A ledger whose baseline records all three flags as present, restored against a fake registry, ends with the owned values absent and a log line saying why; a baseline without the primary flag still restores the extras exactly; Remove reports PARTIAL, not REMOVED, while any owned value remains.
  Confidence: Likely
  Effort: M

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

- [ ] P3 — Group Policy pins only apply at load; CLI flags and GUI toggles override them for the rest of the run
  Category: reliability
  Where: `src/NVMeDriverPatcher.Cli/Program.cs:97` then `:140-144` (`--include-server-key`, `--no-server-key`, `--standalone-future`, `--safe`/`--full` are applied after `GpoPolicyService.ApplyTo`); `src/NVMeDriverPatcher/ViewModels/MainViewModel.cs:199` (policy applied once, every Settings control stays editable and saves to config.json)
  Problem: The ADMX and README say policy overrides local config, and the CLI comment says a pinned fleet policy isn't quietly overridden by a local run. In practice a pinned PatchProfile, IncludeServerKey or IncludeStandaloneFuture lasts only until a CLI flag or a Settings click changes it, so an admin who pins Safe can still get Full on a machine.
  Evidence: Found while adding the IncludeStandaloneFuture policy for #19; the ADML text was kept to "Disabled turns it off" instead of claiming enforcement.
  Fix: Keep the overlay from `GpoPolicyService.Read()` and re-apply it after CLI parsing, with a `[WARNING]` naming each flag the policy overrode. In the GUI, disable the pinned controls with a "Set by Group Policy" tooltip and skip pinned fields in `SyncConfigFromUI`.
  Acceptance: With a policy pinning Safe, `apply --full` writes the Safe set and warns once; the Settings profile radios are disabled and the saved config keeps Safe.
  Confidence: Verified
  Effort: M

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
