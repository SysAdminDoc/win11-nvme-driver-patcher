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
            // Schema bounds (watchdog.schema.json / ADMX): 1–168 hours. Out-of-range or
            // wrapped values from a malformed GPO must not break watchdog timer math.
            overlay.WatchdogWindowHours = ReadIntDword(key, "WatchdogWindowHours", min: 1, max: 168);
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
    public static WatchdogStateSaveResult? ApplyTo(AppConfig config, PolicyOverlay overlay)
    {
        if (overlay.PatchProfile is PatchProfile profile) config.PatchProfile = profile;
        if (overlay.IncludeServerKey is bool inc) config.IncludeServerKey = inc;
        if (overlay.SkipWarnings is bool skip) config.SkipWarnings = skip;
        if (overlay.CompatTelemetryEnabled is bool telem) config.CompatTelemetryEnabled = telem;

        if (overlay.WatchdogAutoRevert is not null || overlay.WatchdogWindowHours is not null)
        {
            return EventLogWatchdogService.UpdateState(config, state =>
                {
                    if (overlay.WatchdogAutoRevert is bool autoRevert) state.AutoRevertEnabled = autoRevert;
                    if (overlay.WatchdogWindowHours is int hours) state.WindowHours = hours;
                });
        }

        return null;
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

    private static int? ReadIntDword(RegistryKey key, string valueName, int min, int max)
    {
        try
        {
            var raw = key.GetValue(valueName);
            // Range-validate instead of blind-casting: a QWORD > int.MaxValue would wrap
            // negative through (int)l, and an out-of-range DWORD is a policy authoring
            // error — both fall back to "no policy value" so defaults apply.
            long? value = raw switch
            {
                int i => i,
                long l => l,
                _ => null
            };
            if (value is not long v) return null;
            return v >= min && v <= max ? (int)v : null;
        }
        catch { return null; }
    }
}
