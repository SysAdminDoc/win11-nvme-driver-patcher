using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Cli;

class Program
{
    static int Main(string[] args)
    {
        try
        {
            var config = ConfigService.Load();
            EventLogService.Initialize(config.WriteEventLog);

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

            bool MatchesAny(string a, params string[] forms) =>
                forms.Any(f => a.Equals(f, StringComparison.OrdinalIgnoreCase));

            bool force = args.Any(a => a is not null && MatchesAny(a, "--force", "-f"));
            bool noRestart = args.Any(a => a is not null && MatchesAny(a, "--no-restart"));
            bool includeServerKeyOverride = args.Any(a => a is not null && MatchesAny(a, "--include-server-key"));
            bool excludeServerKeyOverride = args.Any(a => a is not null && MatchesAny(a, "--no-server-key"));

            // Allow CLI override of the persisted IncludeServerKey config so automation doesn't
            // need to first edit config.json. --no-server-key wins if both are passed by mistake.
            if (includeServerKeyOverride) config.IncludeServerKey = true;
            if (excludeServerKeyOverride) config.IncludeServerKey = false;

            return command switch
            {
                "status" => StatusCommand(),
                "apply" or "install" => ApplyCommand(config, force, noRestart),
                "remove" or "uninstall" => RemoveCommand(config, noRestart),
                "diagnostics" or "export-diagnostics" => DiagnosticsCommand(config),
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

        if (preflight.VeraCryptDetected && !force)
        {
            Console.Error.WriteLine("BLOCKED: VeraCrypt system encryption detected. Use --force to override.");
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
            Console.WriteLine($"Diagnostics exported to: {path}");
            return 0;
        }
        Console.Error.WriteLine("Failed to export diagnostics");
        return 1;
    }

    static int RecoveryKitCommand(AppConfig config)
    {
        Console.WriteLine("Generating recovery kit...");
        var kitDir = RecoveryKitService.Export(config.WorkingDir, msg => Console.WriteLine(msg));
        return kitDir is not null ? 0 : 1;
    }

    static int VerifyCommand(AppConfig config)
    {
        var path = RecoveryKitService.GenerateVerificationScript(config.WorkingDir, config.IncludeServerKey);
        if (path is not null)
        {
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
        Console.WriteLine("  diagnostics         Export system diagnostics report");
        Console.WriteLine("  recovery-kit        Generate WinRE recovery kit");
        Console.WriteLine("  verify              Generate post-reboot verification script");
        Console.WriteLine("  version             Print the CLI version");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --force, -f                Skip safety checks");
        Console.WriteLine("  --no-restart               Don't prompt for restart");
        Console.WriteLine("  --include-server-key       Force the optional Server 2025 key on for this run");
        Console.WriteLine("  --no-server-key            Force the optional Server 2025 key off for this run");
        Console.WriteLine();
        Console.WriteLine("Exit codes:");
        Console.WriteLine("  0  success / patch applied (status)");
        Console.WriteLine("  1  failure / patch not applied (status)");
        Console.WriteLine("  2  partial state / no NVMe drives");
        Console.WriteLine("  3  unknown command or no args");
        Console.WriteLine("  99 unhandled error");
    }
}
