using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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

public sealed class QuickMachineRecoverySettings
{
    public bool QuerySucceeded { get; init; }
    public bool? CloudRemediationEnabled { get; init; }
    public bool? AutoRemediationEnabled { get; init; }
    public string Summary { get; init; } = string.Empty;
}

// Probes the Windows Recovery Environment (reagentc /info) and the BCD entry for WinRE
// (bcdedit /enum "{current}" /v) so the tool can tell the user whether their box can
// actually fall back to WinRE if the patch wedges startup. Closes part of ROADMAP §3.3.
// The stornvme-into-WinRE injection is planned and guarded by WinReDriverInjectionService.
// `winre-inject` previews by default; `winre-inject --apply` performs the backup, mount,
// driver injection, commit/discard, and checksum logging.
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
        foreach (var line in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            // The translated label retains the invariant "BCD" acronym. Restrict the GUID to
            // that identifier row so unrelated recovery/package GUIDs cannot be mistaken for the
            // WinRE boot entry merely because they appeared earlier in reagentc output.
            if (!line.Contains("BCD", StringComparison.OrdinalIgnoreCase)) continue;
            var match = RxGuid.Match(line);
            if (!match.Success) continue;

            var candidate = match.Groups[1].Value;
            if (!IsZeroGuid(candidate)) guid = "{" + candidate + "}";
            break;
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
            var reagentc = RunCapture(SystemToolPathService.Resolve("reagentc.exe"), new[] { "/info" }, 20);
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
                var bcd = RunCapture(SystemToolPathService.Resolve("bcdedit.exe"), new[] { "/enum", info.DeviceGuid, "/v" }, 20);
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
            var result = RunCapture(SystemToolPathService.Resolve("reagentc.exe"), new[] { "/enable" }, 30);
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

    /// <summary>
    /// Read the documented Quick Machine Recovery settings without changing recovery state.
    /// The command emits an XML document that may contain Wi-Fi credentials; only the two
    /// boolean remediation states are returned to callers.
    /// </summary>
    public static QuickMachineRecoverySettings ProbeQuickMachineRecovery()
    {
        try
        {
            var result = RunCapture(SystemToolPathService.Resolve("reagentc.exe"), new[] { "/getrecoverysettings" }, 20);
            var parsed = ParseRecoverySettings(result.Stdout);
            if (result.ExitCode != 0 || !parsed.Parsed)
            {
                return new QuickMachineRecoverySettings
                {
                    Summary = "Quick Machine Recovery settings are not exposed by reagentc on this build or policy."
                };
            }

            return new QuickMachineRecoverySettings
            {
                QuerySucceeded = true,
                CloudRemediationEnabled = parsed.CloudRemediationEnabled,
                AutoRemediationEnabled = parsed.AutoRemediationEnabled,
                Summary = "Quick Machine Recovery settings were read from reagentc."
            };
        }
        catch (Exception ex)
        {
            return new QuickMachineRecoverySettings
            {
                Summary = $"Quick Machine Recovery settings could not be read ({ex.GetType().Name})."
            };
        }
    }

    /// <summary>
    /// Parses only the QMR state attributes from reagentc's XML response. Never return the XML
    /// or arbitrary attributes because the response can contain configured Wi-Fi credentials.
    /// </summary>
    internal static (bool Parsed, bool? CloudRemediationEnabled, bool? AutoRemediationEnabled)
        ParseRecoverySettings(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return (false, null, null);

        try
        {
            var start = stdout.IndexOf("<WindowsRE", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return (false, null, null);
            var endMarker = "</WindowsRE>";
            var end = stdout.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return (false, null, null);
            var xml = stdout.Substring(start, end + endMarker.Length - start);
            var document = XDocument.Parse(xml, LoadOptions.None);
            var cloud = ParseRecoveryState(document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("CloudRemediation", StringComparison.OrdinalIgnoreCase)));
            var auto = ParseRecoveryState(document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("AutoRemediation", StringComparison.OrdinalIgnoreCase)));
            return (true, cloud, auto);
        }
        catch
        {
            return (false, null, null);
        }
    }

    private static bool? ParseRecoveryState(XElement? element)
    {
        var state = element?.Attribute("state")?.Value;
        if (string.IsNullOrWhiteSpace(state)) return null;
        return state.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ when bool.TryParse(state, out var value) => value,
            _ => null
        };
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
