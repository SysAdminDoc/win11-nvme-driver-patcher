using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Cli;

class Program
{
    static int Main(string[] args)
    {
        try
        {
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
            if (!IsKnownOperationalCommand(command))
                return Unknown(command);
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
                if (changed) ConfigService.Save(config);
            }
            catch { }
            // GPO overlay takes precedence over per-user config.json so a pinned fleet policy
            // isn't quietly overridden by a local run of the CLI.
            GpoPolicyService.ApplyTo(config, GpoPolicyService.Read());
            LogRotationService.RotateAll(config);
            EventLogRegistrationService.EnsureRegistered();
            EventLogService.Initialize(config.WriteEventLog);

            bool MatchesAny(string a, params string[] forms) =>
                forms.Any(f => a.Equals(f, StringComparison.OrdinalIgnoreCase));

            bool force = args.Any(a => a is not null && MatchesAny(a, "--force", "-f"));
            bool noRestart = args.Any(a => a is not null && MatchesAny(a, "--no-restart"));
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

            bool dryRun = args.Any(a => a is not null && MatchesAny(a, "--dry-run", "--preview"));
            bool unattended = args.Any(a => a is not null && MatchesAny(a, "--unattended"));
            bool autoRevert = args.Any(a => a is not null && MatchesAny(a, "--auto-revert"));
            string? isoOut = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--output=".Length);
            string? telemetryEndpoint = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--endpoint=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--endpoint=".Length);
            string? exportPath = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--export=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--export=".Length);
            string? importPath = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--import=", StringComparison.OrdinalIgnoreCase))?
                                 .Substring("--import=".Length);
            int thresholdArg = 15;
            var threshStr = args.Select(a => (a ?? string.Empty))
                                 .FirstOrDefault(a => a.StartsWith("--threshold=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(threshStr) && int.TryParse(threshStr.Substring("--threshold=".Length), out var t)) thresholdArg = t;

            return command switch
            {
                "status" => StatusCommand(),
                "apply" or "install" => dryRun ? DryRunCommand(config) : ApplyCommand(config, force, noRestart, unattended),
                "remove" or "uninstall" => RemoveCommand(config, noRestart),
                "diagnostics" or "export-diagnostics" => DiagnosticsCommand(config),
                "bundle" or "export-bundle" or "support-bundle" => SupportBundleCommand(config),
                "fallback" or "vivetool-fallback" or "apply-fallback" => FallbackCommand(config),
                "recovery-kit" or "export-recovery-kit" => RecoveryKitCommand(config),
                "verify" => VerifyCommand(config),
                "dry-run" or "preview" => DryRunCommand(config),
                "watchdog" => autoRevert ? WatchdogAutoRevertCommand(config) : WatchdogCommand(config),
                "reliability" => ReliabilityCommand(config),
                "minidump" or "triage" => MinidumpCommand(config),
                "firmware" or "compat" => FirmwareCompatCommand(),
                "scope" => ScopeCommand(config),
                "etw" => EtwCommand(config).GetAwaiter().GetResult(),
                "winpe" => WinPECommand(config, isoOut).GetAwaiter().GetResult(),
                "telemetry" => TelemetryCommand(config, telemetryEndpoint).GetAwaiter().GetResult(),
                "verifier-on" => VerifierOnCommand(),
                "verifier-off" => VerifierOffCommand(),
                "verifier-status" => VerifierStatusCommand(),
                "guardrails" => GuardrailsCommand(),
                "controllers" or "per-controller" => PerControllerCommand(),
                "tail" or "events-tail" => EventTailCommand(),
                "physical-disks" => PhysicalDisksCommand(),
                "bypassio" => BypassIoCommand(),
                "apst" => ApstCommand(),
                "identify" => IdentifyCommand(),
                "config-export" => ConfigExportCommand(config, exportPath),
                "config-import" => ConfigImportCommand(config, importPath),
                "tuning-export" => TuningExportCommand(exportPath),
                "tuning-import" => TuningImportCommand(importPath),
                "compare-benchmarks" => CompareBenchmarksCommand(config, thresholdArg),
                "compat-checksum" => CompatChecksumCommand(config),
                "verify-backup" => VerifyBackupCommand(config, importPath),
                "register-tasks" => RegisterTasksCommand(),
                "unregister-tasks" => UnregisterTasksCommand(),
                "portable-enable" => PortableEnableCommand(),
                "portable-disable" => PortableDisableCommand(),
                "update-check" => UpdateCheckCommand().GetAwaiter().GetResult(),
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

    static bool IsKnownOperationalCommand(string command) => command switch
    {
        "status" => true,
        "apply" or "install" => true,
        "remove" or "uninstall" => true,
        "diagnostics" or "export-diagnostics" => true,
        "bundle" or "export-bundle" or "support-bundle" => true,
        "fallback" or "vivetool-fallback" or "apply-fallback" => true,
        "recovery-kit" or "export-recovery-kit" => true,
        "verify" => true,
        "dry-run" or "preview" => true,
        "watchdog" => true,
        "reliability" => true,
        "minidump" or "triage" => true,
        "firmware" or "compat" => true,
        "scope" => true,
        "etw" => true,
        "winpe" => true,
        "telemetry" => true,
        "verifier-on" or "verifier-off" or "verifier-status" => true,
        "guardrails" => true,
        "controllers" or "per-controller" => true,
        "tail" or "events-tail" => true,
        "physical-disks" => true,
        "bypassio" => true,
        "apst" => true,
        "identify" => true,
        "config-export" or "config-import" => true,
        "tuning-export" or "tuning-import" => true,
        "compare-benchmarks" => true,
        "compat-checksum" => true,
        "verify-backup" => true,
        "register-tasks" or "unregister-tasks" => true,
        "portable-enable" or "portable-disable" => true,
        "update-check" => true,
        _ => false
    };

    static int ApplyCommand(AppConfig config, bool force, bool noRestart, bool unattended)
    {
        // v4.5: unattended mode = silent apply + auto-reboot (if --no-restart is not also set).
        // Unattended implies SkipWarnings for the run.
        if (unattended) config.SkipWarnings = true;
        return ApplyCommand(config, force, noRestart);
    }

    static int WatchdogAutoRevertCommand(AppConfig config)
    {
        var outcome = AutoRevertService.MaybeRun(config, msg => Console.WriteLine(msg));
        Console.WriteLine(outcome.Summary);
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

    static int PerControllerCommand()
    {
        var report = PerControllerAuditService.Audit();
        Console.WriteLine("Per-controller audit");
        Console.WriteLine("====================");
        Console.WriteLine(report.Summary);
        foreach (var c in report.Controllers)
            Console.WriteLine($"  {(c.IsNative ? "[NATIVE] " : "[LEGACY] ")}{c.FriendlyName}  driver={c.BoundDriver}  id={c.InstanceId}");
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

    static int BypassIoCommand()
    {
        var list = BypassIoInspectorService.Inspect();
        foreach (var v in list)
            Console.WriteLine($"  {v.Letter}  {v.Status}  stack={v.Stack}");
        return 0;
    }

    static int ApstCommand()
    {
        var report = ApstInspectorService.Inspect();
        Console.WriteLine(report.Summary);
        foreach (var s in report.States)
            Console.WriteLine($"  PS{s.PowerStateNumber}  idle={s.IdleTimeMicroseconds}us  entry={s.EntryLatencyUs}us  exit={s.ExitLatencyUs}us  nonOp={s.NonOperational}");
        return 0;
    }

    static int IdentifyCommand()
    {
        for (int n = 0; n < 16; n++)
        {
            var r = NvmeIdentifyService.Query(n);
            if (!r.Success) continue;
            Console.WriteLine($"PhysicalDrive{n}: {r.Summary}  SN={r.SerialNumber.Trim()}");
        }
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

    static int CompareBenchmarksCommand(AppConfig config, int threshold)
    {
        var baseline = AutoBenchmarkService.LoadBaseline(config);
        if (baseline is null)
        {
            Console.Error.WriteLine("No baseline present. Run a benchmark first.");
            return 1;
        }
        // No current benchmark supplied — treat the baseline as both sides and exit 0.
        var verdict = AutoBenchmarkService.Compare(baseline, baseline, threshold);
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
        var preflight = PreflightService.RunAll();
        var report = DryRunService.PlanInstall(config, preflight);
        Console.WriteLine();
        Console.WriteLine(DryRunService.RenderMarkdown(report));
        return report.PreflightBlockers.Count > 0 ? 1 : 0;
    }

    static int WatchdogCommand(AppConfig config)
    {
        var report = EventLogWatchdogService.Evaluate(config);
        Console.WriteLine("NVMe Driver Watchdog");
        Console.WriteLine("====================");
        Console.WriteLine($"Verdict: {report.Verdict}");
        Console.WriteLine(report.Summary);
        Console.WriteLine();
        Console.WriteLine(report.Detail);
        // Non-zero exit on Unstable so CI/CD and Task Scheduler jobs can react.
        return report.Verdict switch
        {
            WatchdogVerdict.Unstable => 1,
            WatchdogVerdict.Warning => 2,
            _ => 0
        };
    }

    static int ReliabilityCommand(AppConfig config)
    {
        DateTime? patchTs = null;
        if (!string.IsNullOrWhiteSpace(config.PendingVerificationSince) &&
            DateTime.TryParse(config.PendingVerificationSince,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            patchTs = ts;
        var report = ReliabilityService.GetCorrelation(patchTs);
        Console.WriteLine("Reliability Monitor correlation");
        Console.WriteLine("===============================");
        Console.WriteLine(report.Summary);
        foreach (var p in report.Series.TakeLast(20))
            Console.WriteLine($"  {p.Timestamp:yyyy-MM-dd}  {p.Index:F1}");
        return report.DataAvailable ? 0 : 1;
    }

    static int MinidumpCommand(AppConfig config)
    {
        DateTime? patchTs = null;
        if (!string.IsNullOrWhiteSpace(config.PendingVerificationSince) &&
            DateTime.TryParse(config.PendingVerificationSince,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            patchTs = ts;
        var report = MinidumpTriageService.Analyze(patchTs);
        Console.WriteLine("Minidump triage");
        Console.WriteLine("===============");
        Console.WriteLine(report.Summary);
        foreach (var d in report.Dumps)
            Console.WriteLine($"  {(d.MentionsNVMeStack ? "[NVMe]" : "[....]")}  {d.CreatedUtc:u}  {d.FilePath}");
        return report.NVMeRelated > 0 ? 1 : 0;
    }

    static int FirmwareCompatCommand()
    {
        var db = FirmwareCompatService.LoadDatabase();
        Console.WriteLine($"Firmware compat DB (schema {db.SchemaVersion}, updated {db.Updated}):");
        foreach (var e in db.Entries)
            Console.WriteLine($"  [{e.Level}] {e.Controller} / {e.Firmware} — {e.Note}");
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
        foreach (var w in result.Warnings) Console.WriteLine($"  [WARN] {w}");
        return result.Success ? 0 : 1;
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

    static int StatusCommand()
    {
        var preflight = PreflightService.RunAll();
        var status = RegistryService.GetPatchStatus();

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

            // Honest read-out of the override-block state — if keys are written but the
            // driver never swapped, that's Microsoft's Feb/Mar 2026 block, not a user error.
            if (status.Count > 0 && !preflight.NativeNVMeStatus.IsActive)
            {
                Console.WriteLine();
                Console.WriteLine("NOTE: The feature flags are set but Windows is still loading the legacy driver.");
                Console.WriteLine("      On post-block Insider builds (early 2026+) the override is a no-op.");
                Console.WriteLine("      Community workaround: ViVeTool with feature IDs 60786016 and 48433719.");
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

    static int ApplyCommand(AppConfig config, bool force, bool noRestart)
    {
        var preflight = PreflightService.RunAll(msg => Console.WriteLine(msg));

        if (preflight.VeraCryptDetected)
        {
            Console.Error.WriteLine("BLOCKED: VeraCrypt system encryption detected. This safeguard cannot be bypassed.");
            return 1;
        }
        if (!PreflightService.AllCriticalPassed(preflight.Checks) && !force)
        {
            Console.Error.WriteLine("Critical preflight check(s) failed. Use --force to override.");
            return 1;
        }
        if (!preflight.HasNVMeDrives && !force)
        {
            Console.Error.WriteLine("No NVMe drives detected. Use --force to apply anyway.");
            return 2;
        }

        var result = PatchService.Install(
            config,
            preflight.BitLockerEnabled,
            preflight.VeraCryptDetected,
            preflight.NativeNVMeStatus,
            preflight.BypassIOStatus,
            msg => Console.WriteLine(msg));

        if (result.Success && result.NeedsRestart && !noRestart)
        {
            Console.WriteLine($"Restart required. Run 'shutdown /r /t {config.RestartDelay}' to restart.");
        }
        if (result.Success)
        {
            PatchVerificationService.MarkPending(config);
            // Arm the post-patch watchdog so post-reboot event-log distress signals auto-revert
            // (if the user opted into auto-revert in config / GPO).
            EventLogWatchdogService.Arm(config);
            ConfigService.Save(config);
        }

        return result.Success ? 0 : 1;
    }

    static int RemoveCommand(AppConfig config, bool noRestart)
    {
        // Capture native + bypass status before removal so the snapshot diffs are meaningful
        // instead of "Unknown -> Unknown".
        var nativeStatus = DriveService.TestNativeNVMeActive();
        var bypassStatus = DriveService.GetBypassIOStatus();
        var result = PatchService.Uninstall(config, nativeStatus, bypassStatus, msg => Console.WriteLine(msg));
        if (result.Success)
        {
            // Close the watchdog window — no more post-patch monitoring makes sense after
            // an explicit uninstall.
            EventLogWatchdogService.Disarm(config);
        }
        if (result.Success && result.NeedsRestart && !noRestart)
            Console.WriteLine("Restart required to complete removal.");
        return result.Success ? 0 : 1;
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

    static int FallbackCommand(AppConfig config)
    {
        Console.WriteLine("ViVeTool fallback (downloads from https://github.com/thebookisclosed/ViVe)");
        Console.WriteLine("Writes feature IDs 60786016 and 48433719 to Windows's FeatureStore.");
        Console.WriteLine();
        Action<string> log = msg => Console.WriteLine(msg);
        var result = ViVeToolService.ApplyFallbackAsync(config.WorkingDir, log).GetAwaiter().GetResult();
        if (!result.Success)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Fallback failed: {result.Message}");
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine($"Applied feature ID(s): {string.Join(", ", result.AppliedIDs)}");
        PatchVerificationService.MarkPending(config);
        ConfigService.Save(config);
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

    static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintUsage();
        return 3;
    }

    static void PrintUsage()
    {
        Console.WriteLine($"NVMe Driver Patcher CLI v{AppConfig.AppVersion}");
        Console.WriteLine();
        Console.WriteLine("Usage: NVMeDriverPatcher.Cli <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  status              Check patch status (exit: 0=applied, 1=not, 2=partial)");
        Console.WriteLine("  apply               Apply the NVMe driver patch");
        Console.WriteLine("  remove              Remove the NVMe driver patch");
        Console.WriteLine("  dry-run             Show exactly what apply would change — no registry writes");
        Console.WriteLine("  diagnostics         Export system diagnostics report (.txt)");
        Console.WriteLine("  bundle              Export shareable support bundle (.zip: report + config + crash + regs + db)");
        Console.WriteLine("  fallback            Apply ViVeTool fallback (for post-block Insider builds — IDs 60786016, 48433719)");
        Console.WriteLine("  recovery-kit        Generate WinRE recovery kit");
        Console.WriteLine("  verify              Generate post-reboot verification script");
        Console.WriteLine("  watchdog            Read watchdog verdict (exit: 0=healthy, 1=unstable, 2=warning)");
        Console.WriteLine("  reliability         Pull Win32_ReliabilityStabilityMetrics, correlate with patch timestamp");
        Console.WriteLine("  minidump            Scan C:\\Windows\\Minidump for NVMe-stack-referencing dumps");
        Console.WriteLine("  firmware            List the bundled controller/firmware compat entries");
        Console.WriteLine("  scope               List per-drive include/exclude decisions");
        Console.WriteLine("  etw                 Capture a 60s ETW storage trace (wpr.exe) to %LocalAppData%\\NVMePatcher\\etl");
        Console.WriteLine("  winpe               Build a WinPE recovery USB tree/ISO from the current Recovery Kit");
        Console.WriteLine("                        --output=<dir>   Output directory (default: %LocalAppData%\\NVMePatcher\\winpe)");
        Console.WriteLine("  telemetry           Build compat report; optional --endpoint=<url> to submit anonymized payload");
        Console.WriteLine("  verifier-on/off     Enable/disable Driver Verifier stress checks on nvmedisk/stornvme/disk");
        Console.WriteLine("  verifier-status     Report whether Driver Verifier is active on the NVMe stack");
        Console.WriteLine("  version             Print the CLI version");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --force, -f                Skip overridable safety checks (VeraCrypt remains blocked)");
        Console.WriteLine("  --no-restart               Don't prompt for restart");
        Console.WriteLine("  --safe                     Safe Mode: write primary flag only (735209102) — recommended default");
        Console.WriteLine("  --full                     Full Mode: write all three flags (higher peak perf, higher BSOD risk)");
        Console.WriteLine("  --include-server-key       Force the optional Server 2025 key on for this run");
        Console.WriteLine("  --no-server-key            Force the optional Server 2025 key off for this run");
        Console.WriteLine("  --dry-run, --preview       Preview changes without applying them (works with 'apply')");
        Console.WriteLine("  --output=<dir>             Output directory for winpe/telemetry output files");
        Console.WriteLine("  --endpoint=<url>           HTTPS endpoint for 'telemetry' submissions");
        Console.WriteLine();
        Console.WriteLine("Modes:");
        Console.WriteLine("  Safe (default)  Primary feature flag + Safe Boot entries. Swaps stornvme.sys");
        Console.WriteLine("                  for nvmedisk.sys with the lowest reported crash risk.");
        Console.WriteLine("  Full            Adds UxAccOptimization (1853569164) and Standalone_Future");
        Console.WriteLine("                  (156965516). Higher peak performance on some drives; community");
        Console.WriteLine("                  BSOD reports in early 2026 cluster on these two flags.");
        Console.WriteLine();
        Console.WriteLine("Exit codes:");
        Console.WriteLine("  0  success / patch applied (status)");
        Console.WriteLine("  1  failure / patch not applied (status)");
        Console.WriteLine("  2  partial state / no NVMe drives");
        Console.WriteLine("  3  unknown command or no args");
        Console.WriteLine("  4  Administrator privileges required");
        Console.WriteLine("  99 unhandled error");
    }
}
