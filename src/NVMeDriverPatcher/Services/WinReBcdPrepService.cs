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
// actually fall back to WinRE if the patch wedges startup. Closes part of ROADMAP §3.3.
// The stornvme-into-WinRE injection is now PLANNED/PREVIEWED by WinReDriverInjectionService
// (the `winre-inject` CLI command prints the exact DISM operations + blast-radius warnings);
// the actual image mount/commit is left to a deliberate operator step because it mutates the
// recovery boot image and must be validated by a real WinRE boot afterward.
public static class WinReBcdPrepService
{
    // `reagentc /info` output is LOCALIZED — the "Windows RE status: Enabled" label and value
    // differ per UI language, so matching English literals reports "disabled" on every non-English
    // Windows even when WinRE is fully provisioned. The locale-INDEPENDENT signal is the BCD
    // identifier GUID: a real (non-zero) GUID means WinRE has a boot entry (enabled); an all-zeros
    // GUID (or none) means disabled. Both are structural, not translated.
    private static readonly Regex RxGuid = new(@"\{?([0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})\}?", RegexOptions.Compiled);
    // WinRE location is a device path (not localized): `\\?\GLOBALROOT\...` or a drive path ending
    // in `\Recovery\WindowsRE`. The LABEL preceding it is localized; the path itself is not.
    private static readonly Regex RxWinrePath = new(@"\\\\\?\\GLOBALROOT\S+|[A-Za-z]:\\\S*?\\Recovery\\WindowsRE", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxOsDevice  = new(@"osdevice\s+ramdisk=\[(?<vol>[^\]]+)\](?<path>\\[^\s,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Locale-independent parse of <c>reagentc /info</c> stdout. Enabled-state is derived from the
    /// presence of a non-zero BCD identifier GUID, not the translated status label. Pure + testable.
    /// </summary>
    internal static (bool Enabled, string? Location, string? Guid) ParseReagentcInfo(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return (false, null, null);

        string? guid = null;
        foreach (Match m in RxGuid.Matches(stdout))
        {
            var g = m.Groups[1].Value;
            if (!IsZeroGuid(g)) { guid = "{" + g + "}"; break; }
        }

        var pathMatch = RxWinrePath.Match(stdout);
        string? location = pathMatch.Success ? pathMatch.Value.Trim() : null;

        return (guid is not null, location, guid);
    }

    private static bool IsZeroGuid(string guid) => guid.All(c => c is '0' or '-');

    public static WinReProvisionInfo Probe()
    {
        var info = new WinReProvisionInfo();
        try
        {
            var reagentc = RunCapture("reagentc.exe", new[] { "/info" }, 20);
            if (string.IsNullOrWhiteSpace(reagentc.Stdout))
            {
                info.NeedsReagentcInstall = true;
            }
            else
            {
                var (enabled, location, guid) = ParseReagentcInfo(reagentc.Stdout);
                info.WinReEnabled = enabled;
                info.WinReLocation = location;
                info.DeviceGuid = guid;
            }

            if (!string.IsNullOrEmpty(info.DeviceGuid))
            {
                var bcd = RunCapture("bcdedit.exe", new[] { "/enum", info.DeviceGuid, "/v" }, 20);
                var imgMatch = RxOsDevice.Match(bcd.Stdout);
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
