using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>One stornvme controller and its per-device StorPort override, when set.</summary>
public sealed record StorPortControllerOverride(string InstanceId, string? FriendlyName, int? EnableNVMeInterface)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? InstanceId : FriendlyName!;
}

/// <summary>The StorPort values that decide the native NVMe path ahead of, or after, the feature
/// overrides. Read once so classification is pure. Null means the value isn't set as a DWORD.</summary>
public sealed record StorPortOverrideSnapshot
{
    public int? DisableNativeNVMeStack { get; init; }
    public IReadOnlyList<StorPortControllerOverride> Controllers { get; init; } = Array.Empty<StorPortControllerOverride>();
}

/// <summary>
/// Reads the two StorPort values other tools now write to force the NVMe path (revoconner,
/// 2026-09-22; St1cky, 2026-10-05). <c>storport!RaDriverAddDevice</c> checks the global
/// <c>DisableNativeNVMeStack</c> before any feature decision, then lets a controller's
/// <c>Device Parameters\StorPort\EnableNVMeInterface</c> override that decision. Either one
/// outranks every route this tool writes, so their leftovers have to be named. Read-only: this
/// tool never writes or deletes them.
/// </summary>
public static class StorPortOverrideService
{
    internal const string StorPortControlSubKey = @"SYSTEM\CurrentControlSet\Control\StorPort";
    internal const string KillSwitchValueName = "DisableNativeNVMeStack";
    internal const string ControllerValueName = "EnableNVMeInterface";
    private const string StornvmeEnumSubKey = @"SYSTEM\CurrentControlSet\Services\stornvme\Enum";
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    internal static string KillSwitchPath => $@"HKLM\{StorPortControlSubKey}";

    internal static string ControllerKeyPath(string instanceId) =>
        $@"HKLM\{EnumRoot}\{instanceId}\Device Parameters\StorPort";

    public static StorPortOverrideSnapshot ReadSnapshot()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return new StorPortOverrideSnapshot
        {
            DisableNativeNVMeStack = ReadDword(hklm, StorPortControlSubKey, KillSwitchValueName),
            Controllers = ReadControllers(hklm)
        };
    }

    /// <summary>A readiness Warning naming each value that's set, or null when none is.</summary>
    internal static PreflightCheck? Classify(StorPortOverrideSnapshot snapshot)
    {
        var parts = new List<string>();
        bool killSwitch = snapshot.DisableNativeNVMeStack is int k && k != 0;
        if (killSwitch)
        {
            parts.Add($"{KillSwitchValueName} is {snapshot.DisableNativeNVMeStack} under {KillSwitchPath}. Windows checks it " +
                "before any feature override, so the native driver can't bind while it's set. This tool didn't set it and " +
                "doesn't change it. Delete the value, or set it to 0, and restart to allow the native driver.");
        }
        foreach (var controller in snapshot.Controllers.Where(c => c.EnableNVMeInterface is not null))
        {
            var where = $"{ControllerValueName}={controller.EnableNVMeInterface} under {ControllerKeyPath(controller.InstanceId)}";
            if (controller.EnableNVMeInterface == 0)
            {
                parts.Add($"{controller.DisplayName} has {where}. Windows reads it after the feature overrides and keeps " +
                    "this controller on the legacy driver, so the patch won't bind there.");
            }
            else if (killSwitch)
            {
                parts.Add($"{controller.DisplayName} has {where}, but {KillSwitchValueName} wins over it.");
            }
            else
            {
                parts.Add($"{controller.DisplayName} has {where}. Windows reads it after the feature overrides, so this " +
                    "controller uses the native driver whatever this tool sets, and Remove doesn't take it off.");
            }
        }
        return parts.Count == 0 ? null : new PreflightCheck(CheckStatus.Warning, string.Join(" ", parts));
    }

    /// <summary>The post-reboot reason, when a StorPort value explains the bind result.</summary>
    internal static string? DescribeForVerdict(StorPortOverrideSnapshot snapshot, bool nativeActive)
    {
        bool killSwitch = snapshot.DisableNativeNVMeStack is int k && k != 0;
        if (!nativeActive)
        {
            if (killSwitch)
            {
                return $"{KillSwitchValueName} is {snapshot.DisableNativeNVMeStack} under {KillSwitchPath}. Windows checks it " +
                    "before any feature override, so the native driver can't bind while it's set. Delete it and restart.";
            }
            var legacy = snapshot.Controllers.Where(c => c.EnableNVMeInterface == 0).ToList();
            if (legacy.Count == 0) return null;
            return string.Join(" ", legacy.Select(c =>
                $"{c.DisplayName} has {ControllerValueName}=0 under {ControllerKeyPath(c.InstanceId)}, which keeps it on the " +
                "legacy driver whatever the feature overrides say."));
        }
        if (killSwitch) return null;
        var forced = ForcedNativeControllers(snapshot);
        if (forced.Count == 0) return null;
        return string.Join(" ", forced.Select(c =>
            $"{c.DisplayName} has {ControllerValueName}={c.EnableNVMeInterface} under {ControllerKeyPath(c.InstanceId)}, " +
            "which binds the native driver on its own. Removing this tool's overrides won't change that."));
    }

    /// <summary>Lines for Remove's result: each controller nvmedisk stays bound on after the restart.</summary>
    internal static IReadOnlyList<string> DescribeAfterRemoval(StorPortOverrideSnapshot snapshot) =>
        ForcedNativeControllers(snapshot).Select(c =>
            $"{c.DisplayName} has {ControllerValueName}={c.EnableNVMeInterface} under {ControllerKeyPath(c.InstanceId)}, so " +
            "nvmedisk stays bound on that controller after the restart. This tool didn't set the value and Remove leaves it. " +
            "Delete it and restart to go back to stornvme.").ToList();

    /// <summary>Support-report lines listing both values, set or not, for every stornvme controller.</summary>
    internal static IReadOnlyList<string> FormatForReport(StorPortOverrideSnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"{KillSwitchValueName} ({KillSwitchPath}): {FormatValue(snapshot.DisableNativeNVMeStack)}"
        };
        if (snapshot.Controllers.Count == 0)
        {
            lines.Add("No stornvme controllers were listed under the service's Enum key.");
            return lines;
        }
        foreach (var controller in snapshot.Controllers)
            lines.Add($"{controller.DisplayName} [{controller.InstanceId}]: {ControllerValueName} {FormatValue(controller.EnableNVMeInterface)}");
        return lines;
    }

    private static List<StorPortControllerOverride> ForcedNativeControllers(StorPortOverrideSnapshot snapshot) =>
        snapshot.DisableNativeNVMeStack is int k && k != 0
            ? new List<StorPortControllerOverride>()
            : snapshot.Controllers.Where(c => c.EnableNVMeInterface is int v && v != 0).ToList();

    private static string FormatValue(int? value) => value is int v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "not set";

    private static List<StorPortControllerOverride> ReadControllers(RegistryKey hklm)
    {
        var controllers = new List<StorPortControllerOverride>();
        try
        {
            using var enumKey = hklm.OpenSubKey(StornvmeEnumSubKey);
            if (enumKey is null) return controllers;
            foreach (var name in enumKey.GetValueNames())
            {
                if (!int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                    continue;
                if (enumKey.GetValue(name) is not string instanceId || string.IsNullOrWhiteSpace(instanceId))
                    continue;
                controllers.Add(new StorPortControllerOverride(
                    instanceId,
                    ReadFriendlyName(hklm, instanceId),
                    ReadDword(hklm, $@"{EnumRoot}\{instanceId}\Device Parameters\StorPort", ControllerValueName)));
            }
        }
        catch
        {
            // An unreadable Enum key leaves the list empty; the report says none were listed.
        }
        return controllers;
    }

    private static string? ReadFriendlyName(RegistryKey hklm, string instanceId)
    {
        try
        {
            using var device = hklm.OpenSubKey($@"{EnumRoot}\{instanceId}");
            var name = device?.GetValue("FriendlyName") as string ?? device?.GetValue("DeviceDesc") as string;
            if (string.IsNullOrWhiteSpace(name)) return null;
            // DeviceDesc is an INF reference like "@stornvme.inf,%desc%;Standard NVM Express Controller".
            int semicolon = name.LastIndexOf(';');
            return semicolon >= 0 ? name[(semicolon + 1)..].Trim() : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>storport reads these as REG_DWORD; any other kind is ignored by Windows, so it reads as unset here.</summary>
    private static int? ReadDword(RegistryKey hklm, string subKey, string valueName)
    {
        try
        {
            using var key = hklm.OpenSubKey(subKey);
            if (key is null) return null;
            return key.GetValueKind(valueName) == RegistryValueKind.DWord && key.GetValue(valueName) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }
}
