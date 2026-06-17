using System.Text;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class DryRunPlanItem
{
    public string Action { get; set; } = string.Empty;    // WRITE / CREATE / DELETE
    public string Target { get; set; } = string.Empty;    // full registry path
    public string ValueName { get; set; } = string.Empty;
    public string Before { get; set; } = "(absent)";
    public string After { get; set; } = string.Empty;
    public string Kind { get; set; } = "DWord";
    public string Note { get; set; } = string.Empty;
}

public class DryRunReport
{
    public PatchProfile Profile { get; set; }
    public bool IncludeServerKey { get; set; }
    public int TotalWrites { get; set; }
    public int TotalCreates { get; set; }
    public List<DryRunPlanItem> Items { get; set; } = new();
    public List<string> PreflightBlockers { get; set; } = new();
    public List<string> PreflightWarnings { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
}

// Computes exactly what `PatchService.Install` would write, without touching the registry.
// Surface via the CLI (`--dry-run`) and GUI ("Preview Changes") so scripted callers and
// anxious users can see the full change set before committing.
public static class DryRunService
{
    public static DryRunReport PlanInstall(AppConfig config, PreflightResult? preflight = null)
    {
        var report = new DryRunReport
        {
            Profile = config.PatchProfile,
            IncludeServerKey = config.IncludeServerKey
        };

        var featureIDs = AppConfig.GetFeatureIDsForProfile(config.PatchProfile).ToList();
        if (config.IncludeServerKey) featureIDs.Add(AppConfig.ServerFeatureID);

        foreach (var id in featureIDs)
        {
            string friendly = AppConfig.FeatureNames.TryGetValue(id, out var fn) ? fn : "Feature Flag";
            int? current = ReadCurrentDword(AppConfig.RegistrySubKey, id);
            report.Items.Add(new DryRunPlanItem
            {
                Action = "WRITE",
                Target = AppConfig.RegistryPath,
                ValueName = id,
                Before = current is null ? "(absent)" : current.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                After = "1",
                Kind = "DWord",
                Note = friendly
            });
            report.TotalWrites++;
        }

        report.Items.Add(new DryRunPlanItem
        {
            Action = "CREATE",
            Target = $@"HKEY_LOCAL_MACHINE\{AppConfig.SafeBootMinimalPath}",
            ValueName = "(default)",
            Before = "(absent)",
            After = AppConfig.SafeBootValue,
            Kind = "String",
            Note = "SafeBoot Minimal support — prevents INACCESSIBLE_BOOT_DEVICE in Safe Mode"
        });
        report.Items.Add(new DryRunPlanItem
        {
            Action = "CREATE",
            Target = $@"HKEY_LOCAL_MACHINE\{AppConfig.SafeBootNetworkPath}",
            ValueName = "(default)",
            Before = "(absent)",
            After = AppConfig.SafeBootValue,
            Kind = "String",
            Note = "SafeBoot Network support"
        });
        report.TotalCreates += 2;

        if (preflight is not null)
        {
            // Replay what the UI would show but without the live UI-only bits.
            if (preflight.VeraCryptDetected) report.PreflightBlockers.Add("VeraCrypt system encryption present — patch is blocked.");
            if (preflight.BitLockerEnabled) report.PreflightWarnings.Add("BitLocker will be suspended for one reboot cycle.");
            if (preflight.IsLaptop) report.PreflightWarnings.Add("Laptop detected — APST power-management regression (~15% battery).");
            foreach (var sw in preflight.IncompatibleSoftware)
                report.PreflightWarnings.Add($"Incompatible software: {sw.Name} [{sw.Severity}] — {sw.Message}");
        }

        report.Summary = BuildSummary(report);
        return report;
    }

    public static DryRunReport PlanUninstall()
    {
        var report = new DryRunReport();
        foreach (var id in AppConfig.FeatureIDs.Append(AppConfig.ServerFeatureID))
        {
            int? current = ReadCurrentDword(AppConfig.RegistrySubKey, id);
            if (current is null) continue;
            string friendly = AppConfig.FeatureNames.TryGetValue(id, out var fn) ? fn : "Feature Flag";
            report.Items.Add(new DryRunPlanItem
            {
                Action = "DELETE",
                Target = AppConfig.RegistryPath,
                ValueName = id,
                Before = current.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                After = "(absent)",
                Kind = "DWord",
                Note = friendly
            });
        }
        foreach (var (path, label) in new[]
        {
            (AppConfig.SafeBootMinimalPath, "SafeBoot Minimal"),
            (AppConfig.SafeBootNetworkPath, "SafeBoot Network")
        })
        {
            if (ProbeSubkeyExists(path))
            {
                report.Items.Add(new DryRunPlanItem
                {
                    Action = "DELETE",
                    Target = $@"HKEY_LOCAL_MACHINE\{path}",
                    ValueName = "(subkey)",
                    Before = AppConfig.SafeBootValue,
                    After = "(absent)",
                    Kind = "Key",
                    Note = label
                });
            }
        }
        report.Summary = $"Dry-run uninstall: {report.Items.Count} item(s) would be removed.";
        return report;
    }

    internal static string BuildSummary(DryRunReport report)
    {
        var sb = new StringBuilder();
        sb.Append("Dry-run install: ");
        sb.Append($"{report.TotalWrites} feature-flag write(s), {report.TotalCreates} subkey creation(s). ");
        sb.Append($"Profile: {report.Profile}");
        if (report.IncludeServerKey) sb.Append(" + Server 2025 key");
        if (report.PreflightBlockers.Count > 0) sb.Append($" | {report.PreflightBlockers.Count} BLOCKER(s)");
        if (report.PreflightWarnings.Count > 0) sb.Append($" | {report.PreflightWarnings.Count} warning(s)");
        sb.Append('.');
        return sb.ToString();
    }

    public static string RenderMarkdown(DryRunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# NVMe Driver Patcher — Dry Run");
        sb.AppendLine();
        sb.AppendLine(report.Summary);
        sb.AppendLine();
        if (report.PreflightBlockers.Count > 0)
        {
            sb.AppendLine("## Blockers");
            foreach (var b in report.PreflightBlockers) sb.AppendLine($"- **{b}**");
            sb.AppendLine();
        }
        if (report.PreflightWarnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            foreach (var w in report.PreflightWarnings) sb.AppendLine($"- {w}");
            sb.AppendLine();
        }
        sb.AppendLine("## Registry Changes");
        sb.AppendLine();
        sb.AppendLine("| Action | Target | Value | Before → After | Note |");
        sb.AppendLine("|--------|--------|-------|----------------|------|");
        foreach (var item in report.Items)
        {
            sb.AppendLine($"| {item.Action} | `{item.Target}` | `{item.ValueName}` | `{item.Before}` → `{item.After}` | {item.Note} |");
        }
        return sb.ToString();
    }

    private static int? ReadCurrentDword(string subkey, string valueName)
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(subkey);
            if (key is null) return null;
            var val = key.GetValue(valueName);
            return val is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ProbeSubkeyExists(string subkey)
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(subkey);
            return key is not null;
        }
        catch { return false; }
    }
}
