using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NVMeDriverPatcher.Services;

public class BypassIoVolumeInfo
{
    public string Letter { get; set; } = string.Empty;
    public string Status { get; set; } = "Unknown";
    public string Stack { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Detail { get; set; } = string.Empty;
}

// Per-volume inspector around `fsutil bypassio state <drive>`. Post-patch, nvmedisk.sys refuses
// BypassIO — this lets the user see exactly which volumes lost it. Closes ROADMAP §2.5.
public static class BypassIoInspectorService
{
    private static readonly Regex RxBypassEnabled = new(@"BypassIO\s*:?\s*Enabled", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxStorageStack  = new(@"Storage\s+stack\s*:?\s*(.+)",  RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string BuildGamingImpactSummary(IEnumerable<BypassIoVolumeInfo> volumes)
    {
        var enabledVolumes = volumes
            .Where(v => v.Enabled)
            .Select(v => v.Letter)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        if (enabledVolumes.Count == 0)
            return "Gaming impact: none - BypassIO is already off on all volumes.";

        var volumeList = string.Join(", ", enabledVolumes);
        return $"Gaming impact: BypassIO is active on {enabledVolumes.Count} volume(s) ({volumeList}). " +
            $"After patching to nvmedisk.sys, DirectStorage titles such as {DriveService.DirectStorageGameExamplesText} can fall back to legacy I/O with higher CPU use or stutter. " +
            "The native-NVMe mutation is machine-wide, so a game-library drive cannot be excluded; remove the patch or accept this global tradeoff.";
    }

    public static List<BypassIoVolumeInfo> Inspect()
    {
        var results = new List<BypassIoVolumeInfo>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives()
                         .Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                var info = InspectOne(drive.Name[..2]);
                if (info is not null) results.Add(info);
            }
        }
        catch { }
        return results;
    }

    internal static BypassIoVolumeInfo? InspectOne(string drive)
    {
        try
        {
            var psi = new ProcessStartInfo("fsutil.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("bypassio");
            psi.ArgumentList.Add("state");
            psi.ArgumentList.Add(drive);
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(true); } catch { }
                return null;
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            var info = new BypassIoVolumeInfo { Letter = drive };
            if (proc.ExitCode != 0)
            {
                info.Status = "Query failed";
                info.Detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                return info;
            }
            var combined = stdout;
            info.Enabled = RxBypassEnabled.IsMatch(combined);
            info.Status = info.Enabled ? "Enabled" : "Disabled";
            var stackMatch = RxStorageStack.Match(combined);
            if (stackMatch.Success) info.Stack = stackMatch.Groups[1].Value.Trim();
            info.Detail = combined.Length > 600 ? combined[..600] + "…" : combined.Trim();
            return info;
        }
        catch { return null; }
    }
}
