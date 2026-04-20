using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NVMeDriverPatcher.Services;

public class WinReProvisionInfo
{
    public bool WinReEnabled { get; set; }
    public string? WinReLocation { get; set; }
    public string? ImagePath { get; set; }
    public string? DeviceGuid { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool NeedsReagentcInstall { get; set; }
}

// Probes the Windows Recovery Environment (reagentc /info) and the BCD entry for WinRE
// (bcdedit /enum "{current}" /v) so the tool can tell the user whether their box can
// actually fall back to WinRE if the patch wedges startup. Closes part of ROADMAP §3.3 —
// the full "inject stornvme.sys into WinRE" flow is deliberately left as a future effort
// because it requires Dism + an admin-mounted boot.wim and has a much larger blast radius.
public static class WinReBcdPrepService
{
    public static WinReProvisionInfo Probe()
    {
        var info = new WinReProvisionInfo();
        try
        {
            var reagentc = RunCapture("reagentc.exe", new[] { "/info" }, 20);
            if (!string.IsNullOrWhiteSpace(reagentc.Stdout))
            {
                info.WinReEnabled = Regex.IsMatch(reagentc.Stdout,
                    @"Windows\s+RE\s+status\s*:\s*Enabled",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var locMatch = Regex.Match(reagentc.Stdout,
                    @"Windows\s+RE\s+location\s*:\s*(.+)",
                    RegexOptions.IgnoreCase);
                if (locMatch.Success) info.WinReLocation = locMatch.Groups[1].Value.Trim();
                var bcdMatch = Regex.Match(reagentc.Stdout,
                    @"(?:Boot\s+Configuration\s+Data\s+\(BCD\)\s+identifier|BCD\s+identifier)\s*:\s*(\{[0-9a-fA-F-]+\})",
                    RegexOptions.IgnoreCase);
                if (bcdMatch.Success) info.DeviceGuid = bcdMatch.Groups[1].Value;
            }
            else
            {
                info.NeedsReagentcInstall = true;
            }

            if (!string.IsNullOrEmpty(info.DeviceGuid))
            {
                var bcd = RunCapture("bcdedit.exe", new[] { "/enum", info.DeviceGuid, "/v" }, 20);
                var imgMatch = Regex.Match(bcd.Stdout,
                    @"osdevice\s+ramdisk=\[(?<vol>[^\]]+)\](?<path>\\[^\s,]+)",
                    RegexOptions.IgnoreCase);
                if (imgMatch.Success)
                {
                    info.ImagePath = imgMatch.Groups["vol"].Value + imgMatch.Groups["path"].Value;
                }
            }
        }
        catch (Exception ex)
        {
            info.Summary = $"WinRE probe failed: {ex.Message}";
            return info;
        }

        info.Summary = info.WinReEnabled
            ? $"WinRE enabled at {info.WinReLocation ?? "(unknown location)"}. Fallback path is viable."
            : "WinRE not currently enabled — recovery-from-WinRE path will NOT work until reagentc /enable is run.";
        return info;
    }

    /// <summary>
    /// Ensure WinRE is enabled. Wraps `reagentc /enable` — on a fresh install where the WinRE
    /// image is staged but the entry isn't registered, this flips the switch.
    /// </summary>
    public static bool EnableWinRe(Action<string>? log = null)
    {
        try
        {
            var result = RunCapture("reagentc.exe", new[] { "/enable" }, 30);
            if (result.ExitCode == 0)
            {
                log?.Invoke("[OK] WinRE enabled.");
                return true;
            }
            log?.Invoke($"[ERROR] reagentc /enable exit {result.ExitCode}: {result.Stderr.Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not invoke reagentc: {ex.Message}");
            return false;
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCapture(string exe, string[] args, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} did not start.");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException($"{exe} timed out after {timeoutSeconds}s");
        }
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
