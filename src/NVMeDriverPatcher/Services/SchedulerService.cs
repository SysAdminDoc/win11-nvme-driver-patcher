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

    public static bool RegisterBootVerify(string cliPath, Action<string>? log = null)
    {
        // At login as SYSTEM, run `NVMeDriverPatcher.Cli watchdog --auto-revert` so the auto-
        // revert consumer runs even if the user never launches the GUI. /RL HIGHEST is required
        // because the CLI self-elevates via its manifest.
        return RunSchtasks(new[]
        {
            "/Create", "/F", "/RU", "SYSTEM", "/RL", "HIGHEST",
            "/TN", BootTaskName,
            "/TR", $"\"{cliPath}\" watchdog --auto-revert",
            "/SC", "ONSTART"
        }, log);
    }

    public static bool RegisterWatchdogSweep(string cliPath, int intervalMinutes, Action<string>? log = null)
    {
        intervalMinutes = Math.Clamp(intervalMinutes, 5, 1440);
        return RunSchtasks(new[]
        {
            "/Create", "/F", "/RU", "SYSTEM", "/RL", "HIGHEST",
            "/TN", WatchdogTaskName,
            "/TR", $"\"{cliPath}\" watchdog",
            "/SC", "MINUTE", "/MO", intervalMinutes.ToString()
        }, log);
    }

    public static bool Unregister(string taskName, Action<string>? log = null) =>
        RunSchtasks(new[] { "/Delete", "/F", "/TN", taskName }, log);

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
            proc.WaitForExit(10_000);
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
