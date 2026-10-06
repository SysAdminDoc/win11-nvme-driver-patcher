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
    public bool IncludeStandaloneFuture { get; set; }
    public int TotalWrites { get; set; }
    public int TotalCreates { get; set; }
    public int TotalDeletes { get; set; }
    public RegistryOverrideAssessment? RegistryOverrideAssessment { get; set; }
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
    public static DryRunReport PlanInstall(AppConfig config, PreflightResult? preflight = null) =>
        PlanInstall(config, preflight, ControlSetService.GetMirrorTargets());

    /// <summary>
    /// Overload taking an explicit mirror set so the preview can be verified on any host. A
    /// machine with a single control set has nothing to mirror, which would make a
    /// live-enumeration test assert 0 == 0 and pass without exercising anything.
    /// </summary>
    internal static DryRunReport PlanInstall(
        AppConfig config,
        PreflightResult? preflight,
        IReadOnlyList<string> mirrorControlSets) =>
        PlanInstall(config, preflight, mirrorControlSets, ReadCurrentValue);

    /// <summary>
    /// The rows come from <see cref="PatchService.BuildRequiredRegistryMutations(PatchProfile, bool, IReadOnlyList{string}?)"/>,
    /// the same list apply commits, so the preview can't drift from the real write set. The
    /// reader is injectable because the live SafeBoot keys differ by build: 24H2 26100.9550
    /// and 26200.8737+ ship the GUID keys themselves.
    /// </summary>
    internal static DryRunReport PlanInstall(
        AppConfig config,
        PreflightResult? preflight,
        IReadOnlyList<string> mirrorControlSets,
        Func<string, string, CurrentRegistryValue> readCurrent)
    {
        var report = new DryRunReport
        {
            Profile = config.PatchProfile,
            IncludeServerKey = config.IncludeServerKey,
            IncludeStandaloneFuture = config.PatchProfile == PatchProfile.Full && config.IncludeStandaloneFuture
        };

        var featureIDs = AppConfig.GetFeatureIDsForProfile(config.PatchProfile, report.IncludeStandaloneFuture).ToList();
        if (config.IncludeServerKey) featureIDs.Add(AppConfig.ServerFeatureID);

        report.RegistryOverrideAssessment = FallbackFeatureCatalog.AssessRegistryOverrides(
            preflight?.BuildDetails,
            featureIDs.Where(id => AppConfig.FeatureIDs.Contains(id)));

        // Apply mirrors every CurrentControlSet write into each spare control set (issue #15), and
        // appends those mirrors after the primary writes. A preview that omitted them would
        // under-report the real change set, which is the whole thing this command exists to prevent.
        int primaryCount = PatchService.BuildRequiredRegistryMutations(
            config.PatchProfile, config.IncludeServerKey, mirrorControlSets: null, report.IncludeStandaloneFuture).Count;
        var mutations = PatchService.BuildRequiredRegistryMutations(
            config.PatchProfile, config.IncludeServerKey, mirrorControlSets, report.IncludeStandaloneFuture);
        for (int i = 0; i < mutations.Count; i++)
        {
            var mutation = mutations[i];
            var current = readCurrent(mutation.Path, mutation.ValueName);
            string? mirrorNote = i >= primaryCount ? $"Boot-recovery mirror ({mutation.Path.Split('\\')[1]})" : null;
            report.Items.Add(mutation.ValueKind == Microsoft.Win32.RegistryValueKind.DWord
                ? OverrideRow(mutation, current, mirrorNote)
                : SafeBootRow(mutation, current, mirrorNote));
        }
        // #19: apply clears a 156965516 left by an earlier Full install when this one doesn't write it.
        if (!report.IncludeStandaloneFuture)
        {
            foreach (var subKey in MutationLedgerService.FeatureOverrideSubKeys(mirrorControlSets))
            {
                var current = readCurrent(subKey, AppConfig.StandaloneFutureFeatureID);
                if (current.Value is null) continue;
                report.Items.Add(new DryRunPlanItem
                {
                    Action = "DELETE",
                    Target = $@"HKEY_LOCAL_MACHINE\{subKey}",
                    ValueName = AppConfig.StandaloneFutureFeatureID,
                    Before = Convert.ToString(current.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "(absent)",
                    After = "(absent)",
                    Note = "Left by an earlier Full install. This profile doesn't include it, so apply clears it."
                });
            }
        }
        report.TotalWrites = report.Items.Count(item => item.Action == "WRITE");
        report.TotalCreates = report.Items.Count(item => item.Action == "CREATE");
        report.TotalDeletes = report.Items.Count(item => item.Action == "DELETE");

        if (preflight is not null)
        {
            // Replay what the UI would show but without the live UI-only bits.
            foreach (var probe in preflight.CriticalProbes.Items.Where(item => item.BlocksMutation))
                report.PreflightBlockers.Add(
                    $"{probe.Label}: {probe.Verdict} [{probe.ReasonCode}]: {probe.Detail}");
            if (preflight.VeraCryptDetected &&
                preflight.CriticalProbes.Items.All(item => item.Id != "VeraCrypt"))
                report.PreflightBlockers.Add("VeraCrypt system encryption present. Patch is blocked.");
            if (preflight.BitLockerEnabled) report.PreflightWarnings.Add("BitLocker will be suspended for one reboot cycle.");
            if (preflight.IsLaptop) report.PreflightWarnings.Add("Laptop detected. APST power-management regression (~15% battery).");
            foreach (var sw in preflight.IncompatibleSoftware)
                report.PreflightWarnings.Add($"Incompatible software: {sw.Name} [{sw.Severity}]: {sw.Message}");
        }

        report.Summary = BuildSummary(report);
        return report;
    }

    public static DryRunReport PlanUninstall() => PlanUninstall(ControlSetService.GetMirrorTargets());

    /// <summary>Overload taking an explicit mirror set. See <see cref="PlanInstall"/>.</summary>
    internal static DryRunReport PlanUninstall(IReadOnlyList<string> mirrorControlSets)
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

        // Removal restores the ledger baseline, which includes the boot-recovery mirrors, so the
        // preview has to show them or it under-reports the change set in the other direction.
        foreach (var controlSet in mirrorControlSets)
        {
            var overridesPath = ControlSetService.MirrorPath(AppConfig.RegistrySubKey, controlSet);
            if (overridesPath is not null)
            {
                foreach (var id in AppConfig.FeatureIDs.Append(AppConfig.ServerFeatureID))
                {
                    int? current = ReadCurrentDword(overridesPath, id);
                    if (current is null) continue;
                    report.Items.Add(new DryRunPlanItem
                    {
                        Action = "DELETE",
                        Target = $@"HKEY_LOCAL_MACHINE\{overridesPath}",
                        ValueName = id,
                        Before = current.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        After = "(absent)",
                        Kind = "DWord",
                        Note = $"Boot-recovery mirror ({controlSet})"
                    });
                }
            }

            foreach (var safeBootPath in new[] { AppConfig.SafeBootMinimalPath, AppConfig.SafeBootNetworkPath })
            {
                var path = ControlSetService.MirrorPath(safeBootPath, controlSet);
                if (path is null || !ProbeSubkeyExists(path)) continue;
                report.Items.Add(new DryRunPlanItem
                {
                    Action = "DELETE",
                    Target = $@"HKEY_LOCAL_MACHINE\{path}",
                    ValueName = "(subkey)",
                    Before = AppConfig.SafeBootValue,
                    After = "(absent)",
                    Kind = "Key",
                    Note = $"Boot-recovery mirror ({controlSet})"
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
        sb.Append($"{report.TotalWrites} value write(s), {report.TotalCreates} new key(s)");
        if (report.TotalDeletes > 0) sb.Append($", {report.TotalDeletes} leftover value(s) cleared");
        sb.Append(". ");
        sb.Append("Scope: machine-wide across every eligible NVMe drive/controller; per-drive exclusions are not enforced. ");
        sb.Append($"Profile: {report.Profile}");
        if (report.IncludeServerKey) sb.Append(" + Server 2025 key");
        if (report.IncludeStandaloneFuture) sb.Append(" + 156965516");
        if (report.RegistryOverrideAssessment is not null)
            sb.Append($" | {report.RegistryOverrideAssessment.Summary.TrimEnd('.')}");
        if (report.PreflightBlockers.Count > 0) sb.Append($" | {report.PreflightBlockers.Count} BLOCKER(s)");
        if (report.PreflightWarnings.Count > 0) sb.Append($" | {report.PreflightWarnings.Count} warning(s)");
        sb.Append('.');
        return sb.ToString();
    }

    public static string RenderMarkdown(DryRunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# NVMe Driver Patcher: Dry Run");
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
        if (report.RegistryOverrideAssessment is not null)
        {
            sb.AppendLine("## Registry Override IDs");
            sb.AppendLine();
            sb.AppendLine("These are the registry override IDs this profile would write, shown beside this branch's FeatureStore IDs for the same features. The two use separate numbering, so they aren't expected to match, and nothing here changes the payload.");
            foreach (var feature in report.RegistryOverrideAssessment.Features)
                sb.AppendLine($"- {feature.Detail}");
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

    /// <summary>What the preview knows about one registry value right now.</summary>
    internal readonly record struct CurrentRegistryValue(bool KeyExists, object? Value, bool WindowsOwned = false);

    private static DryRunPlanItem OverrideRow(DurableRegistryMutation mutation, CurrentRegistryValue current, string? mirrorNote) => new()
    {
        Action = "WRITE",
        Target = $@"HKEY_LOCAL_MACHINE\{mutation.Path}",
        ValueName = mutation.ValueName,
        Before = current.Value switch
        {
            null => "(absent)",
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            var other => other.ToString() ?? "(absent)"
        },
        After = Convert.ToString(mutation.ExpectedValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        Kind = "DWord",
        Note = mirrorNote ?? (AppConfig.FeatureNames.TryGetValue(mutation.ValueName, out var friendly) ? friendly : "Feature Flag")
    };

    private static DryRunPlanItem SafeBootRow(DurableRegistryMutation mutation, CurrentRegistryValue current, string? mirrorNote)
    {
        var expected = (string)mutation.ExpectedValue;
        var existing = current.Value as string;
        var note = mirrorNote ?? mutation.Path switch
        {
            AppConfig.SafeBootMinimalPath => "SafeBoot Minimal support (prevents INACCESSIBLE_BOOT_DEVICE in Safe Mode)",
            AppConfig.SafeBootNetworkPath => "SafeBoot Network support",
            AppConfig.SafeBootMinimalServicePath => "SafeBoot Minimal entry for the nvmedisk service",
            _ => "SafeBoot Network entry for the nvmedisk service"
        };
        if (current.WindowsOwned)
        {
            // Apply leaves these alone (PatchService.SplitWindowsOwnedSafeBootWrites).
            return new DryRunPlanItem
            {
                Action = "KEEP",
                Target = $@"HKEY_LOCAL_MACHINE\{mutation.Path}",
                ValueName = "(default)",
                Before = existing ?? "(absent)",
                After = existing ?? "(absent)",
                Kind = "String",
                Note = note + ". Windows owns and write-protects this key and already registers the driver for Safe Mode in it, so apply leaves it as is"
            };
        }
        if (existing is not null && !string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
            note += $". Replaces the existing default '{existing}', which removal puts back";

        return new DryRunPlanItem
        {
            Action = current.KeyExists ? "WRITE" : "CREATE",
            Target = $@"HKEY_LOCAL_MACHINE\{mutation.Path}",
            ValueName = "(default)",
            Before = existing ?? "(absent)",
            After = expected,
            Kind = "String",
            Note = note
        };
    }

    private static CurrentRegistryValue ReadCurrentValue(string subkey, string valueName)
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(subkey);
            return key is null
                ? new(false, null)
                : new(true, key.GetValue(valueName), SafeBootStateService.IsTrustedInstallerOwned(key));
        }
        catch (Exception ex) when (PatchService.IsAccessDenied(ex))
        {
            // A key we may not read still exists; its value is unknown.
            return new(true, null);
        }
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
