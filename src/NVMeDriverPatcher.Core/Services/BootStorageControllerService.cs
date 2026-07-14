using System.Management;
using System.Text.RegularExpressions;

namespace NVMeDriverPatcher.Services;

public enum WinPEControllerCoverage
{
    Inbox,
    PendingInjection,
    Injected,
    Missing
}

public sealed class BootStorageController
{
    public string InstanceId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string InfName { get; set; } = string.Empty;
    public string DriverProvider { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public WinPEControllerCoverage Coverage { get; set; }
    public string? PackageRelativeInfPath { get; set; }
    public string? PackageSha256 { get; set; }
    public string Detail { get; set; } = string.Empty;

    public bool RequiresInjection => Coverage == WinPEControllerCoverage.PendingInjection;
    public string SourceFingerprint => string.Join('|',
        InstanceId.Trim().ToUpperInvariant(),
        InfName.Trim().ToLowerInvariant(),
        DriverVersion.Trim(),
        ServiceName.Trim().ToLowerInvariant());
}

public sealed class BootStorageControllerInventory
{
    public bool ProbeSucceeded { get; set; }
    public string? ProbeError { get; set; }
    public List<BootStorageController> Controllers { get; set; } = [];
    public bool Complete => ProbeSucceeded && Controllers.Count > 0 &&
                            Controllers.All(c => c.Coverage is WinPEControllerCoverage.Inbox or
                                WinPEControllerCoverage.Injected);

    public string Summary => !ProbeSucceeded
        ? $"Boot-storage controller inventory unavailable: {ProbeError ?? "unknown error"}"
        : Controllers.Count == 0
            ? "No present hardware-backed boot-storage controllers were found."
            : $"Boot-storage controllers: {Controllers.Count(c => c.Coverage == WinPEControllerCoverage.Inbox)} inbox, " +
              $"{Controllers.Count(c => c.Coverage == WinPEControllerCoverage.Injected)} injected, " +
              $"{Controllers.Count(c => c.Coverage is WinPEControllerCoverage.Missing or WinPEControllerCoverage.PendingInjection)} missing/pending.";
}

internal sealed record ControllerDeviceEvidence(
    string InstanceId,
    string FriendlyName,
    string DeviceClass,
    string ServiceName,
    uint ConfigManagerErrorCode);

internal sealed record ControllerDriverEvidence(
    string InstanceId,
    string InfName,
    string DriverProvider,
    string DriverVersion);

public static partial class BootStorageControllerService
{
    public static BootStorageControllerInventory Inventory()
    {
        try
        {
            var devices = ReadPresentDevices();
            var drivers = ReadSignedDrivers();
            return BuildInventory(devices, drivers, probeSucceeded: true);
        }
        catch (Exception ex)
        {
            return new BootStorageControllerInventory
            {
                ProbeSucceeded = false,
                ProbeError = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    internal static BootStorageControllerInventory BuildInventory(
        IEnumerable<ControllerDeviceEvidence> deviceEvidence,
        IEnumerable<ControllerDriverEvidence> driverEvidence,
        bool probeSucceeded,
        string? probeError = null)
    {
        var result = new BootStorageControllerInventory
        {
            ProbeSucceeded = probeSucceeded,
            ProbeError = probeError
        };
        if (!probeSucceeded) return result;

        var drivers = driverEvidence
            .Where(d => !string.IsNullOrWhiteSpace(d.InstanceId))
            .GroupBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var device in deviceEvidence
                     .Where(d => d.ConfigManagerErrorCode == 0)
                     .Where(d => IsHardwareBackedInstance(d.InstanceId))
                     .GroupBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.First())
                     .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            drivers.TryGetValue(device.InstanceId, out var driver);
            var infName = driver?.InfName?.Trim() ?? string.Empty;
            var controller = new BootStorageController
            {
                InstanceId = device.InstanceId,
                FriendlyName = string.IsNullOrWhiteSpace(device.FriendlyName)
                    ? device.InstanceId
                    : device.FriendlyName,
                DeviceClass = device.DeviceClass,
                ServiceName = device.ServiceName,
                InfName = infName,
                DriverProvider = driver?.DriverProvider ?? string.Empty,
                DriverVersion = driver?.DriverVersion ?? string.Empty
            };

            if (string.IsNullOrWhiteSpace(infName))
            {
                controller.Coverage = WinPEControllerCoverage.Missing;
                controller.Detail = "The present controller has no observable bound INF.";
            }
            else if (IsOemInf(infName))
            {
                controller.Coverage = WinPEControllerCoverage.PendingInjection;
                controller.Detail = $"Bound OEM package {infName} must be exported and injected.";
            }
            else
            {
                controller.Coverage = WinPEControllerCoverage.Inbox;
                controller.Detail = $"Bound package {infName} is an inbox INF and is already available to WinPE.";
            }
            result.Controllers.Add(controller);
        }
        return result;
    }

    internal static bool IsHardwareBackedInstance(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        return instanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith("VMBUS\\", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsOemInf(string? infName) =>
        !string.IsNullOrWhiteSpace(infName) && OemInfRegex().IsMatch(Path.GetFileName(infName));

    private static List<ControllerDeviceEvidence> ReadPresentDevices()
    {
        var devices = new List<ControllerDeviceEvidence>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Name, PNPClass, Service, ConfigManagerErrorCode " +
            "FROM Win32_PnPEntity WHERE PNPClass='SCSIAdapter' OR PNPClass='HDC'");
        using var results = WmiQueryHelper.ExecuteWithTimeout(searcher);
        foreach (var raw in results)
        {
            if (raw is not ManagementObject device) continue;
            using (device)
            {
                var instanceId = device["DeviceID"] as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(instanceId)) continue;
                uint errorCode;
                try { errorCode = Convert.ToUInt32(device["ConfigManagerErrorCode"] ?? uint.MaxValue); }
                catch { errorCode = uint.MaxValue; }
                devices.Add(new(
                    instanceId,
                    device["Name"] as string ?? string.Empty,
                    device["PNPClass"] as string ?? string.Empty,
                    device["Service"] as string ?? string.Empty,
                    errorCode));
            }
        }
        return devices;
    }

    private static List<ControllerDriverEvidence> ReadSignedDrivers()
    {
        var drivers = new List<ControllerDriverEvidence>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, InfName, DriverProviderName, DriverVersion " +
            "FROM Win32_PnPSignedDriver WHERE DeviceClass='SCSIAdapter' OR DeviceClass='HDC'");
        using var results = WmiQueryHelper.ExecuteWithTimeout(searcher);
        foreach (var raw in results)
        {
            if (raw is not ManagementObject driver) continue;
            using (driver)
            {
                var instanceId = driver["DeviceID"] as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(instanceId)) continue;
                drivers.Add(new(
                    instanceId,
                    driver["InfName"] as string ?? string.Empty,
                    driver["DriverProviderName"] as string ?? string.Empty,
                    driver["DriverVersion"] as string ?? string.Empty));
            }
        }
        return drivers;
    }

    [GeneratedRegex("^oem[0-9]+\\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OemInfRegex();
}
