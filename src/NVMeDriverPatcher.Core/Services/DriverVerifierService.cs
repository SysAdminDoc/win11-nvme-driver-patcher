using System.Diagnostics;

namespace NVMeDriverPatcher.Services;

public class DriverVerifierStatus
{
    public bool ToolAvailable { get; set; }
    public bool AnyDriverVerified { get; set; }
    public List<string> VerifiedDrivers { get; set; } = new();
    public string RawOutput { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public class DriverVerifierResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool RequiresReboot { get; set; }
}

// Wraps verifier.exe for the dev-mode workflow of "enable kernel-level stress checks on the
// NVMe stack, run a benchmark, reboot, confirm no BSOD." Intentionally narrow scope — only
// operates on nvmedisk.sys + stornvme.sys + disk.sys. We never enable /all because that's
// a user-hostile default.
//
// This is a tester harness, not a normal user feature — gated behind a "Developer tools"
// toggle in the Diagnostics tab.
public static class DriverVerifierService
{
    internal static readonly string[] TargetDrivers = { "nvmedisk.sys", "stornvme.sys", "disk.sys" };

    public static DriverVerifierStatus QueryStatus()
    {
        var status = new DriverVerifierStatus();
        try
        {
            var output = RunVerifier(new[] { "/query" }, 15);
            status.ToolAvailable = true;
            status.RawOutput = output;
            foreach (var target in TargetDrivers)
            {
                if (output.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    status.AnyDriverVerified = true;
                    status.VerifiedDrivers.Add(target);
                }
            }
            status.Summary = status.AnyDriverVerified
                ? $"Driver Verifier active on: {string.Join(", ", status.VerifiedDrivers)}"
                : "Driver Verifier inactive for NVMe-stack drivers.";
        }
        catch (Exception ex)
        {
            status.ToolAvailable = false;
            status.Summary = $"Driver Verifier unavailable: {ex.Message}";
        }
        return status;
    }

    /// <summary>
    /// Enables Driver Verifier in /standard mode against the NVMe stack drivers. Persists
    /// across reboots — caller is responsible for instructing the user to run their workload
    /// + reboot, then call DisableAll() to clean up.
    /// </summary>
    public static DriverVerifierResult EnableForNVMeStack()
    {
        var result = new DriverVerifierResult();
        try
        {
            var args = new List<string> { "/standard", "/driver" };
            args.AddRange(TargetDrivers);
            var output = RunVerifier(args.ToArray(), 30);
            result.Success = true;
            result.RequiresReboot = true;
            result.Summary = $"Driver Verifier enabled for {string.Join(", ", TargetDrivers)}. Reboot required.";
        }
        catch (Exception ex)
        {
            result.Summary = $"Failed to enable Driver Verifier: {ex.Message}";
        }
        return result;
    }

    public static DriverVerifierResult DisableAll()
    {
        var result = new DriverVerifierResult();
        try
        {
            RunVerifier(new[] { "/reset" }, 30);
            result.Success = true;
            result.RequiresReboot = true;
            result.Summary = "Driver Verifier disabled. Reboot required.";
        }
        catch (Exception ex)
        {
            result.Summary = $"Failed to disable Driver Verifier: {ex.Message}";
        }
        return result;
    }

    private static string RunVerifier(string[] args, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo("verifier.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("verifier.exe did not start.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"verifier.exe timed out after {timeoutSeconds}s");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        // verifier /query returns 0 on active drivers, 1 when none configured — both are fine for us.
        if (proc.ExitCode != 0 && proc.ExitCode != 1)
            throw new InvalidOperationException($"verifier.exe exit {proc.ExitCode}: {stderr.Trim()}");
        return stdout + "\n" + stderr;
    }
}
