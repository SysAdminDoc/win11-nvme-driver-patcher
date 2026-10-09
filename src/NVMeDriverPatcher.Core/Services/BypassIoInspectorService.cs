using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NVMeDriverPatcher.Data;
using NVMeDriverPatcher.Interop;

namespace NVMeDriverPatcher.Services;

public class BypassIoVolumeInfo
{
    public string Letter { get; set; } = string.Empty;
    public string Status { get; set; } = "Unknown";
    public string Stack { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Detail { get; set; } = string.Empty;
    public bool RegistryValuePresent { get; set; }
    public bool RegistryEnabled { get; set; }
    public string DeviceService { get; set; } = string.Empty;
    public int QueryExitCode { get; set; } = -1;
}

internal sealed record BypassIoRegistryEvidence(
    bool Readable,
    bool ValuePresent,
    bool Enabled,
    string Detail);

internal sealed record BypassIoDeviceEvidence(
    bool Readable,
    string ServiceName,
    string Detail);

// Per-volume inspector around `fsutil bypassio state <drive>`. Post-patch, nvmedisk.sys refuses
// BypassIO — this lets the user see exactly which volumes lost it. The state verdict is based on
// the non-localized storport registry value and each volume's own PnP DEVPKEY_Device_Service binding; fsutil is used
// only for its locale-independent query exit code and retained as diagnostic output.
/// <summary>One recorded BypassIO snapshot: every volume captured at the same moment.</summary>
public sealed record BypassIoSnapshotGroup(DateTime TakenAt, IReadOnlyList<BypassIoHistoryRecord> Volumes);

/// <summary>
/// The latest pre-patch and post-patch snapshots side by side, and the volumes that had BypassIO
/// before the patch and lost it after. Either snapshot is null when none was recorded.
/// </summary>
public sealed record BypassIoHistoryDiff(
    BypassIoSnapshotGroup? Pre,
    BypassIoSnapshotGroup? Post,
    IReadOnlyList<string> LostAfterPatch)
{
    public bool Recorded => Pre is not null || Post is not null;
}

public static class BypassIoInspectorService
{
    internal const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Services\storport\Parameters";

    /// <summary>
    /// Pure: pairs the newest pre-patch snapshot with the newest post-patch one. The lists come
    /// newest first (<see cref="DataService.GetBypassIoLatestPair"/>); a snapshot is the records
    /// that share the first record's timestamp. The CLI's text and JSON output both come from this,
    /// so the two can't drift.
    /// </summary>
    public static BypassIoHistoryDiff DiffLatestPair(
        IReadOnlyList<BypassIoHistoryRecord> pre,
        IReadOnlyList<BypassIoHistoryRecord> post)
    {
        var preGroup = Newest(pre);
        var postGroup = Newest(post);
        var lost = preGroup is null || postGroup is null
            ? []
            : preGroup.Volumes
                .Where(p => p.Enabled && postGroup.Volumes.Any(q =>
                    string.Equals(q.VolumeLetter, p.VolumeLetter, StringComparison.OrdinalIgnoreCase) && !q.Enabled))
                .Select(p => p.VolumeLetter)
                .ToList();
        return new BypassIoHistoryDiff(preGroup, postGroup, lost);

        static BypassIoSnapshotGroup? Newest(IReadOnlyList<BypassIoHistoryRecord> records)
        {
            if (records.Count == 0) return null;
            var takenAt = records[0].Timestamp;
            return new BypassIoSnapshotGroup(takenAt, records.Where(r => r.Timestamp == takenAt).ToList());
        }
    }
    internal const string RegistryValueName = "EnableBypassIO";

    public static string BuildGamingImpactSummary(IEnumerable<BypassIoVolumeInfo> volumes)
    {
        var enabledVolumes = volumes
            .Where(v => v.Enabled)
            .Select(v => v.Letter)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        if (enabledVolumes.Count == 0)
            return "Gaming impact: none. BypassIO is already off on all volumes.";

        var volumeList = string.Join(", ", enabledVolumes);
        return $"Gaming impact: BypassIO is active on {enabledVolumes.Count} volume(s) ({volumeList}). " +
            $"After patching to nvmedisk.sys, DirectStorage titles such as {DriveService.DirectStorageGameExamplesText} can fall back to legacy I/O with higher CPU use or stutter. " +
            "The native-NVMe mutation is machine-wide, so a game-library drive cannot be excluded; remove the patch or accept this global tradeoff.";
    }

    internal sealed record BypassIoQueryResult(int ExitCode, string Stdout, string Stderr);

    public static List<BypassIoVolumeInfo> Inspect()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.Name[..2])
                .ToList();
            return InspectVolumes(drives, ReadRegistryEvidence(), CreateVolumeDeviceResolver(), RunFsutilQuery);
        }
        catch { return []; }
    }

    /// <summary>
    /// Inspects each volume against its own storage controller. <paramref name="resolveDevice"/> and
    /// <paramref name="runQuery"/> are the two OS seams (PnP walk and fsutil), so the per-volume
    /// verdict can be exercised without hardware.
    /// </summary>
    internal static List<BypassIoVolumeInfo> InspectVolumes(
        IEnumerable<string> drives,
        BypassIoRegistryEvidence registry,
        Func<string, BypassIoDeviceEvidence> resolveDevice,
        Func<string, BypassIoQueryResult?> runQuery)
    {
        var results = new List<BypassIoVolumeInfo>();
        foreach (var drive in drives)
        {
            var info = InspectOne(drive, registry, resolveDevice, runQuery);
            if (info is not null) results.Add(info);
        }
        return results;
    }

    internal static BypassIoVolumeInfo? InspectOne(string drive) =>
        InspectOne(drive, ReadRegistryEvidence(), CreateVolumeDeviceResolver(), RunFsutilQuery);

    internal static BypassIoVolumeInfo? InspectOne(
        string drive,
        BypassIoRegistryEvidence registry,
        Func<string, BypassIoDeviceEvidence> resolveDevice,
        Func<string, BypassIoQueryResult?> runQuery)
    {
        try
        {
            var query = runQuery(drive);
            if (query is null) return null;
            return BuildVolumeInfo(drive, registry, resolveDevice(drive), query.ExitCode, query.Stdout, query.Stderr);
        }
        catch { return null; }
    }

    private static BypassIoQueryResult? RunFsutilQuery(string drive)
    {
        var psi = new ProcessStartInfo(SystemToolPathService.Resolve("fsutil.exe"))
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
            return new BypassIoQueryResult(-1, string.Empty, "fsutil bypassio query timed out after 10s");
        }

        return new BypassIoQueryResult(
            proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    internal static BypassIoVolumeInfo BuildVolumeInfo(
        string drive,
        BypassIoRegistryEvidence registry,
        BypassIoDeviceEvidence device,
        int queryExitCode,
        string stdout,
        string stderr)
    {
        var serviceName = NormalizeServiceName(device.ServiceName);
        var info = new BypassIoVolumeInfo
        {
            Letter = drive,
            RegistryValuePresent = registry.ValuePresent,
            RegistryEnabled = registry.Enabled,
            DeviceService = string.IsNullOrWhiteSpace(serviceName) ? "Unknown" : serviceName,
            Stack = StackName(serviceName),
            QueryExitCode = queryExitCode
        };

        info.Detail = BuildDetail(registry, device, queryExitCode, stdout, stderr);
        if (!registry.Readable || !device.Readable)
        {
            info.Status = "Unknown";
            return info;
        }

        if (queryExitCode != 0)
        {
            info.Status = "Query failed";
            return info;
        }

        info.Enabled = EvaluateEnabled(registry.Enabled, serviceName, queryExitCode);
        info.Status = info.Enabled ? "Enabled" : "Disabled";
        return info;
    }

    internal static bool EvaluateEnabled(bool registryEnabled, string deviceService, int queryExitCode) =>
        registryEnabled &&
        string.Equals(NormalizeServiceName(deviceService), "stornvme", StringComparison.OrdinalIgnoreCase) &&
        queryExitCode == 0;

    internal static string StorageTypeForService(string serviceName) =>
        IsNvmeService(serviceName) ? "NVMe" : "Unknown";

    internal static string StackName(string serviceName)
    {
        var normalized = NormalizeServiceName(serviceName);
        return string.IsNullOrWhiteSpace(normalized) ? "Unknown" : $"{normalized}.sys";
    }

    internal static bool IsNvmeService(string serviceName) =>
        string.Equals(NormalizeServiceName(serviceName), "stornvme", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(NormalizeServiceName(serviceName), "nvmedisk", StringComparison.OrdinalIgnoreCase);

    internal static BypassIoRegistryEvidence ReadRegistryEvidence()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistrySubKey, writable: false);
            var raw = key?.GetValue(RegistryValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return ClassifyRegistryValue(raw);
        }
        catch (Exception ex)
        {
            return new BypassIoRegistryEvidence(
                Readable: false,
                ValuePresent: false,
                Enabled: false,
                Detail: $"Unable to read HKLM\\{RegistrySubKey}\\{RegistryValueName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Stock Windows never writes EnableBypassIO (the storport service key itself is usually
    /// absent), so a missing value means storport's default, which allows BypassIO. Only an
    /// explicit value other than 1 turns it off.
    /// </summary>
    internal static BypassIoRegistryEvidence ClassifyRegistryValue(object? raw)
    {
        if (raw is null)
        {
            return new BypassIoRegistryEvidence(
                Readable: true,
                ValuePresent: false,
                Enabled: true,
                Detail: $"HKLM\\{RegistrySubKey}\\{RegistryValueName} is not set, so storport uses its default (BypassIO allowed).");
        }

        var numeric = raw switch
        {
            int value => (long)value,
            uint value => value,
            long value => value,
            ulong value when value <= long.MaxValue => (long)value,
            _ => -1L
        };
        var enabled = numeric == 1;
        return new BypassIoRegistryEvidence(
            Readable: true,
            ValuePresent: true,
            Enabled: enabled,
            Detail: $"HKLM\\{RegistrySubKey}\\{RegistryValueName}={(numeric >= 0 ? numeric : "invalid")}; enabled={enabled}.");
    }

    /// <summary>
    /// Pure: turns the services found on a volume's disk node and its parent (the storage
    /// controller) into device evidence. A disk node already bound to nvmedisk wins, since the
    /// patched stack binds there and the controller above it stays on stornvme. Otherwise the
    /// controller's service names the stack. No controller service means the walk failed, which
    /// is reported as unreadable rather than guessed from another volume.
    /// </summary>
    internal static BypassIoDeviceEvidence BuildVolumeDeviceEvidence(
        string drive,
        int diskNumber,
        string? diskService,
        string? controllerService)
    {
        var disk = NormalizeServiceName(diskService);
        var controller = NormalizeServiceName(controllerService);
        var scope = $"Volume {drive} on disk {diskNumber}: DEVPKEY_Device_Service disk node={DisplayService(disk)}, controller={DisplayService(controller)}";
        if (string.Equals(disk, "nvmedisk", StringComparison.OrdinalIgnoreCase))
            return new BypassIoDeviceEvidence(true, "nvmedisk", scope + "; selected=nvmedisk.");
        if (string.IsNullOrWhiteSpace(controller))
            return new BypassIoDeviceEvidence(false, string.Empty, scope + "; no controller service to read.");
        return new BypassIoDeviceEvidence(true, controller, scope + $"; selected={controller}.");

        static string DisplayService(string value) => string.IsNullOrWhiteSpace(value) ? "none" : value;
    }

    /// <summary>
    /// Production seam: volume letter to disk number (MSFT_Partition) to the disk's PnP node, then
    /// CM_Get_Parent to its storage controller, reading DEVPKEY_Device_Service on both. The device
    /// list is enumerated once per resolver. Any step that fails yields unreadable evidence.
    /// </summary>
    internal static Func<string, BypassIoDeviceEvidence> CreateVolumeDeviceResolver()
    {
        Dictionary<uint, string>? services = null;
        string enumerationError = string.Empty;
        var enumerated = false;

        return drive =>
        {
            try
            {
                if (!enumerated)
                {
                    services = ReadDeviceServiceMap(out enumerationError);
                    enumerated = true;
                }
                if (services is null)
                    return Unreadable(drive, enumerationError);

                if (!TryGetDiskNumber(drive, out var diskNumber))
                    return Unreadable(drive, "the disk number could not be read from MSFT_Partition.");
                var pnpId = GetDiskPnpDeviceId(diskNumber);
                if (string.IsNullOrWhiteSpace(pnpId))
                    return Unreadable(drive, $"disk {diskNumber} has no PnP device ID.");

                var locate = NativeMethods.CM_Locate_DevNode(
                    out var diskDevInst, pnpId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);
                if (locate != NativeMethods.CR_SUCCESS)
                    return Unreadable(drive, $"CM_Locate_DevNode failed with CONFIGRET 0x{locate:X8}.");
                var parent = NativeMethods.CM_Get_Parent(out var controllerDevInst, diskDevInst, 0);
                if (parent != NativeMethods.CR_SUCCESS)
                    return Unreadable(drive, $"CM_Get_Parent failed with CONFIGRET 0x{parent:X8}.");

                services.TryGetValue(diskDevInst, out var diskService);
                services.TryGetValue(controllerDevInst, out var controllerService);
                return BuildVolumeDeviceEvidence(drive, diskNumber, diskService, controllerService);
            }
            catch (Exception ex)
            {
                return Unreadable(drive, ex.Message);
            }
        };

        static BypassIoDeviceEvidence Unreadable(string drive, string reason) =>
            new(Readable: false, ServiceName: string.Empty,
                Detail: $"Unable to map volume {drive} to its storage controller: {reason}");
    }

    private static Dictionary<uint, string>? ReadDeviceServiceMap(out string error)
    {
        error = string.Empty;
        var map = new Dictionary<uint, string>();
        using var deviceSet = NativeMethods.SetupDiGetClassDevsAllClasses(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES);
        if (deviceSet.IsInvalid)
        {
            error = $"SetupAPI could not enumerate present devices (Win32 error {Marshal.GetLastWin32Error()}).";
            return null;
        }

        for (uint index = 0; ; index++)
        {
            var device = NativeMethods.SP_DEVINFO_DATA.Create();
            if (!NativeMethods.SetupDiEnumDeviceInfo(deviceSet, index, ref device))
            {
                if (Marshal.GetLastWin32Error() == NativeMethods.ERROR_NO_MORE_ITEMS) break;
                continue;
            }

            if (TryReadDeviceService(deviceSet, ref device, out var service))
                map[device.DevInst] = service;
        }
        return map;
    }

    private static bool TryGetDiskNumber(string drive, out int diskNumber)
    {
        diskNumber = -1;
        if (string.IsNullOrEmpty(drive) || !char.IsLetter(drive[0])) return false;
        var letter = (int)char.ToUpperInvariant(drive[0]);
        using var search = new System.Management.ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter={letter}");
        using var results = WmiQueryHelper.ExecuteWithTimeout(search);
        foreach (var raw in results)
        {
            if (raw is not System.Management.ManagementObject part) continue;
            using (part)
            {
                if (int.TryParse(part["DiskNumber"]?.ToString(), out diskNumber)) return true;
            }
        }
        return false;
    }

    private static string? GetDiskPnpDeviceId(int diskNumber)
    {
        using var search = new System.Management.ManagementObjectSearcher(
            $"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index={diskNumber}");
        using var results = WmiQueryHelper.ExecuteWithTimeout(search);
        foreach (var raw in results)
        {
            if (raw is not System.Management.ManagementObject disk) continue;
            using (disk)
            {
                var id = disk["PNPDeviceID"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
        }
        return null;
    }

    private static bool TryReadDeviceService(
        DeviceInfoSetSafeHandle deviceSet,
        ref NativeMethods.SP_DEVINFO_DATA device,
        out string service)
    {
        service = string.Empty;
        var propertyKey = NativeMethods.DEVPKEY_Device_Service;
        if (!NativeMethods.SetupDiGetDeviceProperty(
                deviceSet,
                ref device,
                in propertyKey,
                out var propertyType,
                IntPtr.Zero,
                0,
                out var requiredSize,
                0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ERROR_INSUFFICIENT_BUFFER || requiredSize == 0)
                return false;
        }

        if (requiredSize == 0 || requiredSize > int.MaxValue || propertyType != NativeMethods.DEVPROP_TYPE_STRING)
            return false;

        var buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            if (!NativeMethods.SetupDiGetDeviceProperty(
                    deviceSet,
                    ref device,
                    in propertyKey,
                    out propertyType,
                    buffer,
                    requiredSize,
                    out requiredSize,
                    0) || propertyType != NativeMethods.DEVPROP_TYPE_STRING)
                return false;

            service = Marshal.PtrToStringUni(buffer, (int)(requiredSize / 2))?.TrimEnd('\0').Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(service);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string NormalizeServiceName(string? serviceName)
    {
        var normalized = serviceName?.Trim() ?? string.Empty;
        return normalized.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static string BuildDetail(
        BypassIoRegistryEvidence registry,
        BypassIoDeviceEvidence device,
        int queryExitCode,
        string stdout,
        string stderr)
    {
        var raw = string.IsNullOrWhiteSpace(stderr)
            ? stdout.Trim()
            : $"{stdout}{Environment.NewLine}{stderr}".Trim();
        var detail = $"Evidence: {registry.Detail} {device.Detail} fsutil exit code={queryExitCode}.";
        if (!string.IsNullOrWhiteSpace(raw)) detail += Environment.NewLine + raw;
        return detail.Length > 600 ? detail[..600] + "…" : detail;
    }
}
