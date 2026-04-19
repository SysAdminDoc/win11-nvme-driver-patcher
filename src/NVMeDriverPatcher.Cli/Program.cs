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

            return command switch
            {
                "status" => StatusCommand(),
                "apply" or "install" => ApplyCommand(config, force, noRestart),
                "remove" or "uninstall" => RemoveCommand(config, noRestart),
                "diagnostics" or "export-diagnostics" => DiagnosticsCommand(config),
                "bundle" or "export-bundle" or "support-bundle" => SupportBundleCommand(config),
                "fallback" or "vivetool-fallback" or "apply-fallback" => FallbackCommand(config),
                "recovery-kit" or "export-recovery-kit" => RecoveryKitCommand(config),
                "verify" => VerifyCommand(config),
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
        _ => false
    };

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
        Console.WriteLine("  diagnostics         Export system diagnostics report (.txt)");
        Console.WriteLine("  bundle              Export shareable support bundle (.zip: report + config + crash + regs + db)");
        Console.WriteLine("  fallback            Apply ViVeTool fallback (for post-block Insider builds — IDs 60786016, 48433719)");
        Console.WriteLine("  recovery-kit        Generate WinRE recovery kit");
        Console.WriteLine("  verify              Generate post-reboot verification script");
        Console.WriteLine("  version             Print the CLI version");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --force, -f                Skip overridable safety checks (VeraCrypt remains blocked)");
        Console.WriteLine("  --no-restart               Don't prompt for restart");
        Console.WriteLine("  --safe                     Safe Mode: write primary flag only (735209102) — recommended default");
        Console.WriteLine("  --full                     Full Mode: write all three flags (higher peak perf, higher BSOD risk)");
        Console.WriteLine("  --include-server-key       Force the optional Server 2025 key on for this run");
        Console.WriteLine("  --no-server-key            Force the optional Server 2025 key off for this run");
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
