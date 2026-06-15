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
        RunSchtasks(BuildBootVerifyArgs(cliPath), log);

    public static bool RegisterWatchdogSweep(string cliPath, int intervalMinutes, Action<string>? log = null) =>
        RunSchtasks(BuildWatchdogSweepArgs(cliPath, intervalMinutes), log);

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
        return new[]
        {
            "/Create", "/F", "/RU", "SYSTEM", "/RL", "HIGHEST",
            "/TN", WatchdogTaskName,
            "/TR", $"\"{cliPath}\" watchdog",
            "/SC", "MINUTE", "/MO", intervalMinutes.ToString()
        };
    }

    internal static string[] BuildUnregisterArgs(string taskName) =>
        new[] { "/Delete", "/F", "/TN", taskName };

    public static bool IsRegistered(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
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
            var psi = new ProcessStartInfo("schtasks.exe")
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
