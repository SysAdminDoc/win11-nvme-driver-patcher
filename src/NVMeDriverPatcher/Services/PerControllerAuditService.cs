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
}

public class PerControllerAuditReport
{
    public List<ControllerAudit> Controllers { get; set; } = new();
    public int NativeCount => Controllers.Count(c => c.IsNative);
    public int LegacyCount => Controllers.Count(c => !c.IsNative);
    public string Summary { get; set; } = string.Empty;
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
                "SELECT DeviceID, FriendlyName, DriverName, InfName, DriverVersion, DeviceClass " +
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
                        IsNative = driver.IndexOf("nvmedisk", StringComparison.OrdinalIgnoreCase) >= 0
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
}
