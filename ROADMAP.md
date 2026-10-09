# Roadmap — win11-nvme-driver-patcher

Actionable work only. Historical and completed roadmap material is archived in CHANGELOG.md. Work that is waiting on hardware, a VM, credentials or a release decision is not listed here.

## Audit Findings — 2026-08-10

Baseline at audit time: `dotnet build` clean (1 warning: xUnit2031 at `tests/NVMeDriverPatcher.Tests/ControlSetMirroringTests.cs:108`), 1166/1166 tests pass, `Validate-ReleaseVersions.ps1` / `Validate-DocumentationFacts.ps1` / `Validate-BuildRulesFreshness.ps1` all pass (rules stale in 21 days). No pre-existing failures.

### P2

### P3

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

- [ ] P3 — Test helpers launch `powershell.exe` by bare name
  Category: testing
  Where: `tests/NVMeDriverPatcher.Tests/DocumentationFactsValidatorTests.cs` (`RunValidator`), and any other test that starts a tool with `new ProcessStartInfo("<tool>.exe")`
  Problem: The bare-name gates cover `src/`, `packaging/` and `scripts/`, not the test tree, so a test run resolves `powershell.exe` through PATH and the current directory.
  Fix: Route test launches through `SystemToolPathService.Resolve`/`.PowerShell` and widen the gate to `tests/` with an explicit allowlist for fixtures that need a bare name.
  Acceptance: The widened gate passes; a planted `powershell.exe` in the test output folder isn't picked up.
  Confidence: Verified (reported by the 2026-10-09 lane)
  Effort: S

- [ ] P3 — Build rule `26300-feature-flags-page` calls 26300+ "26H2 Experimental"
  Category: docs
  Where: `src/NVMeDriverPatcher.Core/windows_build_rules.json` (rule `26300-feature-flags-page` summary)
  Problem: 26H2 ships as an enablement package over 25H2 on build 26200 (now noted in the 26200 rules), so labeling the 26300 Insider train "26H2" contradicts the same file.
  Fix: Re-read the rule's `sourceUrl` and Microsoft's current Insider channel naming, then rename the label to what Microsoft calls 26300.x today. Don't touch the verdict or `lastReviewed` without that re-verification.
  Acceptance: No rule summary calls two different builds 26H2; `WindowsBuildRulesServiceTests` pass.
  Confidence: Likely (noticed 2026-10-09 while adding the 26H2 note)
  Effort: S

### Unaudited — needs a pass

## Research-Driven Additions — 2026-08-11

Evidence and full reasoning in RESEARCH.md (2026-08-11 pass). No item here duplicates the
2026-08-10 audit findings above; where they touch the same file, the relationship is noted inline.

### P1


### P2

### P3

- [ ] P3 — First-run expectation gate for the non-enthusiast audience
  Why: The tool is now mirrored by MajorGeeks, which brings users who did not read the README to a program whose measured benefit at desktop queue depths is near zero and whose 4K random write is slightly worse — while the downside is a boot-critical driver swap. The confirmation dialog explains risk well but never states "this may do nothing for you".
  Evidence: https://www.majorgeeks.com/files/details/nvme_driver_patcher_for_windows_11.html; https://www.storagereview.com/review/windows-server-native-nvme; `ViewModels/MainViewModel.cs` `BuildConfirmMessage`.
  Touches: `ViewModels/MainViewModel.cs` (`BuildConfirmMessage` GOOD TO KNOW tier), `Services/DocsService.cs`, README "What Does This Do?".
  Acceptance: The confirmation's expected-gains text distinguishes high-queue-depth workloads from ordinary desktop use and names the write regression; the README does the same above the fold. Pairs with the QD1 benchmark item so the claim is measurable on the user's own machine.
  Complexity: S

## Research-Driven Additions — 2026-10-06

Evidence and full reasoning are in RESEARCH.md (2026-10-06 pass). None of these repeats an item above; where one touches the same ground, the item says how they relate.

### P1

### P2

### P3

- [ ] P3 — Ship one shared runtime in the MSI instead of four
  Why: v5.6.0's MSI and Intune zip are 211 MB each, because the GUI (89 MB), Tray (57 MB), CLI (44 MB) and Watchdog (44 MB) each embed their own .NET runtime. An MDL user complained about the size. Publishing the four self-contained but not single-file into one shared directory keeps the no-prerequisite install with one runtime copy.
  Evidence: v5.6.0 release asset sizes; MDL native NVMe thread (2026 size complaint); `src/NVMeDriverPatcher/NVMeDriverPatcher.csproj:17-18`; `src/NVMeDriverPatcher.Cli/NVMeDriverPatcher.Cli.csproj:11-12`.
  Touches: `Build-ReleaseArtifacts.ps1` MSI staging, the WiX source, the service and scheduled-task registration paths, `Validate-ReleaseAssets.ps1`. The portable single-file exes stay as they are. Relates to the P3 "dll-hosted runs register `dotnet.exe`" item above: a shared directory must still register the apphost exe, not `dotnet.exe`.
  Acceptance: MSI and Intune zip sizes measured before and after; install, upgrade over the 5.6.0 MSI, repair and uninstall all pass; the service and tray start from the new layout.
  Complexity: M

- [ ] P3 — Dependency refresh: SourceGear.sqlite3 3.53.4, Microsoft.NET.Test.Sdk 18.10.1, SkiaSharp 4.153.1, wrangler 4.148.0
  Why: All four have newer stable releases with no advisories against the pinned versions, and staying close makes the next security bump small. SkiaSharp needs a render check because 4.150 turned obsolete APIs into errors. SQLitePCLRaw stays on 3.0.4.
  Evidence: NuGet flat-container lookups (2026-10-06); https://www.sqlite.org/changes.html; https://developers.cloudflare.com/workers/wrangler/migration/deprecations/.
  Touches: `NVMeDriverPatcher.Core.csproj`, the test csproj, `NVMeDriverPatcher.csproj` (SkiaSharp family), `packaging/telemetry-receiver/package.json` and `wrangler.toml` `compatibility_date`.
  Acceptance: The build is clean, the suite passes, the GUI charts render the same in all three themes, and `wrangler deploy --dry-run` passes for the receiver.
  Complexity: S

- [ ] P3 — The in-place updater's staging code has no caller
  Why: `AutoUpdaterService.StageUpdateAsync` (sidecar check, signed manifest, protected staging, swap command) is only reached from tests. The GUI's update badge opens the release page, and the CLI's `update-check` only prints the asset URL. The README used to describe a Help menu updater that stages downloads; it was corrected on 2026-10-06. The code either gets a front end or goes.
  Evidence: `git grep StageUpdateAsync` (tests only); `MainViewModel.ApplyUpdateBadge`; `Program.UpdateCheckCommand`.
  Touches: either a CLI `update --stage` command (admin, prints the restart command) plus a GUI button behind it, or deleting `StageUpdateAsync`, `DownloadPinnedAsync`, `BuildRestartCommand` and their tests while keeping the manifest checks for `update-check`.
  Acceptance: Either a user can stage a verified update from the CLI and the GUI, shown working against a real release with a signed manifest, or the dead path is gone and `update-check` reports whether the latest release's manifest verifies.
  Complexity: M
