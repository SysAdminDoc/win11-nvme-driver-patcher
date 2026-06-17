using System.Management;

namespace NVMeDriverPatcher.Services;

public class ControllerAudit
{
    public string InstanceId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string BoundDriver { get; set; } = string.Empty;
    public bool IsNative { get; set; }
    public string QueueDepth { get; set; } = "Unknown";
    public string Firmware { get; set; } = string.Empty;

    // PnP driver-method evidence (RD P1). When nvmedisk.sys is bound with no patch
    // breadcrumbs, these fields let a reader tell an official rollout from a forced
    // "driver method" install: a forced install shows a non-Microsoft INF/provider or a
    // GenNvmeDisk compatible ID that the inbox stack would not have matched on its own.
    public string InfName { get; set; } = string.Empty;
    public string DriverProvider { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;
    public string CompatibleId { get; set; } = string.Empty;
}

public class PerControllerAuditReport
{
    public List<ControllerAudit> Controllers { get; set; } = new();
    public int NativeCount => Controllers.Count(c => c.IsNative);
    public int LegacyCount => Controllers.Count(c => !c.IsNative);
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Pure renderer for support-bundle PnP evidence (RD P1). For every native
    /// (nvmedisk.sys-bound) controller it prints the INF, driver provider, device class, and
    /// hardware/compatible IDs so a triager can distinguish Microsoft's official rollout from a
    /// forced Device Manager/PnPUtil "driver method" install. Returns a short "no native
    /// controllers" line when nothing is bound to the native stack.
    /// </summary>
    public string RenderForcedDriverEvidence()
    {
        var native = Controllers.Where(c => c.IsNative).ToList();
        if (native.Count == 0)
            return "No nvmedisk.sys-bound controllers — no forced-driver evidence to capture.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("nvmedisk.sys is bound on the controllers below. A Microsoft INF/provider with");
        sb.AppendLine("no patch breadcrumbs indicates the official rollout; a non-Microsoft provider or a");
        sb.AppendLine("manually-matched compatible ID indicates a forced 'driver method' install that must");
        sb.AppendLine("be rolled back in Device Manager (not by registry cleanup).");
        foreach (var c in native)
        {
            sb.AppendLine($"  {c.FriendlyName}  (id={c.InstanceId})");
            sb.AppendLine($"    INF        : {Blankable(c.InfName)}");
            sb.AppendLine($"    Provider   : {Blankable(c.DriverProvider)}");
            sb.AppendLine($"    Class      : {Blankable(c.DeviceClass)}");
            sb.AppendLine($"    HardwareID : {Blankable(c.HardwareId)}");
            sb.AppendLine($"    CompatID   : {Blankable(c.CompatibleId)}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Blankable(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;
}

// Per-controller version of PatchVerificationService. Enumerates every NVMe PnP instance
// and reports which driver is bound at that instance, so a user with multiple NVMe drives
// can see exactly which ones migrated and which didn't. Closes ROADMAP §2.1.
public static class PerControllerAuditService
{
    public static PerControllerAuditReport Audit()
    {
        var report = new PerControllerAuditReport();

        try
        {
            // Win32_PnPSignedDriver gives us DriverName + InfName + DeviceID for storage
            // controllers. Filter on DeviceClass=SCSIAdapter|DiskDrive for both NVMe and SCSI.
            using var search = new ManagementObjectSearcher(
                "SELECT DeviceID, FriendlyName, DriverName, InfName, DriverVersion, DeviceClass, " +
                "DriverProviderName, HardWareID, CompatID " +
                "FROM Win32_PnPSignedDriver " +
                "WHERE DeviceClass='SCSIAdapter' OR DeviceClass='DiskDrive'");
            using var collection = search.Get();
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject mo) continue;
                using (mo)
                {
                    var devId = (mo["DeviceID"] as string) ?? string.Empty;
                    var driver = (mo["DriverName"] as string) ?? string.Empty;
                    var inf = (mo["InfName"] as string) ?? string.Empty;
                    var name = (mo["FriendlyName"] as string) ?? string.Empty;
                    if (string.IsNullOrEmpty(driver)) continue;

                    var isNvme = driver.IndexOf("nvme", StringComparison.OrdinalIgnoreCase) >= 0
                              || inf.IndexOf("nvme", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isNvme) continue;

                    report.Controllers.Add(new ControllerAudit
                    {
                        InstanceId = devId,
                        FriendlyName = name,
                        BoundDriver = driver,
                        IsNative = driver.IndexOf("nvmedisk", StringComparison.OrdinalIgnoreCase) >= 0,
                        InfName = inf,
                        DriverProvider = (mo["DriverProviderName"] as string) ?? string.Empty,
                        DeviceClass = (mo["DeviceClass"] as string) ?? string.Empty,
                        // HardWareID / CompatID come back as string[] in WMI; take the primary entry.
                        HardwareId = FirstOf(mo["HardWareID"]),
                        CompatibleId = FirstOf(mo["CompatID"]),
                    });
                }
            }
        }
        catch
        {
            // WMI refusal: return whatever we managed to get.
        }

        report.Summary = report.Controllers.Count == 0
            ? "No NVMe controllers detected (or WMI query denied)."
            : $"NVMe controllers: {report.NativeCount} native, {report.LegacyCount} legacy.";
        return report;
    }

    // WMI string-array properties (HardWareID, CompatID) arrive as string[]; some providers
    // return a bare string. Take the primary (first) entry either way.
    private static string FirstOf(object? value) => value switch
    {
        string[] arr when arr.Length > 0 => arr[0] ?? string.Empty,
        string s => s,
        _ => string.Empty,
    };
}
