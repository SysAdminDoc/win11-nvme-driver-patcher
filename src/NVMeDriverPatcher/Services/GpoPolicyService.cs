using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class PolicyOverlay
{
    public PatchProfile? PatchProfile { get; set; }
    public bool? IncludeServerKey { get; set; }
    public bool? SkipWarnings { get; set; }
    public bool? WatchdogAutoRevert { get; set; }
    public int? WatchdogWindowHours { get; set; }
    public bool? CompatTelemetryEnabled { get; set; }

    public bool AnyApplied =>
        PatchProfile is not null || IncludeServerKey is not null || SkipWarnings is not null ||
        WatchdogAutoRevert is not null || WatchdogWindowHours is not null ||
        CompatTelemetryEnabled is not null;
}

// Reads the Group Policy values written by the shipped ADMX template at
// HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher. Policy values take precedence over
// the user's config.json — if a sysadmin has pinned "PatchProfile = Safe" via GPO, the
// per-user setting becomes advisory.
public static class GpoPolicyService
{
    private const string PolicySubKey = @"SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher";

    public static PolicyOverlay Read()
    {
        var overlay = new PolicyOverlay();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(PolicySubKey);
            if (key is null) return overlay;

            overlay.PatchProfile = ReadProfile(key, "PatchProfile");
            overlay.IncludeServerKey = ReadBoolDword(key, "IncludeServerKey");
            overlay.SkipWarnings = ReadBoolDword(key, "SkipWarnings");
            overlay.WatchdogAutoRevert = ReadBoolDword(key, "WatchdogAutoRevert");
            overlay.WatchdogWindowHours = ReadIntDword(key, "WatchdogWindowHours");
            overlay.CompatTelemetryEnabled = ReadBoolDword(key, "CompatTelemetryEnabled");
        }
        catch
        {
            // Permission denied or hive missing. The overlay stays empty — user config wins.
        }
        return overlay;
    }

    /// <summary>
    /// Apply the policy overlay to a loaded config. Values the policy pins take precedence
    /// over whatever the user set locally; unspecified policy values leave the config alone.
    /// </summary>
    public static void ApplyTo(AppConfig config, PolicyOverlay overlay)
    {
        if (overlay.PatchProfile is PatchProfile profile) config.PatchProfile = profile;
        if (overlay.IncludeServerKey is bool inc) config.IncludeServerKey = inc;
        if (overlay.SkipWarnings is bool skip) config.SkipWarnings = skip;
    }

    private static PatchProfile? ReadProfile(RegistryKey key, string valueName)
    {
        try
        {
            // The ADMX writes "Safe"/"Full" as REG_SZ via enabledValue/disabledValue.
            var raw = key.GetValue(valueName) as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return Enum.TryParse<PatchProfile>(raw, ignoreCase: true, out var parsed)
                   && Enum.IsDefined(typeof(PatchProfile), parsed)
                ? parsed
                : null;
        }
        catch { return null; }
    }

    private static bool? ReadBoolDword(RegistryKey key, string valueName)
    {
        try
        {
            var raw = key.GetValue(valueName);
            return raw switch
            {
                int i => i != 0,
                long l => l != 0,
                _ => null
            };
        }
        catch { return null; }
    }

    private static int? ReadIntDword(RegistryKey key, string valueName)
    {
        try
        {
            var raw = key.GetValue(valueName);
            return raw switch
            {
                int i => i,
                long l => (int)l,
                _ => null
            };
        }
        catch { return null; }
    }
}
