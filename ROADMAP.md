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
