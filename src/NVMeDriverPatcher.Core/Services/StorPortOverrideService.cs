using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>One stornvme controller and its per-device StorPort override, when set.
/// <paramref name="EnableNVMeInterfaceOtherKind"/> describes a value that's present but isn't a
/// DWORD (e.g. <c>REG_SZ "1"</c>), which the DWORD field can't hold.</summary>
public sealed record StorPortControllerOverride(
    string InstanceId,
    string? FriendlyName,
    int? EnableNVMeInterface,
    string? EnableNVMeInterfaceOtherKind = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? InstanceId : FriendlyName!;
}

/// <summary>The StorPort values that decide the native NVMe path ahead of, or after, the feature
/// overrides. Read once so classification is pure. A null DWORD means the value isn't set as a
/// DWORD; the matching OtherKind field says when it's there as some other type.</summary>
public sealed record StorPortOverrideSnapshot
{
    public int? DisableNativeNVMeStack { get; init; }
    public string? DisableNativeNVMeStackOtherKind { get; init; }
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
        var (killSwitch, killSwitchOther) = ReadValue(hklm, StorPortControlSubKey, KillSwitchValueName);
        return new StorPortOverrideSnapshot
        {
            DisableNativeNVMeStack = killSwitch,
            DisableNativeNVMeStackOtherKind = killSwitchOther,
            Controllers = ReadControllers(hklm)
        };
    }

    /// <summary>
    /// The reason the native driver isn't bound when StorPort alone explains it: the kill switch
    /// is set, or every listed controller has <c>EnableNVMeInterface=0</c>. Null otherwise, so
    /// the feature-route verdicts still apply.
    /// </summary>
    internal static string? DescribeLegacyHold(StorPortOverrideSnapshot snapshot)
    {
        if (snapshot.DisableNativeNVMeStack is int k && k != 0)
        {
            return $"{KillSwitchValueName} is {k} under {KillSwitchPath}. Windows checks it before any feature " +
                "override, so the native driver can't bind while it's set. This tool didn't set it and doesn't " +
                "change it. Delete the value, or set it to 0, and restart to allow the native driver.";
        }
        if (snapshot.Controllers.Count > 0 && snapshot.Controllers.All(c => c.EnableNVMeInterface == 0))
        {
            return string.Join(" ", snapshot.Controllers.Select(c =>
                $"{c.DisplayName} has {ControllerValueName}=0 under {ControllerKeyPath(c.InstanceId)}, which keeps it on " +
                "the legacy driver whatever the feature overrides say.")) +
                " This tool didn't set these values. Delete them and restart to let the feature overrides decide.";
        }
        return null;
    }

    /// <summary>True when a controller's StorPort value binds nvmedisk on its own.</summary>
    internal static bool ForcesNativeAnywhere(StorPortOverrideSnapshot snapshot) =>
        ForcedNativeControllers(snapshot).Count > 0;

    internal const string ForcedNativeActivationNote =
        "nvmedisk.sys is active because a controller's StorPort EnableNVMeInterface value forces it (see the " +
        "StorPort override check), not because of this tool. Deleting that value and restarting reverts it.";

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
        parts.AddRange(DescribeOtherKinds(snapshot));
        return parts.Count == 0 ? null : new PreflightCheck(CheckStatus.Warning, string.Join(" ", parts));
    }

    // A value written without /t REG_DWORD lands as REG_SZ. The write-ups describe DWORDs, so
    // whether storport honors another type is unknown; name it rather than call it unset.
    private static IEnumerable<string> DescribeOtherKinds(StorPortOverrideSnapshot snapshot)
    {
        if (snapshot.DisableNativeNVMeStackOtherKind is string killSwitchKind)
        {
            yield return $"{KillSwitchValueName} is set under {KillSwitchPath} as {killSwitchKind}, not a DWORD. " +
                "Windows may not read it. Set it as a DWORD or delete it so the result is predictable.";
        }
        foreach (var controller in snapshot.Controllers.Where(c => c.EnableNVMeInterfaceOtherKind is not null))
        {
            yield return $"{controller.DisplayName} has {ControllerValueName} set as {controller.EnableNVMeInterfaceOtherKind}, " +
                $"not a DWORD, under {ControllerKeyPath(controller.InstanceId)}. Windows may not read it. Set it as a DWORD " +
                "or delete it so the result is predictable.";
        }
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
            var notes = legacy.Select(c =>
                $"{c.DisplayName} has {ControllerValueName}=0 under {ControllerKeyPath(c.InstanceId)}, which keeps it on the " +
                "legacy driver whatever the feature overrides say.").Concat(DescribeOtherKinds(snapshot)).ToList();
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }
        if (killSwitch) return null;
        var lines = ForcedNativeControllers(snapshot).Select(c =>
            $"{c.DisplayName} has {ControllerValueName}={c.EnableNVMeInterface} under {ControllerKeyPath(c.InstanceId)}, " +
            "which binds the native driver on its own. Removing this tool's overrides won't change that.").ToList();
        // Bound somewhere doesn't mean bound everywhere: a controller held at 0 stays on stornvme.
        lines.AddRange(snapshot.Controllers.Where(c => c.EnableNVMeInterface == 0).Select(c =>
            $"{c.DisplayName} has {ControllerValueName}=0 under {ControllerKeyPath(c.InstanceId)}, so that controller " +
            "stays on stornvme.sys while the others use nvmedisk."));
        return lines.Count == 0 ? null : string.Join(" ", lines);
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
            $"{KillSwitchValueName} ({KillSwitchPath}): {FormatValue(snapshot.DisableNativeNVMeStack, snapshot.DisableNativeNVMeStackOtherKind)}"
        };
        if (snapshot.Controllers.Count == 0)
        {
            lines.Add("No stornvme controllers were listed under the service's Enum key.");
            return lines;
        }
        foreach (var controller in snapshot.Controllers)
            lines.Add($"{controller.DisplayName} [{controller.InstanceId}]: {ControllerValueName} {FormatValue(controller.EnableNVMeInterface, controller.EnableNVMeInterfaceOtherKind)}");
        return lines;
    }

    private static List<StorPortControllerOverride> ForcedNativeControllers(StorPortOverrideSnapshot snapshot) =>
        snapshot.DisableNativeNVMeStack is int k && k != 0
            ? new List<StorPortControllerOverride>()
            : snapshot.Controllers.Where(c => c.EnableNVMeInterface is int v && v != 0).ToList();

    private static string FormatValue(int? value, string? otherKind) =>
        value is int v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : otherKind is not null ? $"present as {otherKind} (not a DWORD)"
        : "not set";

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
                var (dword, otherKind) = ReadValue(hklm, $@"{EnumRoot}\{instanceId}\Device Parameters\StorPort", ControllerValueName);
                controllers.Add(new StorPortControllerOverride(instanceId, ReadFriendlyName(hklm, instanceId), dword, otherKind));
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

    /// <summary>The value as a DWORD, or, when it's present as another type, a short description
    /// of it (<c>REG_SZ "1"</c>). Both null when it's absent or unreadable.</summary>
    private static (int? Dword, string? OtherKind) ReadValue(RegistryKey hklm, string subKey, string valueName)
    {
        try
        {
            using var key = hklm.OpenSubKey(subKey);
            var raw = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (key is null || raw is null) return (null, null);
            var kind = key.GetValueKind(valueName);
            if (kind == RegistryValueKind.DWord && raw is int value) return (value, null);
            return (null, DescribeOtherKind(kind, raw));
        }
        catch
        {
            return (null, null);
        }
    }

    internal static string DescribeOtherKind(RegistryValueKind kind, object raw) => kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => $"{RegistryTypeName(kind)} \"{Truncate(raw as string ?? string.Empty)}\"",
        RegistryValueKind.QWord => $"REG_QWORD {raw}",
        RegistryValueKind.MultiString => $"REG_MULTI_SZ \"{Truncate(string.Join(" | ", raw as string[] ?? []))}\"",
        RegistryValueKind.Binary => $"REG_BINARY ({(raw as byte[])?.Length ?? 0} bytes)",
        _ => RegistryTypeName(kind)
    };

    private static string RegistryTypeName(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => "REG_SZ",
        RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        RegistryValueKind.QWord => "REG_QWORD",
        RegistryValueKind.MultiString => "REG_MULTI_SZ",
        RegistryValueKind.Binary => "REG_BINARY",
        RegistryValueKind.None => "REG_NONE",
        _ => "an unknown registry type"
    };

    private static string Truncate(string text) => text.Length <= 32 ? text : text[..32] + "...";
}
