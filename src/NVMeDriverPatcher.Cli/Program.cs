using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Cli;

class Program
{
    static int Main(string[] args)
    {
        try
        {
            RecoverySafetyGateService.Reset();
            if (args is null || args.Length == 0)
            {
                PrintUsage();
                return 3;
            }

            var command = (args[0] ?? string.Empty).ToLowerInvariant().TrimStart('-', '/');
            if (command == "help" || command == "?" || command == "h")
            {
                PrintUsage();
                return 0;
            }
            if (command == "version" || command == "v")
            {
                Console.WriteLine($"NVMe Driver Patcher CLI v{AppConfig.AppVersion}");
                return 0;
            }
            if (!CliCommandRegistry.IsKnown(command))
                return Unknown(command);

            // Payload verification is read-only and must not initialize shared app state. Keep it
            // ahead of the in-process administrator gate for DLL-hosted support tooling; the
            // published EXE still carries the product-wide requireAdministrator manifest.
            if (command is "verify-payload" or "payload-integrity")
            {
                var payloadPath = args.Skip(1)
                    .Select(a => a ?? string.Empty)
                    .FirstOrDefault(a => a.StartsWith("--input=", StringComparison.OrdinalIgnoreCase))?
                    .Substring("--input=".Length);
                payloadPath ??= args.Skip(1)
                    .Select(a => a ?? string.Empty)
                    .FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
                var payloadJson = args.Any(a => a is not null &&
                    a.Equals("--json", StringComparison.OrdinalIgnoreCase));
                return VerifyPayloadCommand(payloadPath, payloadJson);
            }
            if (!PreflightService.IsRunningAsAdmin())
            {
                Console.Error.WriteLine("Administrator privileges are required for NVMe Driver Patcher CLI operations.");
                Console.Error.WriteLine("Run an elevated terminal, or launch the published EXE directly so Windows can honor its elevation manifest.");
                return 4;
            }

            var config = ConfigService.Load();
            // v4.5: config schema migration before anything else touches it.
            try
            {
                var (changed, migrationSummary) = ConfigMigrationService.Migrate(config);
                if (changed && !ConfigService.Save(config))
                    Console.Error.WriteLine("[WARNING] Config migration could not be saved — it will be re-attempted next run.");
            }
            catch { }
            // GPO overlay takes precedence over shared config.json so a pinned fleet policy
            // isn't quietly overridden by a local run of the CLI.
            var policyWatchdogSave = GpoPolicyService.ApplyTo(config, GpoPolicyService.Read());
            if (policyWatchdogSave is { Success: false })
                Console.Error.WriteLine("[WARNING] Watchdog Group Policy state is unavailable: " + policyWatchdogSave.Summary);
            LogRotationService.RotateAll(config);
            EventLogRegistrationService.EnsureRegistered();
            EventLogService.Initialize(config.WriteEventLog);

            // Recover any previous process that terminated after publishing Prepared/Applied but
            // before the reboot checkpoint became durable. RebootPending operations are left intact
            // for normal post-reboot verification.
            var interruptedRecovery = MutationLedgerService.RecoverInterrupted(
                config.WorkingDir,
                message => Console.WriteLine(message));
            if (!interruptedRecovery.Success)
            {
                RecoverySafetyGateService.ObserveInterruptedRecovery(interruptedRecovery);
                Console.Error.WriteLine("Mutation recovery failed: " + interruptedRecovery.Summary);
                return 5;
            }

            bool MatchesAny(string a, params string[] forms) =>
                forms.Any(f => a.Equals(f, StringComparison.OrdinalIgnoreCase));

            bool force = args.Any(a => a is not null && MatchesAny(a, "--force", "-f"));
            bool noRestart = args.Any(a => a is not null && MatchesAny(a, "--no-restart"));
            // Generic --force may override non-critical readiness (for example, no detected NVMe
            // drive or a missing optional recovery artifact). It never overrides typed critical
            // probes or the build-rule action policy. Overriding only the build policy requires
            // this explicit, interactive-only flag, and it can never auto-restart.
            bool forceUnsupportedBuild = args.Any(a => a is not null && MatchesAny(a, "--force-unsupported-build"));
            bool includeServerKeyOverride = args.Any(a => a is not null && MatchesAny(a, "--include-server-key"));
            bool excludeServerKeyOverride = args.Any(a => a is not null && MatchesAny(a, "--no-server-key"));
            bool safeMode = args.Any(a => a is not null && MatchesAny(a, "--safe", "--safe-mode"));
            bool fullMode = args.Any(a => a is not null && MatchesAny(a, "--full", "--full-mode"));

            // Allow CLI override of the persisted IncludeServerKey config so automation doesn't
            // need to first edit config.json. --no-server-key wins if both are passed by mistake.
            if (includeServerKeyOverride) config.IncludeServerKey = true;
            if (excludeServerKeyOverride) config.IncludeServerKey = false;

            // Reject conflicting mode flags instead of silently picking one — automation callers
            // deserve a clear error so the intended profile is written to the audit trail.
            if (safeMode && fullMode)
            {
                Console.Error.WriteLine("Error: --safe and --full cannot be combined. Pick one.");
                return 3;
            }
            if (safeMode) config.PatchProfile = PatchProfile.Safe;
            else if (fullMode) config.PatchProfile = PatchProfile.Full;

            bool json = args.Any(a => a is not null && MatchesAny(a, "--json"));
            bool dryRun = args.Any(a => a is not null && MatchesAny(a, "--dry-run", "--preview"));
            bool applyWinReInjection = args.Any(a => a is not null && MatchesAny(a, "--apply"));
            bool unattended = args.Any(a => a is not null && MatchesAny(a, "--unattended"));
            bool autoRevert = args.Any(a => a is not null && MatchesAny(a, "--auto-revert"));
            string? isoOut = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--output=".Length);
            string? inputPath = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--input=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--input=".Length);
            string? telemetryEndpoint = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--endpoint=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--endpoint=".Length);
            string? exportPath = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--export=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--export=".Length);
            string? importPath = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--import=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--import=".Length);
            string? currentBenchmarkPath = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--current=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--current=".Length);
            string? sourceArg = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--source=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--source=".Length);
            string? centralStoreArg = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--central-store=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--central-store=".Length);
            int thresholdArg = 15;
            var threshStr = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--threshold=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(threshStr) && int.TryParse(threshStr.Substring("--threshold=".Length), out var t)) thresholdArg = t;

            return command switch
            {
                "status" => StatusCommand(json),
                "apply" or "install" => dryRun ? DryRunCommand(config) : ApplyCommand(config, force, noRestart, unattended, forceUnsupportedBuild),
                "remove" or "uninstall" => RemoveCommand(config, noRestart),
                "disable-for-update" or "disable-for-firmware" => DisableForUpdateCommand(config, noRestart),
                "re-enable-after-update" or "reenable-after-update" => ReEnableAfterUpdateCommand(config, force, noRestart, unattended),
                "diagnostics" or "export-diagnostics" => DiagnosticsCommand(config),
                "bundle" or "export-bundle" or "support-bundle" => SupportBundleCommand(config),
                "fallback" or "vivetool-fallback" or "apply-fallback" => FallbackCommand(config, forceUnsupportedBuild),
                "recovery-kit" or "export-recovery-kit" => RecoveryKitCommand(config),
                "verify" => VerifyCommand(config),
                "recovery-proof" => RecoveryProofCommand(config, json),
                "preflight" or "critical-probes" => CriticalPreflightCommand(json),
                "dry-run" or "preview" => DryRunCommand(config),
                "watchdog" => autoRevert ? WatchdogAutoRevertCommand(config) : WatchdogCommand(config, json),
                "watchdog-service" or "service-status" => WatchdogServiceStateCommand(),
                "reliability" => ReliabilityCommand(config, json),
                "minidump" or "triage" => MinidumpCommand(config, json),
                "firmware" or "compat" => FirmwareCompatCommand(json),
                "scope" => ScopeCommand(config),
                "etw" => EtwCommand(config).GetAwaiter().GetResult(),
                "winpe" => WinPECommand(config, isoOut).GetAwaiter().GetResult(),
                "winpe-freshness" or "winpe-status" => WinPEFreshnessCommand(config, inputPath).GetAwaiter().GetResult(),
                "telemetry" => TelemetryCommand(config, telemetryEndpoint).GetAwaiter().GetResult(),
                "verifier-on" => VerifierOnCommand(),
                "verifier-off" => VerifierOffCommand(),
                "verifier-status" => VerifierStatusCommand(),
                "guardrails" => GuardrailsCommand(),
                "controllers" or "per-controller" => PerControllerCommand(json),
                "tail" or "events-tail" => EventTailCommand(),
                "physical-disks" => PhysicalDisksCommand(),
                "bypassio" => BypassIoCommand(args.Any(a => a is not null && MatchesAny(a, "--history")), json),
                "apst" => ApstCommand(),
                "identify" => IdentifyCommand(),
                "config-export" => ConfigExportCommand(config, exportPath),
                "config-import" => ConfigImportCommand(config, importPath),
                "tuning-export" => TuningExportCommand(exportPath),
                "tuning-import" => TuningImportCommand(importPath),
                "compare-benchmarks" => CompareBenchmarksCommand(config, thresholdArg, currentBenchmarkPath),
                "compat-checksum" => CompatChecksumCommand(config),
                "verify-backup" => VerifyBackupCommand(config, importPath),
                "register-tasks" => RegisterTasksCommand(),
                "unregister-tasks" => UnregisterTasksCommand(),
                "policy-install" => PolicyInstallCommand(sourceArg, centralStoreArg),
                "policy-uninstall" => PolicyUninstallCommand(sourceArg, centralStoreArg),
                "portable-enable" => PortableEnableCommand(),
                "portable-disable" => PortableDisableCommand(),
                "update-check" => UpdateCheckCommand().GetAwaiter().GetResult(),
                "winre" => WinReCommand(),
                "winre-inject" or "inject-winre" => WinReInjectCommand(config, applyWinReInjection).GetAwaiter().GetResult(),
                "featurestore" or "feature-store" => FeatureStoreCommand(
                    config,
                    args.Any(a => a is not null && a.Equals("--write-native", StringComparison.OrdinalIgnoreCase)),
                    args.Any(a => a is not null && a.Equals("--reset-native", StringComparison.OrdinalIgnoreCase)),
                    json,
                    forceUnsupportedBuild),
                "kit-freshness" or "recovery-kit-freshness" => RecoveryKitFreshnessCommand(config),
                "docs" or "help-topic" => DocsCommand(args.Skip(1).FirstOrDefault()),
                "clean-data" => CleanDataCommand(config),
                "dashboard" or "html-report" => DashboardCommand(config),
                "fw-nudge" or "firmware-nudge" => FirmwareNudgeCommand(args.Skip(1).FirstOrDefault(), args.Skip(2).FirstOrDefault()),
                "safemode-verify" => SafeModeVerifyCommand(config),
                "upgrade-safeboot" or "safeboot-upgrade" => UpgradeSafeBootCommand(),
                "accessibility" or "a11y" => AccessibilityCommand(),
                "maintenance-window" or "window" => MaintenanceWindowCommand(config),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            // Surface any unexpected CLI error so scripted callers get a non-zero exit + reason.
            Console.Error.WriteLine($"Unhandled error: {ex.GetType().Name}: {ex.Message}");
            return 99;
        }
    }

    // Routing validation is derived from CliCommandRegistry — no more hand-maintained parallel list.
    // The switch expression in Main still dispatches to concrete handler methods (refactoring that
    // into dynamic dispatch would add complexity without improving safety).

    static int VerifyPayloadCommand(string? payloadPath, bool json)
    {
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            Console.Error.WriteLine("A payload directory or ZIP is required: --input=<path>");
            return 3;
        }

        var result = GeneratedArtifactManifestService.Verify(payloadPath);
        if (json)
        {
            var body = new
            {
                success = result.Success,
                payloadPath = result.PayloadPath,
                payloadType = result.PayloadType,
                schemaVersion = result.SchemaVersion,
                summary = result.Summary,
                issues = result.Issues.Select(i => new
                {
                    kind = i.Kind.ToString(),
                    relativePath = i.RelativePath,
                    detail = i.Detail
                })
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(body,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine(result.Summary);
            foreach (var issue in result.Issues)
                Console.Error.WriteLine($"  [{issue.Kind}] {issue.RelativePath}: {issue.Detail}");
        }
        return result.Success ? 0 : 1;
    }

    static int DocsCommand(string? topic)
    {
        Console.WriteLine(DocsService.Render(topic ?? "index"));
        return 0;
    }

    static int CleanDataCommand(AppConfig config)
    {
        var result = CleanDataService.Clean(config);
        Console.WriteLine(result.Summary);
        foreach (var e in result.Errors) Console.Error.WriteLine($"  [WARN] {e}");
        return result.Success ? 0 : 1;
    }

    static int DashboardCommand(AppConfig config)
    {
        var preflight = PreflightService.RunAll();
        var verification = PatchVerificationService.Evaluate(config);
        var watchdog = EventLogWatchdogService.Evaluate(config);
        var reliability = ReliabilityService.GetCorrelation(verification.PatchAppliedAt);
        var minidump = MinidumpTriageService.Analyze(verification.PatchAppliedAt);
        var guardrails = SystemGuardrailsService.Evaluate();
        var controllers = PerControllerAuditService.Audit();
        var html = HtmlDashboardService.Render(config, preflight, verification, watchdog, reliability, minidump, guardrails, controllers);
        var path = HtmlDashboardService.SaveTo(config, html);
        Console.WriteLine($"Dashboard written to: {path}");
        return 0;
    }

    static int FirmwareNudgeCommand(string? model, string? firmware)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            // Emit nudges for every NVMe drive the preflight sees.
            var preflight = PreflightService.RunAll();
            foreach (var d in preflight.CachedDrives.Where(d => d.IsNVMe))
            {
                var driveName = d.Name ?? string.Empty;
                var fw = preflight.DriverInfo?.FirmwareVersions.TryGetValue(driveName, out var f) == true ? f : string.Empty;
                var nudge = FirmwareUpdateNudgeService.Lookup(driveName, fw);
                Console.WriteLine($"  {driveName}  ({fw})  -> {nudge.Summary}");
            }
            return 0;
        }
        var single = FirmwareUpdateNudgeService.Lookup(model, firmware ?? string.Empty);
        Console.WriteLine(single.Summary);
        Console.WriteLine($"  Vendor: {single.Vendor}");
        Console.WriteLine($"  URL:    {single.UpdateToolUrl}");
        return 0;
    }

    static int UpgradeSafeBootCommand()
    {
        var report = SafeBootUpgradeService.Evaluate();
        Console.WriteLine("SafeBoot Entry Upgrade (KB5079391)");
        Console.WriteLine("==================================");
        Console.WriteLine(report.Summary);
        if (!report.UpgradeNeeded)
            return 0;
        var (success, message) = SafeBootUpgradeService.UpgradeEntries(Console.WriteLine);
        Console.WriteLine(message);
        return success ? 0 : 1;
    }

    static int SafeModeVerifyCommand(AppConfig config)
    {
        var path = SafeModeVerifyScriptService.Generate(config);
        Console.WriteLine($"Safe Mode verify script written to: {path}");
        Console.WriteLine("Boot into Safe Mode and run the script to confirm nvmedisk.sys bound cleanly.");
        return 0;
    }

    static int AccessibilityCommand()
    {
        var snap = AccessibilityService.Probe();
        Console.WriteLine(snap.Summary);
        Console.WriteLine($"  HighContrast={snap.HighContrastActive}  Narrator={snap.NarratorInstalled}  ReducedMotion={snap.ReducedMotion}  TextScale={snap.TextScalePercent:0}%");
        return 0;
    }

    static int MaintenanceWindowCommand(AppConfig config)
    {
        var window = MaintenanceWindowService.Load(config);
        Console.WriteLine(MaintenanceWindowService.Summarize(window));
        Console.WriteLine($"  InWindow now: {MaintenanceWindowService.IsInWindow(window)}");
        Console.WriteLine($"  Config file : {MaintenanceWindowService.WindowPath(config)}");
        return 0;
    }

    static int ApplyCommand(AppConfig config, bool force, bool noRestart, bool unattended, bool forceUnsupportedBuild = false)
    {
        // v4.5: unattended mode = silent apply + auto-reboot (if --no-restart is not also set).
        // Unattended implies SkipWarnings for the run.
        if (unattended) config.SkipWarnings = true;
        return ApplyCommandInner(config, force, noRestart, autoRestart: unattended && !noRestart, forceUnsupportedBuild);
    }

    static int WatchdogAutoRevertCommand(AppConfig config)
    {
        var outcome = AutoRevertService.MaybeRun(config, msg => Console.WriteLine(msg));
        RecoverySafetyGateService.ObserveAutoRevert(outcome);
        Console.WriteLine(outcome.Summary);
        // Same boot-task surface handles the one-shot FeatureStore fallback reset (moved out of the
        // now-pure PatchVerificationService.Evaluate so dashboard/telemetry render can't trigger it).
        var recovery = FallbackRecoveryCoordinator.RunOnce(config, msg => Console.WriteLine(msg));
        if (recovery.Attempted)
            Console.WriteLine($"Fallback recovery: {recovery.Summary}");
        return outcome.Executed && !outcome.Success ? 1 : 0;
    }

    static int GuardrailsCommand()
    {
        var report = SystemGuardrailsService.Evaluate();
        Console.WriteLine("System guardrails");
        Console.WriteLine("=================");
        Console.WriteLine(report.Summary);
        foreach (var f in report.Findings)
            Console.WriteLine($"  [{f.Severity}] {f.Name} — {f.Detail}");
        return report.HasBlocker ? 1 : 0;
    }

    static int PerControllerCommand(bool json = false)
    {
        var report = PerControllerAuditService.Audit();
        if (json)
        {
            Console.WriteLine(CliJson.Serialize("controllers", CliJson.BuildControllers(report)));
            return 0;
        }
        Console.WriteLine("Per-controller audit");
        Console.WriteLine("====================");
        Console.WriteLine(report.Summary);
        Console.WriteLine($"Observed UTC: {report.ObservedAtUtc:o}");
        foreach (var c in report.Controllers)
        {
            Console.WriteLine($"  {(c.IsNative ? "[NATIVE] " : "[LEGACY] ")}{c.FriendlyName}  driver={c.BoundDriver}  id={c.InstanceId}");
            Console.WriteLine($"      inf={c.InfName}  provider={c.DriverProvider}  version={c.BoundDriverVersion}  class={c.DeviceClass}");
            if (!c.DriverCandidateProbeSucceeded)
            {
                Console.WriteLine($"      candidate-error={c.DriverCandidateProbeError}");
                continue;
            }
            foreach (var candidate in c.DriverCandidates)
                Console.WriteLine($"      candidate rank={candidate.Rank} inf={candidate.InfName} provider={candidate.Provider} version={candidate.DriverVersion} match={candidate.MatchingDeviceId} status={candidate.Status}");
        }
        if (report.NativeCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine("PnP driver-method evidence (official rollout vs forced install):");
            Console.WriteLine(report.RenderForcedDriverEvidence());
        }
        return 0;
    }

    static int EventTailCommand()
    {
        var records = EventLogTailService.Recent();
        foreach (var r in records)
            Console.WriteLine($"{r.TimestampUtc:u}  [{r.Level,-8}] {r.Provider}/{r.EventId}  {r.Message}");
        return 0;
    }

    static int PhysicalDisksCommand()
    {
        var list = PhysicalDiskTelemetryService.Collect();
        foreach (var d in list)
            Console.WriteLine($"  {d.FriendlyName}  health={d.HealthStatus}  media={d.MediaType}  bus={d.BusType}  temp={d.Temperature}C  wear={d.Wear}  PoH={d.PowerOnHours}  R-errs={d.ReadErrorsUncorrected}  W-errs={d.WriteErrorsUncorrected}");
        return 0;
    }

    static int BypassIoCommand(bool showHistory, bool json = false)
    {
        if (json)
        {
            Console.WriteLine(CliJson.Serialize("bypassio", CliJson.BuildBypassIo(DriveService.GetBypassIOStatus())));
            return 0;
        }
        Console.WriteLine("Current per-volume BypassIO state:");
        var list = BypassIoInspectorService.Inspect();
        foreach (var v in list)
        {
            var icon = v.Enabled ? "[ON]" : "[OFF]";
            Console.WriteLine($"  {v.Letter}  {icon}  stack={v.Stack}");
        }

        Console.WriteLine($"  {BypassIoInspectorService.BuildGamingImpactSummary(list)}");

        if (showHistory)
        {
            Console.WriteLine();
            Console.WriteLine("BypassIO history (pre/post patch snapshots):");
            var (pre, post) = DataService.GetBypassIoLatestPair();
            if (pre.Count == 0 && post.Count == 0)
            {
                Console.WriteLine("  No history recorded yet. Apply or remove the patch to capture snapshots.");
            }
            else
            {
                if (pre.Count > 0)
                {
                    Console.WriteLine($"  Pre-patch ({pre[0].Timestamp:u}):");
                    foreach (var r in pre.Where(r => r.Timestamp == pre[0].Timestamp))
                        Console.WriteLine($"    {r.VolumeLetter}  {(r.Enabled ? "[ON]" : "[OFF]")}  stack={r.Stack}");
                }
                if (post.Count > 0)
                {
                    Console.WriteLine($"  Post-patch ({post[0].Timestamp:u}):");
                    foreach (var r in post.Where(r => r.Timestamp == post[0].Timestamp))
                        Console.WriteLine($"    {r.VolumeLetter}  {(r.Enabled ? "[ON]" : "[OFF]")}  stack={r.Stack}");
                }

                var preVolumes = pre.Where(r => r.Timestamp == pre.FirstOrDefault()?.Timestamp).ToList();
                var postVolumes = post.Where(r => r.Timestamp == post.FirstOrDefault()?.Timestamp).ToList();
                var lost = preVolumes.Where(p => p.Enabled)
                    .Where(p => postVolumes.Any(q => q.VolumeLetter == p.VolumeLetter && !q.Enabled))
                    .Select(p => p.VolumeLetter).ToList();
                if (lost.Count > 0)
                    Console.WriteLine($"  Volumes that LOST BypassIO after patching: {string.Join(", ", lost)}");
            }
        }

        return 0;
    }

    static int ApstCommand()
    {
        var report = ApstInspectorService.Inspect();
        Console.WriteLine(report.Summary);
        foreach (var s in report.States)
            Console.WriteLine($"  PS{s.PowerStateNumber}  idle={s.IdleTimeMicroseconds}us  entry={s.EntryLatencyUs}us  exit={s.ExitLatencyUs}us  nonOp={s.NonOperational}");
        if (report.BatteryEstimate is { } est)
        {
            Console.WriteLine();
            Console.WriteLine("Battery impact estimate:");
            Console.WriteLine($"  System type:    {(est.IsLaptop ? "Laptop" : "Desktop")}");
            Console.WriteLine($"  APST honored:   {(est.ApstHonored ? "Yes" : "No")}");
            if (est.ActivePowerWatts.HasValue) Console.WriteLine($"  Active power:   {est.ActivePowerWatts:F2}W");
            if (est.LowestIdlePowerWatts.HasValue) Console.WriteLine($"  Lowest idle:    {est.LowestIdlePowerWatts:F2}W");
            if (est.EstimatedIdleSavingsWatts.HasValue) Console.WriteLine($"  Idle savings:   ~{est.EstimatedIdleSavingsWatts:F1}W (lost after patching)");
            Console.WriteLine($"  Impact:         {est.Impact}");
            Console.WriteLine($"  Recommendation: {est.Recommendation}");
        }
        return 0;
    }

    static int IdentifyCommand()
    {
        bool found = false;
        for (int n = 0; n < 16; n++)
        {
            var r = NvmeIdentifyService.Query(n);
            if (!r.Success) continue;
            found = true;
            Console.WriteLine($"PhysicalDrive{n}:");
            Console.WriteLine($"  Model:        {r.ModelNumber.Trim()}");
            Console.WriteLine($"  Serial:       {r.SerialNumber.Trim()}");
            Console.WriteLine($"  Firmware:     {r.FirmwareRevision.Trim()}");
            Console.WriteLine($"  Vendor:       {r.VendorId} / Sub: {r.SubsystemVendorId}");
            Console.WriteLine($"  Namespaces:   {r.NumberOfNamespaces}");
            Console.WriteLine($"  Features:     FW-download={r.SupportsFirmwareDownload}  Format={r.SupportsFormatNvm}  NS-mgmt={r.SupportsNamespaceMgmt}  VWC={r.VolatileWriteCache}");
            Console.WriteLine($"  Power states: {r.NumberOfPowerStates}");
            foreach (var ps in r.PowerStates)
            {
                var opLabel = ps.NonOperational ? "non-op" : "op";
                Console.WriteLine($"    PS{ps.Index}: {ps.MaxPowerWatts:F4}W  entry={ps.EntryLatencyUs}us  exit={ps.ExitLatencyUs}us  [{opLabel}]");
            }
        }
        if (!found) Console.WriteLine("No NVMe controllers responded to Identify Controller.");
        return 0;
    }

    static int ConfigExportCommand(AppConfig config, string? exportPath)
    {
        if (string.IsNullOrWhiteSpace(exportPath))
            exportPath = Path.Combine(config.WorkingDir, "config_bundle.json");
        var path = ConfigImportExportService.Export(config, exportPath);
        Console.WriteLine($"Config bundle exported to: {path}");
        return 0;
    }

    static int ConfigImportCommand(AppConfig config, string? importPath)
    {
        if (string.IsNullOrWhiteSpace(importPath))
        {
            Console.Error.WriteLine("--import=<path> required.");
            return 3;
        }
        var (ok, summary) = ConfigImportExportService.Import(importPath, config);
        Console.WriteLine(summary);
        return ok ? 0 : 1;
    }

    static int TuningExportCommand(string? exportPath)
    {
        var profile = TuningService.GetCurrentParameters();
        var path = string.IsNullOrWhiteSpace(exportPath) ? "nvme_tuning_profile.json" : exportPath;
        TuningProfileIoService.Export(profile?.Name ?? "Current", profile ?? new Models.TuningProfile(), path);
        Console.WriteLine($"Tuning profile exported to: {path}");
        return 0;
    }

    static int TuningImportCommand(string? importPath)
    {
        if (string.IsNullOrWhiteSpace(importPath))
        {
            Console.Error.WriteLine("--import=<path> required.");
            return 3;
        }
        var (profile, summary) = TuningProfileIoService.Import(importPath);
        Console.WriteLine(summary);
        if (profile is null) return 1;
        bool ok = TuningService.ApplyProfile(profile, Console.WriteLine);
        return ok ? 0 : 1;
    }

    static int CompareBenchmarksCommand(AppConfig config, int threshold, string? currentPath)
    {
        var baseline = AutoBenchmarkService.LoadBaseline(config);
        if (baseline is null)
        {
            Console.Error.WriteLine("No baseline present. Run a benchmark first.");
            return 1;
        }

        // Compare against a real "current" benchmark: an explicit --current=<path>, else the most
        // recent recorded benchmark in history. Comparing the baseline to itself (the old behavior)
        // could never detect a regression.
        BenchmarkBaseline? current;
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            current = AutoBenchmarkService.LoadBaselineFromPath(currentPath);
            if (current is null)
            {
                Console.Error.WriteLine($"Could not read a benchmark from --current={currentPath}.");
                return 3;
            }
        }
        else
        {
            var latest = BenchmarkService.GetHistory(config.WorkingDir)
                .OrderByDescending(h => h.Timestamp, StringComparer.Ordinal)
                .FirstOrDefault();
            current = latest is null ? null : AutoBenchmarkService.FromResult(latest);
        }

        if (current is null)
        {
            Console.Error.WriteLine("No current benchmark to compare. Run a benchmark after applying the patch, or pass --current=<path>.");
            return 1;
        }

        var verdict = AutoBenchmarkService.Compare(baseline, current, threshold);
        Console.WriteLine(verdict.Summary);
        return verdict.Regressed ? 1 : 0;
    }

    static int CompatChecksumCommand(AppConfig config)
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "compat.json");
        var local = Path.Combine(config.WorkingDir, "compat.json");
        var r = CompatChecksumService.Verify(local, shipped);
        Console.WriteLine(r.Summary);
        Console.WriteLine($"  sha256={r.Sha256}");
        Console.WriteLine($"  shipped={r.ShippedSha256}");
        return r.ShippedDefault ? 0 : 2;
    }

    static int VerifyBackupCommand(AppConfig config, string? backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            backupPath = Directory.EnumerateFiles(config.WorkingDir, "Pre_*_Backup_*.reg")
                .OrderByDescending(f => f).FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            Console.Error.WriteLine("No backup found in working dir and --import=<path> not supplied.");
            return 3;
        }
        var r = BackupIntegrityService.Verify(backupPath, msg => Console.WriteLine(msg));
        Console.WriteLine(r.Summary);
        return r.Success ? 0 : 1;
    }

    static int RegisterTasksCommand()
    {
        var cliExe = Environment.ProcessPath ?? "NVMeDriverPatcher.Cli.exe";
        bool boot = SchedulerService.RegisterBootVerify(cliExe, Console.WriteLine);
        bool sweep = SchedulerService.RegisterWatchdogSweep(cliExe, 60, Console.WriteLine);
        return boot && sweep ? 0 : 1;
    }

    static int UnregisterTasksCommand()
    {
        bool a = SchedulerService.Unregister(SchedulerService.BootTaskName, Console.WriteLine);
        bool b = SchedulerService.Unregister(SchedulerService.WatchdogTaskName, Console.WriteLine);
        return a && b ? 0 : 1;
    }

    static int PortableEnableCommand()
    {
        return PortableModeService.Enable(Console.WriteLine) ? 0 : 1;
    }

    static int PortableDisableCommand()
    {
        return PortableModeService.Disable(Console.WriteLine) ? 0 : 1;
    }

    static int WinReCommand()
    {
        var info = WinReBcdPrepService.Probe();
        Console.WriteLine(info.Summary);
        if (!string.IsNullOrEmpty(info.WinReLocation)) Console.WriteLine($"  Location: {info.WinReLocation}");
        if (!string.IsNullOrEmpty(info.ImagePath))    Console.WriteLine($"  ImagePath: {info.ImagePath}");
        if (!string.IsNullOrEmpty(info.DeviceGuid))   Console.WriteLine($"  BCD GUID : {info.DeviceGuid}");
        return info.WinReEnabled ? 0 : 1;
    }

    static int FeatureStoreCommand(
        AppConfig config,
        bool writeNative,
        bool resetNative,
        bool json,
        bool forceUnsupportedBuild)
    {
        bool hasFallback = FeatureStoreWriterService.HasFallbackEvidence();
        var configurations = FeatureStoreWriterService.QueryAllKnownConfigurations();

        if (json && !writeNative && !resetNative)
        {
            Console.WriteLine(CliJson.Serialize("featurestore", CliJson.BuildFeatureStore(hasFallback, configurations)));
            return 0;
        }

        Console.WriteLine($"FeatureStore fallback evidence: {(hasFallback ? "PRESENT" : "not detected")}");
        Console.WriteLine();
        Console.WriteLine("Per-ID configuration (ntdll RtlQueryFeatureConfiguration):");
        foreach (var state in configurations)
        {
            var desc = state.Found
                ? $"state={state.EnabledState switch { 2 => "Enabled", 1 => "Disabled", _ => "Default" }} priority={state.Priority}"
                : "no configuration";
            Console.WriteLine($"  {state.FeatureId,10}  [{state.Store,-7}]  {desc}");
        }
        var exportPath = Path.Combine(config.WorkingDir, "featurestore_snapshot.bin");
        var snapshot = FeatureStoreWriterService.ExportBlob(exportPath);
        Console.WriteLine();
        Console.WriteLine(snapshot is null
            ? "No FeatureStore blob to export."
            : $"FeatureStore blob exported to: {snapshot}");

        if (writeNative)
        {
            var policy = BuildActionPolicyService.EvaluateCurrent(config.WorkingDir);
            if (!policy.MutationAllowed && !forceUnsupportedBuild)
            {
                Console.Error.WriteLine("BUILD POLICY: " + policy.Reason);
                Console.Error.WriteLine("Native FeatureStore write refused; use --force-unsupported-build only from an interactive console.");
                return 1;
            }
            var probes = CriticalEnvironmentProbeService.EvaluateFeatureStoreFallback();
            if (!probes.AllPassed)
            {
                PrintCriticalProbeFailures(probes);
                return probes.ExitCode;
            }

            // EXPERIMENTAL: native enable via RtlSetFeatureConfigurations — same write
            // ViVeTool performs, without the external download. It still uses the same
            // transaction ledger, BitLocker proof, and critical environment gate as fallback.
            var idSet = ViVeToolService.SelectFallbackSet();
            Console.WriteLine();
            Console.WriteLine($"EXPERIMENTAL native write: enabling set '{idSet.Name}' ({idSet.IdsDisplay})...");
            var write = FallbackApplyService.ApplyNativeOnlyAsync(
                    config.WorkingDir,
                    Console.WriteLine,
                    allowUnsupportedBuild: forceUnsupportedBuild)
                .GetAwaiter().GetResult();
            Console.WriteLine(write.Message);
            if (write.Success)
            {
                PatchVerificationService.MarkPending(config, isFallback: true);
                bool checkpointSaved = ConfigService.Save(config) &&
                    MutationLedgerService.MarkRebootPending(config.WorkingDir, write.MutationOperationId, Console.WriteLine);
                if (!checkpointSaved)
                {
                    MutationLedgerService.RestoreOriginalState(config.WorkingDir, Console.WriteLine);
                    Console.Error.WriteLine("Native FeatureStore write landed but its reboot checkpoint was not durable; exact restore was attempted.");
                    return 1;
                }
                Console.WriteLine("Post-reboot verification armed. Restart to activate.");
            }
            return write.Success ? 0 : 1;
        }

        if (resetNative)
        {
            // Explicit undo of the native/ViVeTool fallback enablement (ViVeTool /reset).
            Console.WriteLine();
            Console.WriteLine("Resetting native FeatureStore fallback overrides to default...");
            var reset = FeatureStoreWriterService.ResetAppliedFallback();
            Console.WriteLine(reset.Summary);
            foreach (var s in reset.IdStatuses)
            {
                Console.WriteLine($"  {s.FeatureId,10}  Runtime: {(s.RuntimeEnabled ? "still enabled" : "cleared"),-13}  Boot: {(s.BootEnabled ? "still enabled" : "cleared")}");
            }
            return reset.Success ? 0 : 1;
        }

        return 0;
    }

    static int RecoveryKitFreshnessCommand(AppConfig config)
    {
        var report = RecoveryKitFreshnessService.Evaluate(config);
        Console.WriteLine(report.Summary);
        return report.ShouldNag ? 1 : 0;
    }

    static async Task<int> UpdateCheckCommand()
    {
        var (url, name, tag) = await AutoUpdaterService.FetchLatestAssetAsync(AppConfig.GitHubApiReleasesUrl);
        if (url is null || name is null)
        {
            Console.WriteLine("No suitable release asset found.");
            return 1;
        }
        Console.WriteLine($"Latest release: {tag}");
        Console.WriteLine($"Asset: {name}");
        Console.WriteLine($"URL: {url}");
        return 0;
    }

    static int DryRunCommand(AppConfig config)
    {
        // Preview never mutates, so it is always allowed — but state the disposition honestly so a
        // user previewing a build with no known path isn't misled into thinking apply would work.
        var policy = BuildActionPolicyService.EvaluateCurrent(config.WorkingDir);
        Console.WriteLine($"Build policy: {(policy.MutationAllowed ? "apply allowed" : "verify/rollback only")} — {policy.Reason}");
        var preflight = PreflightService.RunAll();
        var report = DryRunService.PlanInstall(config, preflight);
        Console.WriteLine();
        Console.WriteLine(DryRunService.RenderMarkdown(report));
        return report.PreflightBlockers.Count > 0 ? 1 : 0;
    }

    static int WatchdogServiceStateCommand()
    {
        var state = WatchdogServiceStateService.Query();
        Console.WriteLine("Real-time Watchdog Service");
        Console.WriteLine("==========================");
        Console.WriteLine($"Service: {WatchdogServiceStateService.ServiceName}");
        Console.WriteLine($"State:   {state} — {WatchdogServiceStateService.Describe(state)}");
        if (state == WatchdogServiceState.NotInstalled)
        {
            Console.WriteLine();
            Console.WriteLine("The service is opt-in. Install (as admin) with:");
            Console.WriteLine("  NVMeDriverPatcher.Watchdog.exe /install");
            Console.WriteLine("or select the WatchdogService MSI feature (ADDLOCAL=WatchdogService).");
        }
        // Exit codes: 0 running, 2 stopped, 3 not installed, 4 unknown/pending —
        // lets fleet scripts branch on service health without parsing output.
        return state switch
        {
            WatchdogServiceState.Running => 0,
            WatchdogServiceState.Stopped => 2,
            WatchdogServiceState.NotInstalled => 3,
            _ => 4,
        };
    }

    static int WatchdogCommand(AppConfig config, bool json = false)
    {
        var report = EventLogWatchdogService.Evaluate(config);
        if (json)
        {
            Console.WriteLine(CliJson.Serialize("watchdog", CliJson.BuildWatchdog(report)));
            return report.Verdict switch
            {
                WatchdogVerdict.Unstable => 1,
                WatchdogVerdict.Warning => 2,
                WatchdogVerdict.Unavailable => 3,
                _ => 0
            };
        }
        Console.WriteLine("NVMe Driver Watchdog");
        Console.WriteLine("====================");
        Console.WriteLine($"Verdict: {report.Verdict}");
        Console.WriteLine($"Realtime service: {WatchdogServiceStateService.Describe(WatchdogServiceStateService.Query())}");
        Console.WriteLine(report.Summary);
        Console.WriteLine();
        Console.WriteLine(report.Detail);
        // Non-zero exit on Unstable so CI/CD and Task Scheduler jobs can react.
        return report.Verdict switch
        {
            WatchdogVerdict.Unstable => 1,
            WatchdogVerdict.Warning => 2,
            WatchdogVerdict.Unavailable => 3,
            _ => 0
        };
    }

    static int ReliabilityCommand(AppConfig config, bool json)
    {
        DateTime? patchTs = null;
        if (!string.IsNullOrWhiteSpace(config.PendingVerificationSince) &&
            DateTime.TryParse(config.PendingVerificationSince,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            patchTs = ts;
        var report = ReliabilityService.GetCorrelation(patchTs);
        if (json)
        {
            Console.WriteLine(CliJson.Serialize("reliability", CliJson.BuildReliability(report)));
            return report.DataAvailable ? 0 : 1;
        }
        Console.WriteLine("Reliability Monitor correlation");
        Console.WriteLine("===============================");
        Console.WriteLine(report.Summary);
        foreach (var p in report.Series.TakeLast(20))
            Console.WriteLine($"  {p.Timestamp:yyyy-MM-dd}  {p.Index:F1}");
        return report.DataAvailable ? 0 : 1;
    }

    static int MinidumpCommand(AppConfig config, bool json)
    {
        DateTime? patchTs = null;
        if (!string.IsNullOrWhiteSpace(config.PendingVerificationSince) &&
            DateTime.TryParse(config.PendingVerificationSince,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            patchTs = ts;
        var report = MinidumpTriageService.Analyze(patchTs);
        if (json)
        {
            Console.WriteLine(CliJson.Serialize("minidump", CliJson.BuildMinidump(report)));
            return report.NVMeRelated > 0 ? 1 : 0;
        }
        Console.WriteLine("Minidump triage");
        Console.WriteLine("===============");
        Console.WriteLine(report.Summary);
        foreach (var d in report.Dumps)
            Console.WriteLine($"  {(d.MentionsNVMeStack ? "[NVMe]" : "[....]")}  {d.CreatedUtc:u}  {d.FilePath}");
        return report.NVMeRelated > 0 ? 1 : 0;
    }

    static int FirmwareCompatCommand(bool json)
    {
        var db = FirmwareCompatService.LoadDatabase();
        if (json)
        {
            Console.WriteLine(CliJson.Serialize("firmware", CliJson.BuildFirmwareCompat(db)));
            return 0;
        }
        var provenance = DataFileProvenanceService.InspectFirmwareCompat();
        Console.WriteLine($"Firmware compat DB (schema {db.SchemaVersion}, updated {db.Updated}):");
        Console.WriteLine($"  Provenance: {provenance.Summary}");
        Console.WriteLine($"  Source: {provenance.ActivePath}");
        foreach (var e in db.Entries)
        {
            var meta = string.IsNullOrEmpty(e.Confidence) ? "" :
                $" ({e.Confidence}{(string.IsNullOrEmpty(e.LastReviewed) ? "" : $", reviewed {e.LastReviewed}")})";
            Console.WriteLine($"  [{e.Level}] {e.Controller} / {e.Firmware} — {e.Note}{meta}");
            if (!string.IsNullOrEmpty(e.SourceUrl))
                Console.WriteLine($"      source: {e.SourceUrl}");
        }
        if (db.CveAdvisories.Count > 0)
        {
            bool isServer = (DriveService.GetWindowsBuildDetails()?.Caption ?? string.Empty)
                .Contains("Server", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine();
            Console.WriteLine("NVMe-stack CVE advisories:");
            foreach (var a in db.CveAdvisories)
            {
                bool applies = isServer ? a.AffectsServer : a.AffectsClient;
                Console.WriteLine($"  [{(applies ? "APPLIES TO THIS OS" : "not applicable here")}] {a.Cve} ({a.Severity}) — fixed by {a.FixedBy}");
                Console.WriteLine($"      {a.Description}");
            }
        }
        return 0;
    }

    static int ScopeCommand(AppConfig config)
    {
        var scope = PerDriveScopeService.Load(config);
        var preflight = PreflightService.RunAll();
        var driveTuples = preflight.CachedDrives
            .Where(d => d.IsNVMe)
            .Select(d => (Serial: d.PNPDeviceID ?? string.Empty, Model: d.Name ?? string.Empty));
        var decisions = PerDriveScopeService.Decide(driveTuples, scope);
        Console.WriteLine(PerDriveScopeService.Summarize(decisions));
        foreach (var dec in decisions)
            Console.WriteLine($"  {(dec.Include ? "[include]" : "[EXCLUDE]")}  {dec.Model}  [{dec.Serial}]  {dec.Reason}");
        return 0;
    }

    static async Task<int> EtwCommand(AppConfig config)
    {
        Console.WriteLine("Capturing 60s ETW storage trace...");
        var pre = await EtwTraceService.CaptureAsync(config, EtwTracePhase.PrePatch, 60);
        Console.WriteLine(pre.Summary);
        return pre.Success ? 0 : 1;
    }

    static async Task<int> WinPECommand(AppConfig config, string? isoOut)
    {
        var options = new WinPEBuildOptions
        {
            OutputDir = string.IsNullOrWhiteSpace(isoOut) ? Path.Combine(config.WorkingDir, "winpe") : isoOut,
            RecoveryKitDir = config.LastRecoveryKitPath ?? Path.Combine(config.WorkingDir, "NVMe_Recovery_Kit")
        };
        var result = await WinPERecoveryBuilderService.BuildAsync(options, msg => Console.WriteLine(msg));
        Console.WriteLine(result.Summary);
        foreach (var controller in result.Controllers)
            Console.WriteLine($"  [{controller.Coverage}] {controller.FriendlyName} — {controller.InfName} {controller.DriverVersion} — {controller.Detail}");
        foreach (var w in result.Warnings) Console.WriteLine($"  [WARN] {w}");
        if (result.Success && !string.IsNullOrWhiteSpace(result.MediaRoot))
        {
            config.LastWinPEMediaPath = result.MediaRoot;
            if (!ConfigService.Save(config))
                Console.Error.WriteLine("[WARNING] WinPE media was built, but its path could not be saved for later freshness checks.");
            if (!string.IsNullOrWhiteSpace(result.ControllerReportPath))
                Console.WriteLine($"Controller report: {result.ControllerReportPath}");
        }
        return result.Success ? 0 : 1;
    }

    static async Task<int> WinPEFreshnessCommand(AppConfig config, string? inputPath)
    {
        var mediaPath = string.IsNullOrWhiteSpace(inputPath) ? config.LastWinPEMediaPath : inputPath;
        var recoveryKit = config.LastRecoveryKitPath ?? Path.Combine(config.WorkingDir, "NVMe_Recovery_Kit");
        var report = await WinPEMediaFreshnessService.EvaluateAsync(
            mediaPath ?? string.Empty,
            recoveryKit);
        Console.WriteLine(report.Summary);
        foreach (var reason in report.Reasons)
            Console.WriteLine($"  - {reason}");
        return report.State switch
        {
            WinPEMediaFreshness.Fresh => 0,
            WinPEMediaFreshness.Unknown => 2,
            _ => 1
        };
    }

    static async Task<int> TelemetryCommand(AppConfig config, string? endpoint)
    {
        var preflight = PreflightService.RunAll();
        var verification = PatchVerificationService.Evaluate(config);
        var watchdog = EventLogWatchdogService.Evaluate(config);
        var reliability = ReliabilityService.GetCorrelation(verification.PatchAppliedAt);
        var report = CompatTelemetryService.BuildReport(config, preflight, watchdog, reliability, verification, null);
        var path = CompatTelemetryService.SaveReport(config, report);
        Console.WriteLine($"Compat report saved to: {path}");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Console.WriteLine("(No --endpoint=<url> supplied — payload kept local. Review, then resubmit with --endpoint= to share.)");
            return 0;
        }
        if (!config.CompatTelemetryEnabled)
        {
            Console.WriteLine("[BLOCKED] Compatibility telemetry submission is disabled by Group Policy.");
            return 1;
        }
        var submission = await CompatTelemetryService.SubmitAsync(endpoint, report);
        Console.WriteLine(submission.Summary);
        return submission.Success ? 0 : 1;
    }

    static int VerifierOnCommand()
    {
        var result = DriverVerifierService.EnableForNVMeStack();
        Console.WriteLine(result.Summary);
        return result.Success ? 0 : 1;
    }

    static int VerifierOffCommand()
    {
        var result = DriverVerifierService.DisableAll();
        Console.WriteLine(result.Summary);
        return result.Success ? 0 : 1;
    }

    static int VerifierStatusCommand()
    {
        var status = DriverVerifierService.QueryStatus();
        Console.WriteLine(status.Summary);
        return 0;
    }

    static int StatusCommand(bool json = false)
    {
        var preflight = PreflightService.RunAll();
        var status = RegistryService.GetPatchStatus();

        if (json)
        {
            var native = preflight.NativeNVMeStatus;
            bool evidence = false;
            try { evidence = FeatureStoreWriterService.HasFallbackEvidence(); } catch { }
            var src = PatchVerificationService.ClassifyEnablementSource(native?.IsActive ?? false, status.Count, evidence);
            Console.WriteLine(CliJson.Serialize("status",
                CliJson.BuildStatus(status, native, src, WindowsBuildRulesService.MatchCurrent())));
            return status.Applied ? 0 : status.Partial ? 2 : 1;
        }

        Console.WriteLine();
        Console.WriteLine("NVMe Driver Patch Status");
        Console.WriteLine("========================");
        Console.WriteLine($"Components Applied: {status.Count}/{status.Total}");

        string statusStr = status.Applied ? "APPLIED" : status.Partial ? "PARTIAL" : "NOT APPLIED";
        Console.WriteLine($"Status: {statusStr}");

        if (status.Keys.Count > 0)
            Console.WriteLine($"Applied Keys: {string.Join(", ", status.Keys)}");

        if (preflight.NativeNVMeStatus is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Active Driver: {preflight.NativeNVMeStatus.ActiveDriver}");
            Console.WriteLine($"Device Category: {(preflight.NativeNVMeStatus.IsActive ? "Storage disks (native)" : "Disk drives (legacy)")}");

            bool evidence = false;
            try { evidence = FeatureStoreWriterService.HasFallbackEvidence(); } catch { }
            var source = PatchVerificationService.ClassifyEnablementSource(
                preflight.NativeNVMeStatus.IsActive, status.Count, evidence);
            Console.WriteLine($"Enablement source: {source switch
            {
                EnablementSource.Official => "untracked — official Windows rollout OR a forced 'driver method' install (no patch evidence)",
                EnablementSource.RegistryPatch => "this tool's registry patch",
                EnablementSource.FallbackFlags => "ViVeTool/FeatureStore fallback flags",
                _ => "none (driver not bound)"
            }}");
            if (source == EnablementSource.Official)
                Console.WriteLine($"  Note: {PatchVerificationService.UntrackedDriverActivationNote}");

            var rule = WindowsBuildRulesService.MatchCurrent();
            Console.WriteLine($"Build rule: {WindowsBuildRulesService.Describe(rule)}");
            Console.WriteLine("Data files:");
            foreach (var f in DataFileProvenanceService.InspectAll())
                Console.WriteLine($"  {f.Summary} source={f.ActivePath}");

            // Honest read-out of the blocked states — if keys/flags are written but the
            // driver never swapped, that's Microsoft's block, not a user error.
            if (!preflight.NativeNVMeStatus.IsActive)
            {
                bool fallbackEvidence = false;
                try { fallbackEvidence = FeatureStoreWriterService.HasFallbackEvidence(); } catch { }
                if (fallbackEvidence)
                {
                    // The fallback itself is active-but-ineffective: 26200.8524+ removed the
                    // GenNvmeDisk compatible ID, so nvmedisk.inf can never match (ViVe #164).
                    Console.WriteLine();
                    Console.WriteLine("NOTE: The ViVeTool/FeatureStore fallback flags are ENABLED but the driver did not bind.");
                    Console.WriteLine("      On builds 26200.8524+ stornvme no longer exposes the compatible ID nvmedisk.inf");
                    Console.WriteLine("      matches — there is currently NO working enablement path on this build.");
                    Console.WriteLine("      The flags are harmless; remove the patch or wait for Microsoft's official rollout.");
                }
                else if (status.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("NOTE: The feature flags are set but Windows is still loading the legacy driver.");
                    Console.WriteLine("      On post-block Insider builds (early 2026+) the override is a no-op.");
                    Console.WriteLine($"      Use 'fallback' to write FeatureStore IDs {ViVeToolService.SelectFallbackSet().IdsDisplay} natively first.");
                }
            }
        }

        var migration = preflight.CachedMigration;
        if (migration?.Migrated.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Drives on native NVMe:");
            foreach (var d in migration.Migrated) Console.WriteLine($"  + {d}");
        }
        if (migration?.Legacy.Count > 0)
        {
            Console.WriteLine("Drives on legacy stack:");
            foreach (var d in migration.Legacy) Console.WriteLine($"  - {d}");
        }

        if (preflight.IsLaptop)
            Console.WriteLine("\nWARNING: Laptop detected -- APST power management broken with native NVMe");

        if (preflight.IncompatibleSoftware.Count > 0)
        {
            Console.WriteLine("\nCompatibility warnings:");
            foreach (var sw in preflight.IncompatibleSoftware)
                Console.WriteLine($"  [{sw.Severity}] {sw.Name}: {sw.Message}");
        }

        Console.WriteLine();
        return status.Applied ? 0 : status.Partial ? 2 : 1;
    }

    static int ApplyCommandInner(AppConfig config, bool force, bool noRestart, bool autoRestart, bool forceUnsupportedBuild = false)
    {
        // Build-rule action policy is evaluated FIRST and is NOT bypassed by generic --force.
        // Mutating a build with no known binding path can only be done with the explicit,
        // interactive-only --force-unsupported-build flag, which also forbids auto-restart.
        var policy = BuildActionPolicyService.EvaluateCurrent(config.WorkingDir);
        if (!policy.MutationAllowed)
        {
            Console.Error.WriteLine($"BUILD POLICY: {policy.Reason}");
            if (!forceUnsupportedBuild)
            {
                Console.Error.WriteLine("Apply is refused on this build. This is verify/monitor/rollback territory.");
                Console.Error.WriteLine("Generic --force does NOT override this. If you understand the risk and are at an");
                Console.Error.WriteLine("interactive console, re-run with --force-unsupported-build (auto-restart is disabled).");
                return 1;
            }
            if (autoRestart)
            {
                Console.Error.WriteLine("--force-unsupported-build cannot be combined with unattended auto-restart. Aborting.");
                return 3;
            }
            noRestart = true; // never auto-restart into an unproven build
            Console.WriteLine("--force-unsupported-build specified: proceeding without a known binding path; restart is manual.");
        }

        var preflight = PreflightService.RunAll(msg => Console.WriteLine(msg));

        if (preflight.VeraCryptDetected)
        {
            Console.Error.WriteLine("BLOCKED: VeraCrypt system encryption detected. This safeguard cannot be bypassed.");
            return 1;
        }
        if (!PreflightService.AllCriticalPassed(preflight.Checks))
        {
            PrintCriticalProbeFailures(preflight.CriticalProbes);
            Console.Error.WriteLine("Critical safety failures and Unknown probe states cannot be overridden by --force.");
            return preflight.CriticalProbes.ExitCode;
        }
        if (!preflight.HasNVMeDrives && !force)
        {
            Console.Error.WriteLine("No NVMe drives detected. Use --force to apply anyway.");
            return 2;
        }

        var proof = RecoveryProofGateService.Evaluate(config);
        Console.WriteLine($"Recovery readiness: {proof.PassedCount}/{proof.TotalCount}");
        foreach (var item in proof.Items)
            Console.WriteLine($"  [{(item.Passed ? "OK" : "!!")}] {item.Label}: {item.Detail}");
        if (!proof.AllPassed && !force)
        {
            Console.Error.WriteLine("Recovery infrastructure is incomplete. Fix the items above or use --force to override.");
            return 1;
        }

        var result = PatchService.Install(
            config,
            preflight.NativeNVMeStatus,
            preflight.BypassIOStatus,
            msg => Console.WriteLine(msg),
            allowUnsupportedBuild: forceUnsupportedBuild);

        bool checkpointSaved = true;
        if (result.Success)
        {
            PatchVerificationService.MarkPending(config);
            // Arm the post-patch watchdog so post-reboot event-log distress signals auto-revert
            // (if the user opted into auto-revert in config / GPO).
            var watchdogCheckpoint = EventLogWatchdogService.Arm(config);
            if (!watchdogCheckpoint.Success)
                Console.Error.WriteLine("[ERROR] Watchdog checkpoint failed: " + watchdogCheckpoint.Summary);
            checkpointSaved = watchdogCheckpoint.Success && ConfigService.Save(config) &&
                MutationLedgerService.MarkRebootPending(
                    config.WorkingDir,
                    result.MutationOperationId,
                    message => Console.WriteLine(message));
            if (!checkpointSaved)
            {
                var restored = MutationLedgerService.RestoreOriginalState(config.WorkingDir, message => Console.WriteLine(message));
                PatchVerificationService.Clear(config, new VerificationReport { Outcome = VerificationOutcome.Reverted });
                var watchdogRollback = EventLogWatchdogService.Disarm(config);
                if (!watchdogRollback.Success)
                    Console.Error.WriteLine("[ERROR] Rollback restored the patch state, but the watchdog checkpoint is still unavailable: " + watchdogRollback.Summary);
                ConfigService.Save(config);
                Console.Error.WriteLine(
                    restored.Success
                        ? "[ERROR] Patch applied but the reboot checkpoint was not durable. Exact original state was restored; do not restart for this attempt."
                        : "[ERROR] Patch applied, the reboot checkpoint was not durable, and exact rollback was incomplete. Do not restart; use the recovery kit.");
            }
        }

        if (result.Success && result.NeedsRestart && !noRestart && checkpointSaved)
        {
            if (autoRestart)
            {
                // --unattended flow: schedule the reboot ourselves via shutdown.exe /r /t <delay>.
                // Persisted + armed verification state is already written above, so the post-reboot
                // launch will pick up exactly the patch we applied. Fall back to printing the
                // command if shutdown.exe refuses (rare — we already hold Administrator).
                Console.WriteLine(
                    $"[UNATTENDED] Scheduling auto-restart in {config.RestartDelay}s...");
                var restart = PatchService.InitiateRestartDetailed(config.RestartDelay, msg => Console.WriteLine(msg));
                if (restart == PatchService.RestartInitiation.Failed)
                {
                    Console.Error.WriteLine(
                        $"[WARNING] Auto-restart could not be scheduled. Run 'shutdown /r /t {config.RestartDelay}' manually.");
                }
                else if (restart == PatchService.RestartInitiation.Unconfirmed)
                {
                    Console.Error.WriteLine(
                        $"[WARNING] Restart status UNCONFIRMED — verify the machine reboots; if not, run 'shutdown /r /t {config.RestartDelay}' manually.");
                }
            }
            else
            {
                Console.WriteLine($"Restart required. Run 'shutdown /r /t {config.RestartDelay}' to restart.");
            }
        }

        return result.Success && checkpointSaved ? 0 : 1;
    }

    static int RemoveCommand(AppConfig config, bool noRestart)
    {
        // Capture native + bypass status before removal so the snapshot diffs are meaningful
        // instead of "Unknown -> Unknown".
        var nativeStatus = DriveService.TestNativeNVMeActive();
        var bypassStatus = DriveService.GetBypassIOStatus();
        var result = PatchService.Uninstall(config, nativeStatus, bypassStatus, msg => Console.WriteLine(msg));
        bool watchdogDisarmed = true;
        if (result.Success)
        {
            // Close the watchdog window — no more post-patch monitoring makes sense after
            // an explicit uninstall.
            var disarm = EventLogWatchdogService.Disarm(config);
            watchdogDisarmed = disarm.Success;
            if (!watchdogDisarmed)
                Console.Error.WriteLine("Removal completed, but the watchdog disarm checkpoint is unavailable: " + disarm.Summary);
        }
        else if (result.Residue.Count > 0)
        {
            // Watchdog is deliberately LEFT ARMED on a partial removal.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Removal INCOMPLETE — {result.Residue.Count} component(s) still present:");
            foreach (var r in result.Residue)
                Console.Error.WriteLine($"  - {r}");
            Console.Error.WriteLine("Re-run 'remove' as Administrator; if residue persists, use the Recovery Kit or restore the pre-removal backup.");
        }
        if (result.Success && result.NeedsRestart && !noRestart)
            Console.WriteLine("Restart required to complete removal.");
        return result.Success && watchdogDisarmed ? 0 : 1;
    }

    static int DisableForUpdateCommand(AppConfig config, bool noRestart)
    {
        // Remember the active profile BEFORE removing, so re-enable restores it exactly even if
        // the user later edits config. The marker also records that a re-enable is expected.
        FirmwareUpdateWorkflowService.WriteMarker(config, config.PatchProfile, DateTime.UtcNow.ToString("o"));
        Console.WriteLine("Temporarily disabling Native NVMe for a firmware update...");

        var rc = RemoveCommand(config, noRestart);
        if (rc != 0)
        {
            Console.Error.WriteLine("Removal did not complete cleanly — leaving the pending marker so you can retry.");
            return rc;
        }

        // Map detected NVMe drives to their vendor firmware-update guides.
        var preflight = PreflightService.RunAll();
        var nudges = new List<FirmwareUpdateNudge>();
        foreach (var d in preflight.CachedDrives.Where(d => d.IsNVMe))
        {
            var name = d.Name ?? string.Empty;
            var fw = preflight.DriverInfo?.FirmwareVersions.TryGetValue(name, out var f) == true ? f : string.Empty;
            nudges.Add(FirmwareUpdateNudgeService.Lookup(name, fw));
        }

        Console.WriteLine();
        Console.WriteLine(FirmwareUpdateWorkflowService.BuildDisableInstructions(nudges));
        try { EventLogService.Write("Native NVMe disabled for firmware update"); } catch { }
        return 0;
    }

    static int ReEnableAfterUpdateCommand(AppConfig config, bool force, bool noRestart, bool unattended)
    {
        var marker = FirmwareUpdateWorkflowService.ReadMarker(config);
        var (profile, hadMarker) = FirmwareUpdateWorkflowService.ResolveReEnableProfile(marker, config);
        if (!hadMarker)
            Console.WriteLine("No firmware-update marker found — re-applying the current configured profile.");

        config.PatchProfile = profile;
        Console.WriteLine($"Re-enabling Native NVMe ({profile} profile) after the firmware update...");

        var rc = ApplyCommand(config, force, noRestart, unattended);
        if (rc == 0)
        {
            FirmwareUpdateWorkflowService.ClearMarker(config);
            try { EventLogService.Write("Native NVMe re-enabled after firmware update"); } catch { }
        }
        return rc;
    }

    static async Task<int> WinReInjectCommand(AppConfig config, bool apply)
    {
        var info = WinReBcdPrepService.Probe();
        var inf = WinReDriverInjectionService.DefaultStornvmeInf();
        var imageMissing = string.IsNullOrWhiteSpace(info.ImagePath) || !System.IO.File.Exists(info.ImagePath);
        var driverMissing = !System.IO.File.Exists(inf);
        var workingDir = string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir;
        var mountDir = apply
            ? WinReDriverInjectionService.CreateDefaultMountDir(workingDir)
            : System.IO.Path.Combine(workingDir, "WinREMount-preview");
        var plan = WinReDriverInjectionService.BuildPlan(
            info.ImagePath ?? "(WinRE image path unknown)", mountDir, inf, imageMissing, driverMissing);
        var readiness = RecoveryProofGateService.EvaluateWinReInjectionPlan(plan);

        if (!apply)
        {
            Console.WriteLine(WinReDriverInjectionService.RenderPlan(plan));
            Console.WriteLine();
            Console.WriteLine("Preview only. Re-run `winre-inject --apply` to back up, mount, inject, and commit.");
            return plan.IsExecutable ? 0 : 1;
        }

        Console.WriteLine("WinRE stornvme injection -- APPLY mode");
        Console.WriteLine($"  WinRE image : {plan.WinReImagePath}");
        Console.WriteLine($"  Driver INF  : {plan.DriverInfPath}");
        Console.WriteLine($"  Mount dir   : {plan.MountDir}");
        foreach (var warning in plan.Warnings)
            Console.WriteLine($"  ! {warning}");
        Console.WriteLine($"Readiness: {(readiness.Passed ? "OK" : "BLOCKED")} - {readiness.Detail}");
        if (!readiness.Passed)
            return 1;

        var result = await WinReDriverInjectionService.ApplyAsync(plan, workingDir, Console.WriteLine);
        Console.WriteLine(result.Summary);
        if (!string.IsNullOrWhiteSpace(result.BackupPath))
            Console.WriteLine($"Backup: {result.BackupPath}");
        if (!string.IsNullOrWhiteSpace(result.OriginalSha256))
            Console.WriteLine($"Original SHA-256: {result.OriginalSha256}");
        if (!string.IsNullOrWhiteSpace(result.BackupSha256))
            Console.WriteLine($"Backup SHA-256:   {result.BackupSha256}");
        if (!string.IsNullOrWhiteSpace(result.FinalSha256))
            Console.WriteLine($"Final SHA-256:    {result.FinalSha256}");
        return result.Success ? 0 : 1;
    }

    static int PolicyInstallCommand(string? source, string? policyDefs)
    {
        var src = string.IsNullOrWhiteSpace(source) ? PolicyTemplateInstallService.DefaultSourceDir() : source;
        var dst = string.IsNullOrWhiteSpace(policyDefs) ? PolicyTemplateInstallService.DefaultPolicyDefinitionsDir() : policyDefs;
        Console.WriteLine($"Installing ADMX/ADML policy templates from {src}");
        Console.WriteLine($"  into {dst}");
        var (ok, summary) = PolicyTemplateInstallService.Install(src, dst, Console.WriteLine);
        Console.WriteLine(summary);
        if (ok) Console.WriteLine("Refresh policy with 'gpupdate /force' or reopen the Group Policy editor to see the templates.");
        return ok ? 0 : 1;
    }

    static int PolicyUninstallCommand(string? source, string? policyDefs)
    {
        var src = string.IsNullOrWhiteSpace(source) ? PolicyTemplateInstallService.DefaultSourceDir() : source;
        var dst = string.IsNullOrWhiteSpace(policyDefs) ? PolicyTemplateInstallService.DefaultPolicyDefinitionsDir() : policyDefs;
        Console.WriteLine($"Removing ADMX/ADML policy templates from {dst}...");
        var (ok, summary) = PolicyTemplateInstallService.Uninstall(src, dst, Console.WriteLine);
        Console.WriteLine(summary);
        return ok ? 0 : 1;
    }

    static int DiagnosticsCommand(AppConfig config)
    {
        Console.WriteLine("Exporting system diagnostics...");
        var preflight = PreflightService.RunAll();
        var path = DiagnosticsService.Export(config.WorkingDir, preflight, []);
        if (path is not null)
        {
            config.LastDiagnosticsPath = path;
            ConfigService.Save(config);
            Console.WriteLine($"Diagnostics exported to: {path}");
            return 0;
        }
        Console.Error.WriteLine("Failed to export diagnostics");
        return 1;
    }

    static int FallbackCommand(AppConfig config, bool forceUnsupportedBuild = false)
    {
        // Same build-rule action policy as apply: the FeatureStore fallback is still a mutation.
        var policy = BuildActionPolicyService.EvaluateCurrent(config.WorkingDir);
        if (!policy.MutationAllowed)
        {
            Console.Error.WriteLine($"BUILD POLICY: {policy.Reason}");
            if (!forceUnsupportedBuild)
            {
                Console.Error.WriteLine("Fallback is refused on this build. Generic --force does NOT override this;");
                Console.Error.WriteLine("re-run with --force-unsupported-build from an interactive console to proceed anyway.");
                return 1;
            }
            Console.WriteLine("--force-unsupported-build specified: writing fallback IDs without a known binding path.");
        }

        var proof = RecoveryProofGateService.Evaluate(config);
        if (!proof.AllPassed)
        {
            Console.Error.WriteLine("Recovery proof FAILED — FeatureStore overrides cannot be reset from WinRE/Safe Mode.");
            foreach (var item in proof.Items.Where(i => !i.Passed))
                Console.Error.WriteLine($"  FAIL: {item.Label} — {item.Detail}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Fix the above or use --force to override (not recommended).");
            if (!Environment.GetCommandLineArgs().Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase)))
                return 1;
            Console.WriteLine("--force specified: proceeding despite recovery proof failure.");
        }

        Console.WriteLine("FeatureStore fallback (native Rtl API first; no network unless native write fails)");
        var fbSet = ViVeToolService.SelectFallbackSet();
        Console.WriteLine($"Writes feature IDs {fbSet.IdsDisplay} to Windows's FeatureStore");
        Console.WriteLine($"(set '{fbSet.Name}' for {fbSet.AppliesTo}; confidence: {fbSet.Confidence}).");
        Console.WriteLine();
        Action<string> log = msg => Console.WriteLine(msg);
        var result = FallbackApplyService.ApplyAsync(
            config.WorkingDir,
            log,
            allowUnsupportedBuild: forceUnsupportedBuild).GetAwaiter().GetResult();
        if (!result.Success)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Fallback failed: {result.Message}");
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine($"Applied feature ID(s): {string.Join(", ", result.AppliedIds)}");
        Console.WriteLine($"Method: {result.Method}");
        Console.WriteLine($"Integrity check: {result.IntegritySignal}");
        PatchVerificationService.MarkPending(config, isFallback: true);
        var watchdogCheckpoint = EventLogWatchdogService.Arm(config);
        if (!watchdogCheckpoint.Success)
            Console.Error.WriteLine("Watchdog checkpoint failed: " + watchdogCheckpoint.Summary);
        bool configCheckpointSaved = watchdogCheckpoint.Success && ConfigService.Save(config);
        bool ledgerCheckpointSaved = configCheckpointSaved &&
            MutationLedgerService.MarkRebootPending(
                config.WorkingDir,
                result.MutationOperationId,
                message => Console.WriteLine(message));
        if (!ledgerCheckpointSaved)
        {
            var restored = MutationLedgerService.RestoreOriginalState(config.WorkingDir, message => Console.WriteLine(message));
            PatchVerificationService.Clear(config, new VerificationReport { Outcome = VerificationOutcome.Reverted });
            var watchdogRollback = EventLogWatchdogService.Disarm(config);
            if (!watchdogRollback.Success)
                Console.Error.WriteLine("Watchdog rollback checkpoint failed: " + watchdogRollback.Summary);
            ConfigService.Save(config);
            Console.Error.WriteLine();
            Console.Error.WriteLine(restored.Success
                ? "ERROR: Fallback checkpoint was not durable. Exact original state was restored; do not restart for this attempt."
                : "ERROR: Fallback checkpoint was not durable and exact rollback was incomplete. Do NOT restart; use the recovery kit.");
            return 1;
        }
        Console.WriteLine("Restart required. Run: shutdown /r /t 30");
        return 0;
    }

    static int SupportBundleCommand(AppConfig config)
    {
        Console.WriteLine("Building support bundle...");
        var preflight = PreflightService.RunAll();
        var path = DiagnosticsService.ExportBundle(config.WorkingDir, preflight, [], config.ConfigFile);
        if (path is not null)
        {
            config.LastSupportBundlePath = path;
            ConfigService.Save(config);
            Console.WriteLine($"Support bundle saved to: {path}");
            return 0;
        }
        Console.Error.WriteLine("Failed to create support bundle");
        return 1;
    }

    static int RecoveryKitCommand(AppConfig config)
    {
        Console.WriteLine("Generating recovery kit...");
        var kitDir = RecoveryKitService.Export(config.WorkingDir, msg => Console.WriteLine(msg));
        if (kitDir is null)
            return 1;

        config.LastRecoveryKitPath = kitDir;
        ConfigService.Save(config);
        return 0;
    }

    static int VerifyCommand(AppConfig config)
    {
        var path = RecoveryKitService.GenerateVerificationScript(
            config.WorkingDir,
            config.PatchProfile,
            config.IncludeServerKey);
        if (path is not null)
        {
            config.LastVerificationScriptPath = path;
            ConfigService.Save(config);
            Console.WriteLine($"Verification script created: {path}");
            return 0;
        }
        return 1;
    }

    static int RecoveryProofCommand(AppConfig config, bool json = false)
    {
        var proof = RecoveryProofGateService.Evaluate(config);
        if (json)
        {
            Console.WriteLine(CliJson.Serialize("recovery-proof", CliJson.BuildRecoveryProof(proof)));
            return proof.AllPassed ? 0 : 1;
        }
        Console.WriteLine($"Recovery readiness: {proof.PassedCount}/{proof.TotalCount}");
        foreach (var item in proof.Items)
            Console.WriteLine($"  [{(item.Passed ? "OK" : "!!")}] {item.Label}: {item.Detail}");
        Console.WriteLine();
        Console.WriteLine(proof.AllPassed ? "Ready to apply." : "Fix the items above before applying.");
        return proof.AllPassed ? 0 : 1;
    }

    static int CriticalPreflightCommand(bool json)
    {
        var report = CriticalEnvironmentProbeService.EvaluateRegistryPatch();
        if (json)
        {
            Console.WriteLine(CliJson.Serialize("preflight", CliJson.BuildCriticalProbes(report)));
            return report.ExitCode;
        }

        Console.WriteLine(report.Summary);
        foreach (var item in report.Items)
        {
            Console.WriteLine($"  [{item.Verdict}] {item.Label} [{item.ReasonCode}]: {item.Detail}");
            if (!string.IsNullOrWhiteSpace(item.NativeError))
                Console.WriteLine("      Native: " + item.NativeError);
            foreach (var evidence in item.Evidence)
                Console.WriteLine("      Evidence: " + evidence);
            Console.WriteLine($"      Observed: {item.ObservedAtUtc:O}");
        }
        return report.ExitCode;
    }

    static void PrintCriticalProbeFailures(CriticalProbeReport report)
    {
        Console.Error.WriteLine(report.Summary);
        foreach (var item in report.Items.Where(item => item.BlocksMutation))
        {
            Console.Error.WriteLine($"  {item.Id}: {item.Verdict} [{item.ReasonCode}] — {item.Detail}");
            if (!string.IsNullOrWhiteSpace(item.NativeError))
                Console.Error.WriteLine("    Native: " + item.NativeError);
        }
    }

    static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintUsage();
        return 3;
    }

    static void PrintUsage() =>
        Console.Write(CliCommandRegistry.RenderUsage(AppConfig.AppVersion));
}
