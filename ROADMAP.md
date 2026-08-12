# Roadmap — win11-nvme-driver-patcher

Actionable work only. Historical and completed roadmap material is archived in CHANGELOG.md; blocked work is kept in Roadmap_Blocked.md.

## Audit Findings — 2026-08-10

Baseline at audit time: `dotnet build` clean (1 warning: xUnit2031 at `tests/NVMeDriverPatcher.Tests/ControlSetMirroringTests.cs:108`), 1166/1166 tests pass, `Validate-ReleaseVersions.ps1` / `Validate-DocumentationFacts.ps1` / `Validate-BuildRulesFreshness.ps1` all pass (rules stale in 21 days). No pre-existing failures.

### P2

- [ ] P2 — The packaging-script $PATH gate is defeated by one level of indirection and extensionless tool names — the exact escape its C# sibling was fixed for
  Category: testing
  Where: `tests/NVMeDriverPatcher.Tests/SystemToolPathServiceTests.cs:155-159` (`PowerShellPathLookup` regex); offending-but-green sites `scripts/Build-ReleaseArtifacts.ps1:63, 111, 135, 171, 188` (`Invoke-Checked dotnet/powershell.exe/winget.exe`, bare `'wix'` fallback), `scripts/Validate-DocumentationFacts.ps1:142` (`& dotnet`)
  Problem: The gate matches only `Get-Command x.exe`, `& x.exe`, `Start-Process 'x.exe'`. Launches via the `Invoke-Checked` wrapper put the literal in a parameter position, and `dotnet`/`wix` carry no `.exe` suffix — invisible either way. The 2026-08-02 Learned entry documents this exact indirection escape for the C# detector; the PS gate kept the naive shape. The stated policy ("packaging scripts must not resolve tools through $PATH, test-gated") is certified by a gate that cannot see the violations that exist today.
  Evidence: Regex vs. offending lines compared directly.
  Fix: Either resolve dotnet/winget/powershell/wix absolutely in the scripts (as `nuget`/`msiexec`/`sc` already are) and keep the gate, or extend the gate to match extensionless known tool names and wrapper-parameter positions; self-check the gate by reintroducing each real defect shape.
  Acceptance: Gate fails against current `Build-ReleaseArtifacts.ps1` before the script fix, passes after; self-check covers the wrapper shape.
  Confidence: Verified
  Effort: M

- [ ] P2 — Eight test helpers kept the wedge-prone synchronous `ReadToEnd()` pattern the repo already logged as a 25-minute-suite-hang lesson
  Category: testing
  Where: `tests/NVMeDriverPatcher.Tests/`: `LegacyPowerShellBoundaryTests.cs:96-98`, `AutoUpdaterServiceTests.cs:144-146`, `ArtifactManifestScriptTests.cs:77-79`, `PackageManifestsScriptTests.cs:126-128`, `PackagingVersionScriptTests.cs:102-104`, `ReleaseAssetsScriptTests.cs:61-63`, `RootHygieneScriptTests.cs:56-58`, `TelemetryReceiverSummaryTests.cs:569-571`
  Problem: `ReadToEnd()` before `WaitForExit(timeout)` blocks unboundedly (timeout is dead code) and the sequential stdout-then-stderr read can deadlock on a filled stderr pipe. The last six also ignore `WaitForExit`'s bool then read `ExitCode`, which throws `InvalidOperationException` on a live process. The hardened pattern already exists in-suite (`BuildRulesFreshnessScriptTests.cs:161-166`, `SystemToolPathServiceTests.cs:123-135`) — it was applied only to the original incident's files.
  Evidence: All eight sites read; hardened counterparts confirmed.
  Fix: Extract one shared `RunProcessBounded` helper (async pipe drain, kill-on-timeout, honest exit) and use it at all eight sites.
  Acceptance: A deliberately-hanging child script fails one test with a timeout message instead of wedging the suite.
  Confidence: Verified
  Effort: S

- [ ] P2 — APST battery-impact estimate is dead code: `ApstPowerState.MaxPowerWatts` has no writer anywhere
  Category: correctness
  Where: `src/NVMeDriverPatcher.Core/Services/ApstInspectorService.cs:9, 60-68, 94-105` (consumer); `NvmeIdentifyService`'s separate `NvmePowerStateDescriptor` (has real wattage, never bridged)
  Problem: `Inspect()` never assigns `MaxPowerWatts` (repo-wide grep: no writer), so in `EstimateBatteryImpact` `ActivePowerWatts` is always null, the `lowestIdle` query always empties, and the "(up to ~X.XW idle savings)" text can never render — the laptop guidance silently degrades to the generic string. The feature the class exists for never fires.
  Evidence: Exhaustive grep; consumer logic read.
  Fix: Map per-state MPS wattage from `NvmeIdentifyService.Query()` results into `ApstPowerState.MaxPowerWatts` (correlate by power-state index), or delete the estimate branch and its UI/CLI text if identify data is deemed unreliable.
  Acceptance: On an NVMe laptop (or identify fixture), `apst` output includes the idle-savings wattage; unit test feeds a fixture identify and asserts the estimate renders.
  Confidence: Verified
  Effort: M

### P3

- [ ] P3 — `NvmeIdentifyService.Query` ignores the protocol-level result; zeroed buffers can report as successful identifies
  Category: correctness
  Where: `src/NVMeDriverPatcher.Core/Services/NvmeIdentifyService.cs:147-198`
  Problem: After `DeviceIoControl` returns TRUE, `ReturnStatus`/`ErrorCode` in the returned `STORAGE_PROTOCOL_COMMAND` header are never re-read; `Success=true` unconditionally. A controller failing the NVMe command yields empty model/serial/"0x0000" VID as a successful identify, feeding `DiagnosticsService.BuildTrustLedger` and `FirmwareCompatService` identity.
  Evidence: Marshal path read — header not re-read after the call.
  Fix: `Marshal.PtrToStructure` the header post-call; gate `Success` on `ReturnStatus == STORAGE_PROTOCOL_STATUS_SUCCESS`.
  Acceptance: Unit test on the parse path with a nonzero ReturnStatus fixture reports failure.
  Confidence: Likely
  Effort: S

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

- [ ] P3 — `SchedulerService` clamps the sweep interval to 1440, but schtasks `/SC MINUTE /MO` maxes at 1439
  Category: correctness
  Where: `src/NVMeDriverPatcher.Core/Services/SchedulerService.cs:39` (`Math.Clamp(intervalMinutes, 5, 1440)`)
  Problem: A caller passing ≥ 1440 gets "ERROR: The /MO value is invalid" from schtasks instead of a daily sweep — a confusing failure at exactly the boundary the clamp was meant to allow.
  Evidence: Clamp read; schtasks documented range 1–1439.
  Fix: Clamp to 1439 (or switch to `/SC DAILY` at 1440).
  Acceptance: `register-tasks` with a 1440-minute interval succeeds.
  Confidence: Likely (documented range; not executed)
  Effort: S

- [ ] P3 — Build-rule staleness date parse is culture-sensitive inside the mutation gate
  Category: correctness
  Where: `src/NVMeDriverPatcher.Core/Services/BuildActionPolicyService.cs:104-111` (`DateTime.TryParse(date)` + `ToUniversalTime()` on Unspecified kind)
  Problem: On non-Gregorian-default locales (ar-SA) "2026-07-14" parses to a different date or fails; direction is fail-closed (apply silently becomes verify/rollback-only) — invisible per-locale behavior change in the SSOT gate.
  Evidence: Parse read; `ViVeToolService.cs:141` already does it right (`TryParseExact` invariant).
  Fix: `DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, ...)`.
  Acceptance: Test parsing the bundled dates under ar-SA CurrentCulture yields identical staleness verdicts to invariant.
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

- [ ] P3 — Workspace tab-header badges can never render (`TabBadgeBorder` defaults Collapsed with no trigger)
  Category: visual
  Where: `Themes/DarkTheme.xaml:582-590` (style); consumers `Views/MainWindow.xaml:1288-1293, 1441-1446, 1523-1528`; feeding logic `ViewModels/MainViewModel.Workspace.cs:288-325`
  Problem: The Benchmark/Telemetry/Recovery header badges and `UpdateWorkspaceBadges` (run counts, "No NVMe", "N missing") are invisible — no setter/trigger ever shows the style.
  Evidence: Style and all usages read; no visibility trigger anywhere.
  Fix: Bind visibility to non-empty badge text (`StrToVis` converter exists), or delete the badge XAML + `UpdateWorkspaceBadges` if de-clutter was intended.
  Acceptance: Either badges render when text is set, or the dead style/logic is gone.
  Confidence: Verified
  Effort: S

- [ ] P3 — GUI dead-code cluster from the redesign
  Category: maintainability
  Where: `Themes/DarkTheme.xaml:1078-1109` (`WorkspaceTabControl` unused); `Views/MainWindow.xaml.cs:213, 215` (`Minimize_Click`/`Close_Click` unreferenced); `MainWindow.xaml:289` (`MaximizeRestoreButton` permanently Collapsed while `UpdateWindowPresentation` still updates it); `UpdateAdaptiveLayout` (xaml.cs:422) unconditionally collapses `MainContentSplitter`; `Commands.cs:767-768` (`ToggleSettingsCommand`/`SettingsPanelVisible` unused); `App.xaml:12-13` (`SettingsToggle`, `StrToColor` converters unused)
  Problem: Orphaned styles/handlers/commands mislead maintenance and mask which features are actually reachable.
  Evidence: Grep per symbol.
  Fix: Delete each (restore the splitter only if the resize feature is wanted back).
  Acceptance: Repo-wide grep finds no unreferenced symbols from this list; build clean.
  Confidence: Verified
  Effort: S

- [ ] P3 — Activity log: "entrys" pluralization, WARN not counted, clipboard crash, O(n²) rendering
  Category: ux
  Where: `Views/MainWindow.xaml.cs:1046-1049` (`FormatCount` bare `s` → "2 visible activity entrys" via `:532`); `ViewModels/MainViewModel.Commands.cs:995` (level "WARN") vs `MainViewModel.cs:1523-1534` (switch matches only "WARNING"); `MainWindow.xaml.cs:513-519` (`Clipboard.SetText` uncaught — `CLIPBRD_E_CANT_OPEN` COMException escalates to the crash dialog; sibling `CopyLog` at `Commands.cs:677-691` catches it); `MainViewModel.cs:183, 1538` (`LogText` re-joins up to 5000 entries per appended line)
  Problem: Four small defects in one surface: broken plural, minidump warnings not counted in the badge (and rendered `[WARN]` amid `[WARNING]`s), copy-selection can crash-dialog on clipboard contention, and chatty operations re-render the whole log per entry.
  Evidence: Each site read.
  Fix: Use the VM's `Pluralize` helper; log "WARNING" at `:995`; wrap `SetText` in the same try/catch as `CopyLog`; append to the TextBox incrementally or debounce `LogText` notifications.
  Acceptance: "1 visible activity entry"/"2 ... entries"; minidump warnings increment `LogWarningCount`; copy under clipboard contention shows a toast not a crash dialog; preflight burst no longer re-renders per line (profile or entry-count instrumentation).
  Confidence: Verified
  Effort: S

- [ ] P3 — "Rollback readiness" chip background hardcoded yellow while its text turns green when Ready
  Category: visual
  Where: `Views/MainWindow.xaml:1371-1373` (chip `Background=YellowBg`, text binds `RecoveryTabBadgeColor`); pattern to copy at `:1686-1712`
  Problem: Green "Ready" text on a yellow warning chip; every other Ready/Missing chip in the Recovery tab swaps background via DataTrigger.
  Evidence: XAML read.
  Fix: Same DataTrigger background swap as the sibling chips.
  Acceptance: Ready state renders the green chip treatment in all three themes.
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

- [ ] P3 — Microcopy: "Safe Boot" vs "SafeBoot" inconsistency; ThemedDialog has no accessible window title
  Category: a11y
  Where: mixed usage at `ViewModels/MainViewModel.cs:25`, Commands dialogs, vs `Views/MainWindow.xaml:406, 421`; `Views/ThemedDialog.xaml.cs` (never sets `Window.Title`)
  Problem: Three spellings of the same concept in user-facing text; empty accessible title on modal dialogs (mitigated by ShowInTaskbar=False but still announced empty by screen readers).
  Evidence: String greps; dialog code read.
  Fix: Standardize on "Safe Boot" in prose, `SafeBoot\Minimal` only for literal registry paths; set `Title` from the dialog header text.
  Acceptance: Grep finds no prose "SafeBoot"; Narrator announces the dialog title.
  Confidence: Verified
  Effort: S

- [ ] P3 — CLI help/registry text drift (four instances)
  Category: docs
  Where: `src/NVMeDriverPatcher.Cli/CliCommandRegistry.cs:130-131` (`register-tasks` claims "(benchmark regression, firmware nudge)" but registers BootVerify + WatchdogSweep — `SchedulerService.cs:11-47`), `:91-92` (`tail` described "Live event-log tail" but `EventLogTailService.Recent` is a one-shot 60-min/100-record dump), `:79-80` (`watchdog --auto-revert to arm` — the flag executes the evaluation immediately, `Program.cs:173, 373-396`), `:263` (global `--json` list omits `preflight`, `reliability`, `minidump`, `firmware`, `featurestore`, `verify-payload`, which honor it)
  Problem: Help text is the CLI's contract; all four claims are wrong today.
  Evidence: Each descriptor cross-checked against the implementation.
  Fix: Correct the four strings (and re-run `Validate-DocumentationFacts.ps1`, which counts commands from this registry).
  Acceptance: Descriptions match behavior; docs validator still passes.
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

- [ ] P3 — `fallback` re-checks force via raw command line and misses the `-f` alias
  Category: correctness
  Where: `src/NVMeDriverPatcher.Cli/Program.cs:1544` (`Environment.GetCommandLineArgs()` scan for `--force` only; alias defined at `:96`)
  Problem: `fallback -f` on a failed recovery proof exits 1 with a message suggesting `--force` — the alias contract breaks in exactly one command.
  Evidence: Site read.
  Fix: Use the already-parsed `force` bool instead of re-scanning the command line.
  Acceptance: `fallback -f` behaves identically to `fallback --force`.
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

- [ ] P3 — CLI: config-migration exception swallowed bare; `--threshold=<garbage>` silently falls back to 15
  Category: reliability
  Where: `src/NVMeDriverPatcher.Cli/Program.cs:64-70` (bare `catch { }` around `ConfigMigrationService.Migrate`), `:152-155` (failed `int.TryParse` keeps default silently)
  Problem: A throwing migration disappears (contradicts the never-fail-silently rule); `compare-benchmarks --threshold=5%` runs at the default 15 with no warning, potentially masking a regression the user tightened the gate for.
  Evidence: Both read.
  Fix: Log the migration exception (warning + event log); on unparseable `--threshold`, exit 3 naming the bad value.
  Acceptance: Bad threshold exits 3; migration failure appears in output/event log.
  Confidence: Verified
  Effort: S

- [ ] P3 — `GpoPolicyService.HasAnyPolicy` omits the two PersistenceGuard policies
  Category: correctness
  Where: `src/NVMeDriverPatcher.Core/Services/GpoPolicyService.cs:18-20` (list), `:48-51, 70-71` (read+applied)
  Problem: A GPO configuring only the persistence-guard policies reads as "no policy". Latent (no current caller found), but the asymmetry will bite the first consumer.
  Evidence: List vs read/apply members compared.
  Fix: Add both keys to `HasAnyPolicy`; add a completeness test asserting every key read in `ReadOverlay` appears in `HasAnyPolicy`.
  Acceptance: Completeness test fails if a future policy is added to one list only.
  Confidence: Verified
  Effort: S

- [ ] P3 — README "11 async preflight checks" is stale (~26 exist) and outside the docs validator
  Category: docs
  Where: `README.md:124`; `src/NVMeDriverPatcher.Core/Services/PreflightService.cs` (~26 distinct check keys); `scripts/Validate-DocumentationFacts.ps1` (validates commands/tests/paths, not this count)
  Problem: Drifted through at least three releases because no gate covers it.
  Evidence: Check keys enumerated from the service; README read.
  Fix: Derive the count in `Validate-DocumentationFacts.ps1` (same pattern as the CLI command count) and update the README number, or reword to avoid a count.
  Acceptance: Validator fails when the preflight count and README disagree.
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

- [ ] P3 — Legacy script: `.reg` written with `-NoNewline`; removal dialog promises a BitLocker suspension that no longer exists; fsutil parsing is English-only; bare-name tool launches while elevated; Refresh runs preflight synchronously on the UI thread
  Category: reliability
  Where: `NVMe_Driver_Patcher.ps1:1763` (kit `.reg` ends without trailing CRLF — regedit can drop the final SafeBoot Network delete line; `Export-RegistryBackup:2790` is safe); `:2983` (dialog: "BitLocker ... Will be automatically suspended for one reboot" — no `Suspend-BitLocker`/`manage-bde` anywhere in the file); `:986-1006` + generated verify script `:1649-1655` (matches English `fsutil bypassio` strings — Supported always false on non-English Windows); `:325, 983, 2916, 3702` + generated `:1647` (bare `powershell.exe`/`fsutil`/`shutdown.exe`/`explorer.exe` from an always-elevated script — outside both bare-name gates); `:3716-3776` (`BtnRefresh` runs `Invoke-PreflightChecks` + DISM/CIM/fsutil inline — 5-20 s window freeze the v3.4.6 background-runspace work eliminated for startup)
  Problem: Five self-contained legacy-artifact defects, grouped because they share one file and one implementation session.
  Evidence: Each line read directly; `Suspend-BitLocker` absence grep-verified.
  Fix: Append a trailing blank line to the kit `.reg`; delete the stale BitLocker sentence from the removal dialog; note the localization limit in fsutil-derived output (or parse `fsutil` exit codes instead of strings); absolute-path the four tool launches (`$env:SystemRoot\System32\...`); route BtnRefresh through the existing background-runspace preflight.
  Acceptance: Generated `.reg` ends with CRLF; dialog text matches actual behavior; tool launches absolute; Refresh keeps the window responsive.
  Confidence: Verified (except `.reg` regedit behavior: Needs-repro)
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

- [ ] P1 — Stop applying `Standalone_Future: 49453572`; it is Always Enabled on every sampled branch
  Why: The fallback set applies an override for a feature Windows already forces on. It cannot change behavior, but it widens the FeatureStore write, the mutation-ledger baseline, and the restore obligation on a boot-critical store — including the priority-8 reset defect already tracked above.
  Evidence: `49453572` appears under `## Always Enabled:` in all three dumps (26100.8687 line 5417, 26404.5000 line 4549, 29531.1000 line 5262); applied via `FallbackFeatureCatalog.NativeNvmeStack25H2`.
  Touches: `Models/FallbackFeatureCatalog.cs`, `Services/FeatureStoreWriterService.cs`, `Services/FallbackApplyService.cs`, `FallbackFeatureCatalogTests`, dialog/CLI strings deriving from `IdsDisplay`.
  Acceptance: The applied set for 26200+ is {55369237, 48433719}; `AllKnownIds` still includes 49453572 so evidence probes recognize a hand-applied override; a test asserts applied-set ≠ probe-set and documents why.
  Complexity: S

- [ ] P1 — Track `NativeNVMeStackEnableForClientOS: 48613417` as the candidate second gate on 26200+
  Why: `windows_build_rules.json` rules `26200-bind-blocked` and `post-26200-trains-bind-blocked` both cite ViVe issue #164 as evidence that no route exists; #164 was closed by its owner saying there is "another feature ID or registry key in the mix". `48613417` is `Disabled By Default` on the Rubidium branch alongside `NativeNVMeStackForGeClient`, and has zero references in this repo.
  Evidence: 29531.1000 dump lines 6920-6921 (between the `Disabled By Default:` and `Always Disabled:` headings); https://github.com/thebookisclosed/ViVe/issues/164; repo-wide grep for `48613417` returns 0 files.
  Touches: `Models/FallbackFeatureCatalog.cs`, `Services/FeatureStoreWriterService.cs` (probe), `Services/PatchVerificationService.cs`, `windows_build_rules.json` rule summaries, CLI `featurestore`.
  Acceptance: The FeatureStore evidence probe reports 48613417's current state on branches where it exists; the two `none-known` rule summaries name it as an untested candidate with its source. Applying it stays gated behind live validation (move to Roadmap_Blocked.md if hardware is required).
  Complexity: M

- [ ] P1 — Curate per-build feature IDs and default-state from velocity dumps into the existing data model
  Why: `FallbackFeatureCatalog.SelectForBuild` decides by `buildNumber >= 26200` and the build rules encode verdicts derived from press/forum reports. A per-branch primary source exists that gives name→ID *and* a default-state class; `Always Disabled` is the only honest basis for "no known route", and no sampled branch shows it for `NativeNVMeStackForGeClient`.
  Evidence: https://github.com/phantomofearth/windows-velocity-feature-lists (section headings `Always Enabled / Enabled By Default / Disabled By Default / Always Disabled`, verified by download). That repo carries **no LICENSE** — transcribe rows with `sourceUrl` + `lastReviewed` into the existing curated JSON; do not vendor the files and do not auto-download (RESEARCH.md rejects both).
  Touches: `windows_build_rules.json` (or a sibling `feature_ids.json` on the same provenance/freshness machinery), `Models/FallbackFeatureCatalog.cs`, `Services/WindowsBuildRulesService.cs`, `Services/DataFileProvenanceService.cs`, `scripts/Validate-BuildRulesFreshness.ps1`.
  Acceptance: `SelectForBuild` resolves from curated per-branch data rather than a `>= 26200` constant; a build whose feature is `Always Disabled` is reported distinctly from "unknown"; the freshness gate covers the new data file.
  Complexity: M

- [ ] P1 — Disclose that the ViVeTool fallback's upstream is abandoned
  Why: ViVe has had zero commits since 2025-03-10, its bundled dictionary stops at build 26236, and the canonical `PheeL-Pheel/ViVeTool-GUI` repo now 404s. The download is SHA-256-manifest gated so this is not an integrity hole, but the fallback dialog presents ViVeTool as a live escape hatch.
  Evidence: https://github.com/thebookisclosed/ViVe/releases; `Services/ViVeToolService.cs`; `ViewModels/MainViewModel.Commands.cs:482`.
  Touches: `Services/ViVeToolService.cs` (surface a last-release/dormancy field), fallback dialog copy in `MainViewModel.Commands.cs`, `Services/DocsService.cs`, README fallback section.
  Acceptance: The fallback confirmation names ViVeTool's last release date and states that the native FeatureStore path is primary and the ViVeTool path is a cross-check.
  Complexity: S

- [ ] P1 — Warn that this tool's own registry writes can break `vivetool /fullreset`
  Why: ViVe issue #166 (2026-07-30) reports `/fullreset` failing access-denied when values under `Policies\Microsoft\FeatureManagement\Overrides` are TrustedInstaller-owned. That is exactly the key `PatchService` writes, so the tool can wedge the user's independent escape hatch — and the residue probe does not look for it.
  Evidence: https://github.com/thebookisclosed/ViVe/issues/166; `Models/AppConfig.cs:52-53`; `Services/PatchService.cs:791`.
  Touches: `Services/PatchService.cs` (removal residue probe), `Services/RecoveryKitService.cs` (kit `.reg`/README), `Services/DocsService.cs`.
  Acceptance: Removal reports whether any override value remains under the Policies key with an owner the current user cannot rewrite, and the recovery kit documents the manual `takeown`/`reg delete` path for that case.
  Complexity: S

### P2

- [ ] P2 — `BypassIoInspectorService` is English-only; the same trap already logged for the legacy script
  Why: `InspectOne` regex-matches English `fsutil bypassio state` stdout (`RxBypassEnabled`, `RxStorageStack`), so on non-English Windows `Enabled` is always false and the gaming-impact warning silently degrades to a wrong answer. This is the C# twin of the P3 legacy-script fsutil item above — that item covers `NVMe_Driver_Patcher.ps1` only.
  Evidence: `src/NVMeDriverPatcher.Core/Services/BypassIoInspectorService.cs:55-96`; locale-independent alternative demonstrated by TheBeardofKnowledge's `nvmeSPEEDtweak.bat`, which reads `HKLM\SYSTEM\CurrentControlSet\Services\storport\Parameters\EnableBypassIO` directly.
  Touches: `Services/BypassIoInspectorService.cs`, `Services/DriveService.cs` (consumers), CLI `bypassio`, tests.
  Acceptance: BypassIO state is derived from the registry value (and device binding via `DEVPKEY_Device_Service`) rather than parsed prose; a test feeding non-English `fsutil` output still yields the correct verdict.
  Complexity: S

- [ ] P2 — Dependency currency pass with the two documented upgrade traps avoided
  Why: Six Microsoft packages sit at 10.0.9 against a 10.0.11 runtime; `System.Threading.AccessControl 10.0.0` is framework-provided and already emits NU1510; SkiaSharp 4.148.0 was dropped from the supported-stable tier and misses HarfBuzz 14.2.1 hardening.
  Evidence: csproj files read directly; https://github.com/mono/SkiaSharp/pull/4502; https://github.com/mono/SkiaSharp/releases/tag/v4.150.0. Traps: `SQLitePCLRaw.bundle_e_sqlite3` **3.0.5** switches its native dep to a different package id (`SQLite` 3.53.4), orphaning the repo's `SourceGear.sqlite3` pin while `SqliteVersionTests` still passes — use **3.0.4**. LiveCharts 2.0.5 declares SkiaSharp 2.88.9 / Views.WPF 3.119.0 against the forced 4.148.0, and 4.150.0 turned pre-v4 obsolete APIs into errors, so a break surfaces only as a runtime `MissingMethodException`.
  Touches: `src/NVMeDriverPatcher.Core/NVMeDriverPatcher.Core.csproj`, `src/NVMeDriverPatcher/NVMeDriverPatcher.csproj`, `src/NVMeDriverPatcher.Watchdog/NVMeDriverPatcher.Watchdog.csproj`, `tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj`, CLAUDE.md native-pin notes.
  Acceptance: Microsoft packages at 10.0.11; `System.Threading.AccessControl` reference removed with no NU1510; bundle at 3.0.4 with the SourceGear pin still winning (assert the resolved native path, not just the version string); SkiaSharp at 4.150.2 with `ChartingSmokeTests` widened to touch the chart APIs LiveCharts actually calls.
  Complexity: M

- [ ] P2 — Make dependency auditing a build gate
  Why: `Directory.Build.props` sets no `NuGetAuditMode`/`NuGetAuditLevel` and there are no lock files, so `dotnet list package --vulnerable` being clean today is a snapshot nothing enforces — in a repo whose whole thesis is gated safety.
  Evidence: `Directory.Build.props` read in full; no `packages.lock.json` anywhere in the tree.
  Touches: `Directory.Build.props`, `scripts/Build-ReleaseArtifacts.ps1`.
  Acceptance: `<NuGetAuditMode>all</NuGetAuditMode>` with an appropriate `NuGetAuditLevel` set repo-wide; restore with a seeded vulnerable transitive fails the build; the release builder runs the audit explicitly.
  Complexity: S

- [ ] P2 — Add a desktop-profile (QD1/QD2) benchmark alongside the existing high-QD run
  Why: `CreateDiskSpdArguments` is one fixed profile (`-t4 -o16 -b4K` ≈ QD64). Measured native-stack gains are ~+65% 4K random read at high QD but **−2.6% on 4K random write**, and near-zero at QD1–QD2 — i.e. typical desktop use. The most-asked community question ("does this actually help *me*?") is unanswerable with the current single profile, and the tool's own before/after comparison currently flatters the patch.
  Evidence: `src/NVMeDriverPatcher.Core/Services/BenchmarkService.cs:388-404`; https://www.storagereview.com/review/windows-server-native-nvme.
  Touches: `Services/BenchmarkService.cs`, `Services/AutoBenchmarkService.cs` (baseline/compare shape), `Data/BenchmarkRecord.cs` + schema migration, `Views/BenchmarkComparisonView.xaml`, CLI `benchmark`/`compare-benchmarks`, README.
  Acceptance: A run records at least a QD1 4K random profile plus the existing high-QD profile; the comparison view and CLI report them separately; the summary states plainly when high-QD improves while QD1 does not.
  Complexity: M

- [ ] P2 — Use the driver's own ETW provider as first-party watchdog evidence
  Why: `EtwTraceService` wraps a generic `wpr` profile, so post-patch traces contain no native-stack-specific evidence. `nvmedisk.sys` publishes `Microsoft-Windows-NvmeDisk` `{9799276c-fb04-47e8-845e-36946045c218}`. (The classic `nvmedisk`/129 System-log source is already covered at `EventLogWatchdogService.cs:112` — this is the ETW half only.)
  Evidence: https://github.com/libyal/winevt-kb/blob/main/docs/sources/eventlog-providers/Provider-Microsoft-Windows-NvmeDisk.md; `Services/EtwTraceService.cs` (uses `DefaultProfile` only); repo-wide grep for the GUID returns 0 files.
  Touches: `Services/EtwTraceService.cs` (custom WPR profile or explicit provider list), `Services/DiagnosticsService.cs` bundle, docs.
  Acceptance: A post-patch trace enables the NvmeDisk provider when the native stack is bound, and the support bundle records whether the provider was present.
  Complexity: M

- [ ] P2 — Preflight: verify OS-native rollback (Point-in-Time Restore) and recovery (Quick Machine Recovery)
  Why: The tool gates rollback readiness on System Protection + `Checkpoint-Computer`, which does not capture user files/apps/certs. Point-in-Time Restore went GA in 2026 and does; Quick Machine Recovery is the OS's own answer to "can't boot after the change" and is **off by default on Pro/Enterprise** — exactly this tool's audience. Repo has zero references to either. `reagentc /SetRecoveryTestmode` proves the recovery path before any mutation.
  Evidence: https://techcommunity.microsoft.com/blog/windows-itpro-blog/point-in-time-restore-for-windows-11-is-now-generally-available/4508101; https://4sysops.com/archives/quick-machine-recovery-in-windows-11/; `Services/RecoveryProofGateService.cs:207-241`, `Services/WinReBcdPrepService.cs` (already shells `reagentc`).
  Touches: `Services/RecoveryProofGateService.cs`, `Services/PreflightService.cs`, `Services/WinReBcdPrepService.cs`, `Services/RecoveryKitFreshnessService.cs`, GUI recovery tab, CLI `preflight`.
  Acceptance: Preflight reports PiTR availability and the age of the newest restore point where the OS exposes it, and reports whether QMR is enabled; both advisory (never a new hard block) and named in the recovery-readiness summary.
  Complexity: M

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
