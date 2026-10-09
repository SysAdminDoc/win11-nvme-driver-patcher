# Roadmap — win11-nvme-driver-patcher

Actionable work only. Historical and completed roadmap material is archived in CHANGELOG.md. Work that is waiting on hardware, a VM, credentials or a release decision is not listed here.

## Audit Findings — 2026-08-10

Baseline at audit time: `dotnet build` clean (1 warning: xUnit2031 at `tests/NVMeDriverPatcher.Tests/ControlSetMirroringTests.cs:108`), 1166/1166 tests pass, `Validate-ReleaseVersions.ps1` / `Validate-DocumentationFacts.ps1` / `Validate-BuildRulesFreshness.ps1` all pass (rules stale in 21 days). No pre-existing failures.

### P2

### P3

- [ ] P3 — GUI: the Next step buttons, bench label and log list are still unbound, and post-command refreshes still read history on the UI thread
  Category: maintainability / perf
  Where: `src/NVMeDriverPatcher/ViewModels/`: `HasNextStep*`, `NextStep{Primary,Secondary}Action{Text,Enabled,Id}` and `UpdateRecommendedActions` (the ids are read only by `MainWindow.xaml.cs:539,544`), `BenchLabelText`/`BenchLabelVisible` (watched only at `MainWindow.xaml.cs:611`), `LogEntries` (feeds `LogText` only); `MainViewModel.UpdateOverviewSummary` called from `MainViewModel.Commands.cs:107,250,439`
  Problem: The 2026-10-09 cleanup removed the unbound clusters it named but left these. The Next step card has no buttons at all, so its action plumbing is dead. After apply, remove and benchmark commands, `UpdateOverviewSummary` still reads benchmark history and `RegistryService.GetPatchStatus()` on the UI thread, which the refresh path no longer does.
  Fix: Either give the Next step card its two buttons or delete the action cluster and its handlers; bind or delete the bench label; make `LogEntries` private. Move the post-command summary reads into the same background gather the refresh uses.
  Acceptance: `ViewModelSurfaceTests` covers these members; no UI-thread SQLite or registry read remains in the post-command path.
  Confidence: Verified by grep (leftovers reported by the cleanup pass)
  Effort: S

- [ ] P3 — `PatchServiceTests` still reads the live HKLM overrides key
  Category: test-reliability
  Where: `tests/NVMeDriverPatcher.Tests/PatchServiceTests.cs:20-31`, `InspectRegistryOverrideOwnership_ReadsLiveKeyWithoutMutatingIt`
  Problem: The registry fixture work moved backup and residue coverage onto HKCU trees, but these two still read the real overrides key, so their interesting branches depend on whether the test machine is patched.
  Fix: Point them at an HKCU fixture through the internal hive overloads the fixture tests already use, and keep one explicit live smoke test if the read path needs it.
  Acceptance: Both pass with the same branches on a patched and an unpatched machine.
  Confidence: Verified
  Effort: S

### Unaudited — needs a pass

## Research-Driven Additions — 2026-08-11

Evidence and full reasoning in RESEARCH.md (2026-08-11 pass). No item here duplicates the
2026-08-10 audit findings above; where they touch the same file, the relationship is noted inline.

### P1


### P2

### P3

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
