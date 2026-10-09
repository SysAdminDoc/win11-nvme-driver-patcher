using System.Diagnostics;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// Wraps schtasks.exe to register a boot-time verifier + periodic watchdog evaluator. Keeps
// verification + auto-revert decisions running even when the user never launches the app.
// Both tasks invoke the CLI binary; the GUI has no role.
public static class SchedulerService
{
    public const string BootTaskName = @"SysAdminDoc\NVMePatcher\BootVerify";
    public const string WatchdogTaskName = @"SysAdminDoc\NVMePatcher\WatchdogSweep";

    public static bool RegisterBootVerify(string cliPath, Action<string>? log = null) =>
        GuardTaskTarget(cliPath, log) && RunSchtasks(BuildBootVerifyArgs(cliPath), log);

    public static bool RegisterWatchdogSweep(string cliPath, int intervalMinutes, Action<string>? log = null) =>
        GuardTaskTarget(cliPath, log) && RunSchtasks(BuildWatchdogSweepArgs(cliPath, intervalMinutes), log);

    /// <summary>
    /// Both tasks run the CLI as SYSTEM, so whoever can replace the exe gets SYSTEM. Only a CLI
    /// under Program Files, where only administrators and TrustedInstaller can write, or in the
    /// folder the MSI installed to (it pins an admin-only DACL there, and records the folder under
    /// HKLM) may be the target. A copy in Downloads or on the desktop can be swapped by any
    /// program the user runs.
    /// </summary>
    public static bool IsProtectedTaskTarget(string? exePath) =>
        IsProtectedTaskTarget(exePath, new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ReadMsiInstallLocation() ?? string.Empty,
        });

    private static string? ReadMsiInstallLocation()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"Software\SysAdminDoc\NVMeDriverPatcher");
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static bool IsProtectedTaskTarget(string? exePath, IEnumerable<string> protectedRoots)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(exePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        foreach (var root in protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool GuardTaskTarget(string cliPath, Action<string>? log)
    {
        if (IsProtectedTaskTarget(cliPath)) return true;
        log?.Invoke($"[ERROR] Not registering a SYSTEM task for {cliPath}: it isn't under Program Files or the MSI's install folder, so other programs could replace it.");
        return false;
    }

    public static bool Unregister(string taskName, Action<string>? log = null) =>
        RunSchtasks(BuildUnregisterArgs(taskName), log);

    // Pure schtasks.exe argument builders — extracted so the command shape (task name, action,
    // schedule, interval clamping) is unit-testable without spawning schtasks.

    // At login as SYSTEM, run `NVMeDriverPatcher.Cli watchdog --auto-revert` so the auto-revert
    // consumer runs even if the user never launches the GUI. /RL HIGHEST is required because the
    // CLI self-elevates via its manifest.
    internal static string[] BuildBootVerifyArgs(string cliPath) => new[]
    {
        "/Create", "/F", "/RU", "SYSTEM", "/RL", "HIGHEST",
        "/TN", BootTaskName,
        "/TR", $"\"{cliPath}\" watchdog --auto-revert",
        "/SC", "ONSTART"
    };

    internal static string[] BuildWatchdogSweepArgs(string cliPath, int intervalMinutes)
    {
        intervalMinutes = Math.Clamp(intervalMinutes, 5, 1440);
        string[] head =
        {
            "/Create", "/F", "/RU", "SYSTEM", "/RL", "HIGHEST",
            "/TN", WatchdogTaskName,
            "/TR", $"\"{cliPath}\" watchdog"
        };
        // schtasks /SC MINUTE only accepts /MO 1-1439, so the 24h ceiling has to be expressed as
        // a daily schedule; "/SC MINUTE /MO 1440" is rejected with "The /MO value is invalid".
        return intervalMinutes >= 1440
            ? [.. head, "/SC", "DAILY"]
            : [.. head, "/SC", "MINUTE", "/MO", intervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)];
    }

    internal static string[] BuildUnregisterArgs(string taskName) =>
        new[] { "/Delete", "/F", "/TN", taskName };

    public static bool IsRegistered(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo(SystemToolPathService.Resolve("schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/Query");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(taskName);
            using var proc = Process.Start(psi);
            if (proc is null) return false;

            // Drain stdout/stderr asynchronously before WaitForExit. schtasks /Query emits a
            // formatted task summary that easily fills the pipe buffer when the task name
            // matches a localized Windows entry — reading concurrently avoids the deadlock.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(true); } catch { }
                return false;
            }
            try { stdoutTask.GetAwaiter().GetResult(); } catch { }
            try { stderrTask.GetAwaiter().GetResult(); } catch { }
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool RunSchtasks(string[] args, Action<string>? log)
    {
        try
        {
            var psi = new ProcessStartInfo(SystemToolPathService.Resolve("schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) { log?.Invoke("[ERROR] schtasks.exe did not start."); return false; }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(true); } catch { }
                log?.Invoke("[ERROR] schtasks.exe timed out.");
                return false;
            }
            if (proc.ExitCode != 0)
            {
                var err = stderrTask.GetAwaiter().GetResult().Trim();
                log?.Invoke($"[ERROR] schtasks /{args[0]} exit {proc.ExitCode}: {err}");
                return false;
            }
            _ = stdoutTask.GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] schtasks: {ex.Message}");
            return false;
        }
    }
}
