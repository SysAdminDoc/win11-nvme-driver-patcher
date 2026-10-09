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

- [ ] P3 — GUI: large dead ViewModel surface still computed every refresh; user-facing features silently vanished in the redesign
  Category: maintainability
  Where: `src/NVMeDriverPatcher/ViewModels/`: `ReadinessChecks`/`LeftChecks`/`RightChecks` + `PreflightCheckVM` tooltips (`MainViewModel.cs:467-506`), `Drives`/`DriveRowVM` (`RowViewModels.cs:63-106`), `RegistryFlags`/`SafeBootFlags` (`:804-836`), `AttentionNotes` cluster (`:1089-1167`), `DirectStorageImpactText/Severity/PanelVisible` (`:156-159, 1046-1087`), `SkipWarnings` (no toggle anywhere yet described by `OptionsSummaryText`, `MainViewModel.Settings.cs:22-24`), `ChangePlanSteps`, `RiskSummaryColor`, `ActionReadinessText/Color` (bound only inside collapsed XAML)
  Problem: None of these are bound in any view (grep-verified); registry/WMI projections run on every refresh for nothing. Product decision folded in: the per-check readiness list, drive table with SMART/NATIVE-LEGACY badges, per-flag view, and the gaming-impact panel were real features whose only surfaces were removed.
  Evidence: Grep across `Views/` for each binding path.
  Fix: Decide per cluster: re-surface (drive table and readiness list have real value — pairs with the OverviewGrid item) or delete the VM code and its refresh cost. Remove the `SkipWarnings` sentence from `OptionsSummaryText` unless a toggle ships.
  Acceptance: Every remaining VM public member is bound somewhere; refresh no longer computes unbound projections.
  Confidence: Verified
  Effort: M

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

- [ ] P3 — Plan the xunit v3 migration
  Why: NuGet marks `xunit` 2.9.3 deprecated ("Legacy") with `xunit.v3` as the alternative; v3's `TestContext.Current.CancellationToken` would replace the hand-rolled bounded-`WaitForExit` pattern documented in CLAUDE.md and tracked in the P2 `ReadToEnd()` item above. Not urgent — 2.9.3 has no CVE — but the suite is this repo's primary safety evidence and should not sit on a deprecated runner indefinitely.
  Evidence: `dotnet list package --deprecated` on `tests/NVMeDriverPatcher.Tests`; https://xunit.net/docs/getting-started/v3/migration.
  Touches: `tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj` and the whole suite.
  Acceptance: The suite runs green on xunit.v3 with the same test count and no new environment side effects; the shared bounded-process helper uses the framework cancellation token.
  Complexity: L

## Research-Driven Additions — 2026-10-06

Evidence and full reasoning are in RESEARCH.md (2026-10-06 pass). None of these repeats an item above; where one touches the same ground, the item says how they relate.

### P1

- [ ] P1 — Test the per-controller StorPort route and the global kill switch in VMs
  Why: Every current retail client build resolves to `none-known` in `windows_build_rules.json`, so the enable path is idle for nearly every user. The StorPort value is the only route reported working on 25H2 (26200.9168) and on 24H2 from 26100.8875. Separately, if `DisableNativeNVMeStack=1` forces the legacy path ahead of any feature decision, the recovery kit gets a one-value offline revert that works whatever route bound the drive.
  Evidence: RESEARCH.md Executive Summary items 1 and 2, Open Questions 1 and 2; `windows_build_rules.json` (lastReviewed 2026-10-05); the 24H2 rig recipe in the project working notes.
  Touches: VM work only (the 24H2 rig, plus retail-edition 25H2 26200.x and 26H2 26300.x guests). Results go into `windows_build_rules.json` and, if the route works, a new roadmap item.
  Acceptance: An evidence table per build covering: bind after reboot on a secondary drive and then the boot drive (Class `NvmeDisk`, service `nvmedisk`, a `GenNvmeDisk` hardware ID), a Safe Mode boot, `DISM /ScanHealth` and SFC, survival across one cumulative update, revert by deleting the value, and revert by setting `DisableNativeNVMeStack=1` offline from WinRE (also on the 24H2 guest after a three-value bind). The build rules record each verdict with `lastReviewed`. A working route gets its own implementation item with the measured costs; a failed one gets a sentence in the rule summary.
  Complexity: L

### P2

### P3

- [ ] P3 — Record the DiskSpd version and flags with every benchmark result
  Why: DiskSpd 2.3 (2026-09-04) changed two defaults (P-cores before E-cores, buffers separated by cache line) and added BypassIO and IoRing modes. The app pins v2.2 by hash, which is right, but results don't say which DiskSpd made them, so a later bump would put incomparable runs side by side.
  Evidence: https://github.com/microsoft/diskspd/releases; `src/NVMeDriverPatcher.Core/Services/BenchmarkService.cs:28,39-45`.
  Touches: `BenchmarkService` result model and SQLite history, the compare view, CLI benchmark JSON.
  Acceptance: Each stored result carries the DiskSpd version, its SHA-256 and the full argument line. Comparing two results from different DiskSpd versions shows a warning instead of a percentage. Moving to 2.3 is its own decision, made only with `-aup -bsn` or a fresh baseline.
  Complexity: S

- [ ] P3 — Prove Identify and SMART reads still work under nvmedisk
  Why: A driver teardown says nvmedisk doesn't clearly expose `IOCTL_STORAGE_PROTOCOL_COMMAND`, and users say vendor SSD tools stop seeing the drive. The app's health reads use the `IOCTL_STORAGE_QUERY_PROPERTY` protocol-specific path, which nobody has checked under nvmedisk. If it fails, the health view goes blank after the swap, exactly when people want it.
  Evidence: https://borecraft.com/findings/Windows_Server_2025_NVMe_Driver.html; https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices; `src/NVMeDriverPatcher.Core/Interop/StorageStructs.cs:15-19`; `Services/NvmeIdentifyService.cs`.
  Touches: `NvmeIdentifyService`, the SMART reader, the support bundle (record which query path answered).
  Acceptance: On the 24H2 guest with nvmedisk bound, Identify and SMART reads either work (recorded in the working notes) or the UI says plainly that the native driver doesn't answer them, instead of showing empty values.
  Complexity: S

- [ ] P3 — Ship one shared runtime in the MSI instead of four
  Why: v5.6.0's MSI and Intune zip are 211 MB each, because the GUI (89 MB), Tray (57 MB), CLI (44 MB) and Watchdog (44 MB) each embed their own .NET runtime. An MDL user complained about the size. Publishing the four self-contained but not single-file into one shared directory keeps the no-prerequisite install with one runtime copy.
  Evidence: v5.6.0 release asset sizes; MDL native NVMe thread (2026 size complaint); `src/NVMeDriverPatcher/NVMeDriverPatcher.csproj:17-18`; `src/NVMeDriverPatcher.Cli/NVMeDriverPatcher.Cli.csproj:11-12`.
  Touches: `Build-ReleaseArtifacts.ps1` MSI staging, the WiX source, the service and scheduled-task registration paths, `Validate-ReleaseAssets.ps1`. The portable single-file exes stay as they are. Relates to the P3 "dll-hosted runs register `dotnet.exe`" item above: a shared directory must still register the apphost exe, not `dotnet.exe`.
  Acceptance: MSI and Intune zip sizes measured before and after; install, upgrade over the 5.6.0 MSI, repair and uninstall all pass; the service and tray start from the new layout.
  Complexity: M

- [ ] P3 — CHANGELOG's 5.7.0 section describes a release that was never published
  Why: The newest tag and GitHub release is v5.6.0, but the CHANGELOG has `## [5.7.0] - 2026-08-11` and the repo notes said 5.7.0 shipped. The existing tag item above covers 5.4.0, 5.5.0, 5.3.0 and `v.3.0.0`, not this one. Tagging an old commit as a release that never had artifacts would mislead, so fold it forward.
  Evidence: `gh release list` (2026-10-06); `CHANGELOG.md:253`; bump commit `394e2b8`.
  Touches: `CHANGELOG.md`, the next release's notes.
  Acceptance: When the next release is cut, its notes carry every 5.7.0 bullet, and no CHANGELOG heading names a version without a matching tag.
  Complexity: S

- [ ] P3 — Dependency refresh: SourceGear.sqlite3 3.53.4, Microsoft.NET.Test.Sdk 18.10.1, SkiaSharp 4.153.1, wrangler 4.148.0
  Why: All four have newer stable releases with no advisories against the pinned versions, and staying close makes the next security bump small. SkiaSharp needs a render check because 4.150 turned obsolete APIs into errors. SQLitePCLRaw stays on 3.0.4.
  Evidence: NuGet flat-container lookups (2026-10-06); https://www.sqlite.org/changes.html; https://developers.cloudflare.com/workers/wrangler/migration/deprecations/.
  Touches: `NVMeDriverPatcher.Core.csproj`, the test csproj, `NVMeDriverPatcher.csproj` (SkiaSharp family), `packaging/telemetry-receiver/package.json` and `wrangler.toml` `compatibility_date`.
  Acceptance: The build is clean, the suite passes, the GUI charts render the same in all three themes, and `wrangler deploy --dry-run` passes for the receiver.
  Complexity: S

- [ ] P3 — Smoke-test the MSI lifecycle in Windows Sandbox
  Why: The winget sandbox smoke (`scripts/Test-PackageSandbox.ps1`) went with the winget channel, so nothing installs, queries and removes a built package in a clean guest anymore. The MSI is the install route admins and Intune use.
  Evidence: removed `scripts/Test-PackageSandbox.ps1` (2026-10-06); `packaging/wix/NVMeDriverPatcher.wxs`; `scripts/Build-ReleaseArtifacts.ps1` MSI step.
  Touches: a new `scripts/Test-MsiSandbox.ps1` that writes a `.wsb` with the publish folder mapped read-only, runs `msiexec /i` quietly, checks Program Files, the service and the scheduled task, runs `msiexec /x`, and checks nothing is left; a contract test that pins those steps.
  Acceptance: On an x64 Windows 11 host with Windows Sandbox enabled, the script exits 0 and its guest log shows install, the installed CLI answering `--version`, uninstall, and no files, service or task left behind. Without Sandbox it fails fast with a clear message.
  Complexity: S

- [ ] P3 — The suite never runs the documentation-facts validator against the real repo
  Why: Adding the StorPort readiness check (b5d915a) raised the preflight count to 29 while README still said 28, and all 1741 tests passed. `DocumentationFactsValidatorTests` only feeds the validator fixtures; the live check runs only inside `Validate-ReleaseVersions.ps1` at release time, so drift sits unnoticed until a release build.
  Evidence: `tests/NVMeDriverPatcher.Tests/DocumentationFactsValidatorTests.cs`; `scripts/Validate-DocumentationFacts.ps1`; the README count fix of 2026-10-06.
  Touches: one test that runs `Validate-DocumentationFacts.ps1 -RepoRoot <repo>` on the checkout, skipping the untracked `CLAUDE.md` checks when the file is absent so a clean clone still passes.
  Acceptance: Changing the README preflight count by one makes the suite fail with the validator's message; a clean clone without CLAUDE.md passes.
  Complexity: S

- [ ] P3 — The in-place updater's staging code has no caller
  Why: `AutoUpdaterService.StageUpdateAsync` (sidecar check, signed manifest, protected staging, swap command) is only reached from tests. The GUI's update badge opens the release page, and the CLI's `update-check` only prints the asset URL. The README used to describe a Help menu updater that stages downloads; it was corrected on 2026-10-06. The code either gets a front end or goes.
  Evidence: `git grep StageUpdateAsync` (tests only); `MainViewModel.ApplyUpdateBadge`; `Program.UpdateCheckCommand`.
  Touches: either a CLI `update --stage` command (admin, prints the restart command) plus a GUI button behind it, or deleting `StageUpdateAsync`, `DownloadPinnedAsync`, `BuildRestartCommand` and their tests while keeping the manifest checks for `update-check`.
  Acceptance: Either a user can stage a verified update from the CLI and the GUI, shown working against a real release with a signed manifest, or the dead path is gone and `update-check` reports whether the latest release's manifest verifies.
  Complexity: M
