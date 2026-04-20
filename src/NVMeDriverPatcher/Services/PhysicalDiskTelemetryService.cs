using System.Management;

namespace NVMeDriverPatcher.Services;

public class PhysicalDiskTelemetry
{
    public string FriendlyName { get; set; } = string.Empty;
    public string HealthStatus { get; set; } = string.Empty;
    public string OperationalStatus { get; set; } = string.Empty;
    public long? Size { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string BusType { get; set; } = string.Empty;
    public int? SpindleSpeed { get; set; }
    public ulong? ReadErrorsUncorrected { get; set; }
    public ulong? WriteErrorsUncorrected { get; set; }
    public ulong? Temperature { get; set; }
    public double? Wear { get; set; }
    public ulong? PowerOnHours { get; set; }
    public bool PredictiveFailure { get; set; }
}

// Queries MSFT_PhysicalDisk (root\Microsoft\Windows\Storage) and MSFT_StorageReliabilityCounter
// for every physical disk. Feeds the telemetry tab with data that stays valid whether the
// patch is applied or not — lets the user correlate driver swap with long-term reliability
// deltas (closes ROADMAP §2.4).
public static class PhysicalDiskTelemetryService
{
    public static List<PhysicalDiskTelemetry> Collect()
    {
        var results = new List<PhysicalDiskTelemetry>();
        try
        {
            using var search = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, HealthStatus, OperationalStatus, Size, MediaType, BusType, SpindleSpeed, ObjectId FROM MSFT_PhysicalDisk");
            using var collection = search.Get();
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject mo) continue;
                using (mo)
                {
                    var t = new PhysicalDiskTelemetry
                    {
                        FriendlyName = mo["FriendlyName"]?.ToString() ?? string.Empty,
                        HealthStatus = HealthStatusName(mo["HealthStatus"]),
                        OperationalStatus = OpStatusName(mo["OperationalStatus"]),
                        MediaType = MediaTypeName(mo["MediaType"]),
                        BusType = BusTypeName(mo["BusType"])
                    };
                    if (TryU64(mo["Size"], out var size)) t.Size = (long)size;
                    if (TryInt(mo["SpindleSpeed"], out var rpm)) t.SpindleSpeed = rpm;
                    PopulateReliabilityCounters(mo["ObjectId"] as string, t);
                    results.Add(t);
                }
            }
        }
        catch { /* storage WMI can be missing on minimal SKUs — return partial */ }
        return results;
    }

    private static void PopulateReliabilityCounters(string? objectId, PhysicalDiskTelemetry t)
    {
        if (string.IsNullOrEmpty(objectId)) return;
        try
        {
            using var search = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT ReadErrorsUncorrected, WriteErrorsUncorrected, Temperature, Wear, PowerOnHours FROM MSFT_StorageReliabilityCounter WHERE DeviceId LIKE '%'");
            using var collection = search.Get();
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject mo) continue;
                using (mo)
                {
                    if (TryU64(mo["ReadErrorsUncorrected"], out var rerr)) t.ReadErrorsUncorrected = rerr;
                    if (TryU64(mo["WriteErrorsUncorrected"], out var werr)) t.WriteErrorsUncorrected = werr;
                    if (TryU64(mo["Temperature"], out var temp)) t.Temperature = temp;
                    if (TryU64(mo["PowerOnHours"], out var hrs)) t.PowerOnHours = hrs;
                    if (mo["Wear"] is not null && double.TryParse(mo["Wear"].ToString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var wear))
                        t.Wear = wear;
                    break;
                }
            }
        }
        catch { }
    }

    internal static string HealthStatusName(object? raw) => raw is ushort u ? u switch
    {
        0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", 5 => "Unknown", _ => $"Health({u})"
    } : "Unknown";

    internal static string OpStatusName(object? raw) => raw is ushort[] arr && arr.Length > 0
        ? string.Join(",", arr.Select(v => v.ToString()))
        : "Unknown";

    internal static string MediaTypeName(object? raw) => raw is ushort u ? u switch
    {
        0 => "Unspecified", 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => $"MediaType({u})"
    } : "Unknown";

    internal static string BusTypeName(object? raw) => raw is ushort u ? u switch
    {
        1 => "SCSI", 3 => "ATA", 6 => "Fibre Channel", 7 => "USB", 8 => "SAS",
        11 => "SATA", 17 => "NVMe", _ => $"Bus({u})"
    } : "Unknown";

    private static bool TryU64(object? raw, out ulong value)
    {
        value = 0;
        if (raw is null) return false;
        try { value = Convert.ToUInt64(raw); return true; }
        catch { return false; }
    }

    private static bool TryInt(object? raw, out int value)
    {
        value = 0;
        if (raw is null) return false;
        try { value = Convert.ToInt32(raw); return true; }
        catch { return false; }
    }
}
