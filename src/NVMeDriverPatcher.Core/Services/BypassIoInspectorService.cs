using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
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
// the non-localized storport registry value and PnP DEVPKEY_Device_Service binding; fsutil is used
// only for its locale-independent query exit code and retained as diagnostic output.
public static class BypassIoInspectorService
{
    internal const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Services\storport\Parameters";
    internal const string RegistryValueName = "EnableBypassIO";

    private static readonly string[] StorageServicePriority =
    [
        "nvmedisk",
        "stornvme",
        "storahci",
        "iaStorAC",
        "iaStorAVC",
        "iaStorV",
        "vmd",
        "nvraid",
        "disk"
    ];

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
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.Name[..2])
                .ToList();
            if (drives.Count == 0) return results;

            var registry = ReadRegistryEvidence();
            var device = ReadDeviceServiceEvidence();
            foreach (var drive in drives)
            {
                var info = InspectOne(drive, registry, device);
                if (info is not null) results.Add(info);
            }
        }
        catch { }
        return results;
    }

    internal static BypassIoVolumeInfo? InspectOne(string drive)
    {
        return InspectOne(drive, ReadRegistryEvidence(), ReadDeviceServiceEvidence());
    }

    private static BypassIoVolumeInfo? InspectOne(
        string drive,
        BypassIoRegistryEvidence registry,
        BypassIoDeviceEvidence device)
    {
        try
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
                return BuildVolumeInfo(drive, registry, device, -1, string.Empty,
                    "fsutil bypassio query timed out after 10s");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            return BuildVolumeInfo(drive, registry, device, proc.ExitCode, stdout, stderr);
        }
        catch { return null; }
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
            if (key is null)
            {
                return new BypassIoRegistryEvidence(
                    Readable: true,
                    ValuePresent: false,
                    Enabled: false,
                    Detail: $"HKLM\\{RegistrySubKey}\\{RegistryValueName} is not present (treated as disabled).");
            }

            var raw = key.GetValue(RegistryValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null)
            {
                return new BypassIoRegistryEvidence(
                    Readable: true,
                    ValuePresent: false,
                    Enabled: false,
                    Detail: $"HKLM\\{RegistrySubKey}\\{RegistryValueName} is not present (treated as disabled).");
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
        catch (Exception ex)
        {
            return new BypassIoRegistryEvidence(
                Readable: false,
                ValuePresent: false,
                Enabled: false,
                Detail: $"Unable to read HKLM\\{RegistrySubKey}\\{RegistryValueName}: {ex.Message}");
        }
    }

    internal static BypassIoDeviceEvidence ReadDeviceServiceEvidence()
    {
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var deviceSet = NativeMethods.SetupDiGetClassDevsAllClasses(
                IntPtr.Zero,
                null,
                IntPtr.Zero,
                NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES);
            if (deviceSet.IsInvalid)
            {
                return new BypassIoDeviceEvidence(
                    Readable: false,
                    ServiceName: string.Empty,
                    Detail: $"SetupAPI could not enumerate present devices (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            for (uint index = 0; ; index++)
            {
                var device = NativeMethods.SP_DEVINFO_DATA.Create();
                if (!NativeMethods.SetupDiEnumDeviceInfo(deviceSet, index, ref device))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == NativeMethods.ERROR_NO_MORE_ITEMS) break;
                    continue;
                }

                if (TryReadDeviceService(deviceSet, ref device, out var service))
                {
                    var normalized = NormalizeServiceName(service);
                    if (StorageServicePriority.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                        services.Add(normalized);
                }
            }

            var selected = SelectStorageService(services);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return new BypassIoDeviceEvidence(
                    Readable: true,
                    ServiceName: string.Empty,
                    Detail: "DEVPKEY_Device_Service exposed no recognized storage-driver binding.");
            }

            return new BypassIoDeviceEvidence(
                Readable: true,
                ServiceName: selected,
                Detail: $"DEVPKEY_Device_Service storage binding(s): {string.Join(", ", services.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}; selected={selected}.");
        }
        catch (Exception ex)
        {
            return new BypassIoDeviceEvidence(
                Readable: false,
                ServiceName: string.Empty,
                Detail: $"Unable to read DEVPKEY_Device_Service: {ex.Message}");
        }
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

    private static string SelectStorageService(IEnumerable<string> services)
    {
        var set = services.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return StorageServicePriority.FirstOrDefault(set.Contains) ?? string.Empty;
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
