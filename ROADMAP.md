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

- [ ] P3 — `re-enable-after-update` sets the profile after the Group Policy pins are re-applied
  Category: correctness
  Where: `src/NVMeDriverPatcher.Cli/Program.cs` (`re-enable-after-update`, the `config.PatchProfile = profile` assignment from the stored install record)
  Problem: Pins are put back once, right after flag parsing (`GpoPolicyService.ReapplyPins`). This command sets the profile later from the remembered install, so a pinned Safe can still come back as Full on that path.
  Fix: Re-apply the pins after that assignment (or have it skip a pinned profile) and warn the same way the flag path does.
  Acceptance: With a policy pinning Safe and a remembered Full install, `re-enable-after-update` writes the Safe set and prints one warning.
  Confidence: Likely (found reading the GPO fix, 2026-10-09)
  Effort: S

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

- [ ] P3 — The in-place updater's staging code has no caller
  Why: `AutoUpdaterService.StageUpdateAsync` (sidecar check, signed manifest, protected staging, swap command) is only reached from tests. The GUI's update badge opens the release page, and the CLI's `update-check` only prints the asset URL. The README used to describe a Help menu updater that stages downloads; it was corrected on 2026-10-06. The code either gets a front end or goes.
  Evidence: `git grep StageUpdateAsync` (tests only); `MainViewModel.ApplyUpdateBadge`; `Program.UpdateCheckCommand`.
  Touches: either a CLI `update --stage` command (admin, prints the restart command) plus a GUI button behind it, or deleting `StageUpdateAsync`, `DownloadPinnedAsync`, `BuildRestartCommand` and their tests while keeping the manifest checks for `update-check`.
  Acceptance: Either a user can stage a verified update from the CLI and the GUI, shown working against a real release with a signed manifest, or the dead path is gone and `update-check` reports whether the latest release's manifest verifies.
  Complexity: M
