using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>The registry state debloat scripts leave behind, read once so classification is pure.</summary>
public sealed record ThirdPartyResidueSnapshot
{
    /// <summary>Value names under the FeatureManagement Overrides key; null when the key is absent.</summary>
    public IReadOnlyCollection<string>? OverrideValueNames { get; init; }

    /// <summary>Whether <c>SYSTEM\CurrentControlSet\Policies\Microsoft</c> exists. Stock Windows
    /// often has no such key, so its absence only matters next to other residue.</summary>
    public bool PoliciesMicrosoftExists { get; init; }

    public string? SafeBootMinimalDefault { get; init; }
    public string? SafeBootNetworkDefault { get; init; }
}

/// <summary>
/// Spots NVMe registry state that came from a third-party script rather than this tool or Windows.
/// FR33THY "Ultimate" writes a fifth override (3244671118) and its revert runs
/// <c>reg delete HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft /f</c>; cleaners that strip only
/// the overrides leave the SafeBoot entries behind. Without this check both read as a clean PC.
/// </summary>
public static class ThirdPartyResidueService
{
    internal const string PoliciesMicrosoftSubKey = @"SYSTEM\CurrentControlSet\Policies\Microsoft";

    public static ThirdPartyResidueSnapshot ReadSnapshot()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        IReadOnlyCollection<string>? overrideNames = null;
        using (var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey))
            if (overrides is not null) overrideNames = overrides.GetValueNames();

        bool policiesExist;
        using (var policies = hklm.OpenSubKey(PoliciesMicrosoftSubKey))
            policiesExist = policies is not null;

        return new ThirdPartyResidueSnapshot
        {
            OverrideValueNames = overrideNames,
            PoliciesMicrosoftExists = policiesExist,
            SafeBootMinimalDefault = ReadDefault(hklm, AppConfig.SafeBootMinimalPath),
            SafeBootNetworkDefault = ReadDefault(hklm, AppConfig.SafeBootNetworkPath)
        };
    }

    /// <summary>A Warning naming what was found, or null when nothing points at a third-party script.</summary>
    internal static PreflightCheck? Classify(ThirdPartyResidueSnapshot snapshot)
    {
        var names = snapshot.OverrideValueNames ?? Array.Empty<string>();
        var foreign = names.Where(AppConfig.KnownThirdPartyOverrideIDs.ContainsKey)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var orphaned = FindOrphanedSafeBootStores(snapshot);
        if (foreign.Count == 0 && orphaned.Count == 0) return null;

        var parts = new List<string>();
        foreach (var id in foreign)
        {
            parts.Add($"Override {id} is set. Third-party NVMe scripts such as FR33THY Ultimate write it and this tool " +
                "doesn't, so Remove leaves it in place. Delete the value, or run the revert in the script that set it, " +
                "if you want it gone.");
        }
        if (orphaned.Count > 0)
        {
            parts.Add($"SafeBoot {string.Join(" and ", orphaned)} {(orphaned.Count == 1 ? "entry" : "entries")} for " +
                "the native driver are still set, but no NVMe override is. Something removed the overrides and left " +
                "these behind. That's usually a debloat script's revert or a registry cleaner, though some 24H2 builds " +
                "delete 735209102 at boot on their own. They do nothing by themselves. Remove clears them, and Apply " +
                "sets up the full patch again.");
            if (!snapshot.PoliciesMicrosoftExists)
            {
                parts.Add(@"The whole Policies\Microsoft registry tree is gone as well, which is what FR33THY Ultimate's " +
                    "revert does. If Group Policy deploys Known Issue Rollback settings to this PC, run gpupdate /force " +
                    "to put them back.");
            }
        }
        return new PreflightCheck(CheckStatus.Warning, string.Join(" ", parts));
    }

    /// <summary>SafeBoot stores ("Minimal", "Network") holding the patch's "Storage Disks" entry while
    /// no NVMe override is set. Other values under Overrides are Known Issue Rollback policies and
    /// don't count as an override here.</summary>
    internal static IReadOnlyList<string> FindOrphanedSafeBootStores(ThirdPartyResidueSnapshot snapshot)
    {
        var names = snapshot.OverrideValueNames ?? Array.Empty<string>();
        if (names.Any(name => AppConfig.IsOwnedOverrideValueName(name) || AppConfig.KnownThirdPartyOverrideIDs.ContainsKey(name)))
            return Array.Empty<string>();

        var orphaned = new List<string>();
        if (AppConfig.IsPatchSafeBootDefault(snapshot.SafeBootMinimalDefault)) orphaned.Add("Minimal");
        if (AppConfig.IsPatchSafeBootDefault(snapshot.SafeBootNetworkDefault)) orphaned.Add("Network");
        return orphaned;
    }

    private static string? ReadDefault(RegistryKey hklm, string path)
    {
        try
        {
            using var key = hklm.OpenSubKey(path);
            return key?.GetValue("") as string;
        }
        catch
        {
            // ACL-protected OS keys (issue #13) hold Windows' value, never script residue.
            return null;
        }
    }
}
