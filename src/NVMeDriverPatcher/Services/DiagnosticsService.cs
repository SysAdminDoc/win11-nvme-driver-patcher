using System.Management;
using System.Text;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class DiagnosticsService
{
    public static async Task<string?> ExportAsync(
        string workingDir,
        PreflightResult? preflight,
        List<string> logHistory)
    {
        return await Task.Run(() => Export(workingDir, preflight, logHistory));
    }

    public static string? Export(
        string workingDir,
        PreflightResult? preflight,
        List<string> logHistory,
        string? outputPath = null)
    {
        outputPath ??= Path.Combine(workingDir, $"NVMe_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        var sb = new StringBuilder(4096);
        sb.AppendLine("================================================================================");
        sb.AppendLine("NVMe Driver Patcher - System Diagnostics Report");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Version: {AppConfig.AppVersion}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine("SYSTEM INFORMATION");
        sb.AppendLine("------------------");
        sb.AppendLine($"Computer Name: {Environment.MachineName}");
        sb.AppendLine($"User: {Environment.UserName}");
        sb.AppendLine($"OS: {Environment.OSVersion.VersionString}");

        try
        {
            using var search = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
            var os = search.Get().Cast<ManagementObject>().FirstOrDefault();
            if (os is not null)
            {
                sb.AppendLine($"OS Caption: {os["Caption"]}");
                sb.AppendLine($"OS Build: {os["BuildNumber"]}");
                sb.AppendLine($"OS Version: {os["Version"]}");
                sb.AppendLine($"Install Date: {os["InstallDate"]}");
                sb.AppendLine($"Last Boot: {os["LastBootUpTime"]}");
            }
        }
        catch { sb.AppendLine("Unable to retrieve OS information"); }

        sb.AppendLine().AppendLine("HARDWARE").AppendLine("--------");
        try
        {
            using var search = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            var cs = search.Get().Cast<ManagementObject>().FirstOrDefault();
            if (cs is not null)
            {
                sb.AppendLine($"Manufacturer: {cs["Manufacturer"]}");
                sb.AppendLine($"Model: {cs["Model"]}");
                sb.AppendLine($"Total RAM: {Math.Round(Convert.ToInt64(cs["TotalPhysicalMemory"]) / 1073741824.0, 2)} GB");
            }
        }
        catch { sb.AppendLine("Unable to retrieve hardware information"); }

        var drives = preflight?.CachedDrives ?? DriveService.GetSystemDrives();
        sb.AppendLine().AppendLine("STORAGE DRIVES").AppendLine("--------------");
        foreach (var drive in drives)
        {
            string nvmeTag = drive.IsNVMe ? " [NVMe]" : "";
            string bootTag = drive.IsBoot ? " [BOOT]" : "";
            sb.AppendLine($"Disk {drive.Number}: {drive.Name} ({drive.Size}){nvmeTag}{bootTag}");
            sb.AppendLine($"  Bus Type: {drive.BusType}");
            sb.AppendLine($"  PNP ID: {drive.PNPDeviceID}");
        }

        var healthData = preflight?.CachedHealth ?? DriveService.GetNVMeHealthData();
        sb.AppendLine().AppendLine("NVMe HEALTH DATA").AppendLine("-----------------");
        if (healthData.Count > 0)
        {
            foreach (var (diskKey, hd) in healthData)
            {
                sb.AppendLine($"Disk {diskKey}:");
                sb.AppendLine($"  Health: {hd.HealthStatus} | Status: {hd.OperationalStatus}");
                if (hd.Temperature != "N/A") sb.AppendLine($"  Temperature: {hd.Temperature}");
                if (hd.Wear != "N/A") sb.AppendLine($"  Life Remaining: {hd.Wear}");
                if (hd.PowerOnHours != "N/A") sb.AppendLine($"  Power-On Hours: {hd.PowerOnHours}");
                if (hd.MediaErrors > 0) sb.AppendLine($"  Media Errors: {hd.MediaErrors}");
            }
        }
        else sb.AppendLine("  No health data available");

        var driverInfo = preflight?.DriverInfo ?? DriveService.GetNVMeDriverInfo();
        sb.AppendLine().AppendLine("NVMe DRIVER INFORMATION").AppendLine("-----------------------");
        sb.AppendLine($"Current Driver: {driverInfo.CurrentDriver}");
        sb.AppendLine($"Inbox Version: {driverInfo.InboxVersion}");
        sb.AppendLine($"Third-Party: {(driverInfo.HasThirdParty ? driverInfo.ThirdPartyName : "No")}");
        sb.AppendLine($"Queue Depth: {driverInfo.QueueDepth}");

        var nativeStatus = preflight?.NativeNVMeStatus ?? DriveService.TestNativeNVMeActive();
        sb.AppendLine().AppendLine("NATIVE NVMe DRIVER STATUS").AppendLine("-------------------------");
        sb.AppendLine($"Native NVMe Active: {(nativeStatus.IsActive ? "Yes" : "No")}");
        sb.AppendLine($"Active Driver: {nativeStatus.ActiveDriver}");
        sb.AppendLine($"Device Category: {nativeStatus.DeviceCategory}");
        if (nativeStatus.StorageDisks.Count > 0)
        {
            sb.AppendLine("Storage Disks:");
            foreach (var sd in nativeStatus.StorageDisks) sb.AppendLine($"  - {sd}");
        }
        sb.AppendLine($"Details: {nativeStatus.Details}");

        var bypassStatus = preflight?.BypassIOStatus ?? DriveService.GetBypassIOStatus();
        sb.AppendLine().AppendLine("BYPASSIO / DIRECTSTORAGE STATUS").AppendLine("-------------------------------");
        sb.AppendLine($"BypassIO Supported: {(bypassStatus.Supported ? "Yes" : "No")}");
        sb.AppendLine($"Storage Type: {bypassStatus.StorageType}");
        if (!string.IsNullOrEmpty(bypassStatus.BlockedBy)) sb.AppendLine($"Blocked By: {bypassStatus.BlockedBy}");
        if (!string.IsNullOrEmpty(bypassStatus.Warning)) sb.AppendLine($"WARNING: {bypassStatus.Warning}");

        var buildDetails = preflight?.BuildDetails ?? DriveService.GetWindowsBuildDetails();
        sb.AppendLine().AppendLine("WINDOWS BUILD DETAILS").AppendLine("--------------------");
        sb.AppendLine($"Build Number: {buildDetails.BuildNumber}");
        sb.AppendLine($"Display Version: {buildDetails.DisplayVersion}");
        sb.AppendLine($"UBR: {buildDetails.UBR}");
        sb.AppendLine($"Is 24H2+: {buildDetails.Is24H2OrLater}");

        sb.AppendLine().AppendLine("CHASSIS / POWER").AppendLine("---------------");
        sb.AppendLine($"Is Laptop: {(preflight?.IsLaptop ?? false ? "Yes (APST warning applies)" : "No (Desktop)")}");

        sb.AppendLine().AppendLine("BITLOCKER STATUS").AppendLine("----------------");
        sb.AppendLine($"System Drive Encrypted: {(preflight?.BitLockerEnabled ?? false ? "Yes" : "No")}");

        var incompatSw = preflight?.IncompatibleSoftware ?? DriveService.GetIncompatibleSoftware();
        sb.AppendLine().AppendLine("INCOMPATIBLE SOFTWARE").AppendLine("---------------------");
        if (incompatSw.Count > 0)
        {
            foreach (var sw in incompatSw) sb.AppendLine($"  [{sw.Severity}] {sw.Name}: {sw.Message}");
        }
        else sb.AppendLine("  None detected");

        var migration = preflight?.CachedMigration ?? DriveService.GetStorageDiskMigration();
        sb.AppendLine().AppendLine("DRIVE MIGRATION STATUS").AppendLine("----------------------");
        if (migration.Migrated.Count > 0)
        {
            sb.AppendLine("Under 'Storage disks' (native nvmedisk.sys):");
            foreach (var d in migration.Migrated) sb.AppendLine($"  + {d}");
        }
        if (migration.Legacy.Count > 0)
        {
            sb.AppendLine("Under 'Disk drives' (legacy stornvme.sys):");
            foreach (var d in migration.Legacy) sb.AppendLine($"  - {d}");
        }

        var status = RegistryService.GetPatchStatus();
        sb.AppendLine().AppendLine("PATCH STATUS").AppendLine("------------");
        sb.AppendLine($"Applied: {status.Applied}");
        sb.AppendLine($"Partial: {status.Partial}");
        sb.AppendLine($"Components: {status.Count}/{status.Total}");
        sb.AppendLine($"Applied Keys: {string.Join(", ", status.Keys)}");

        // Benchmark history
        var benchHistory = BenchmarkService.GetHistory(workingDir);
        sb.AppendLine().AppendLine("BENCHMARK HISTORY").AppendLine("-----------------");
        if (benchHistory.Count > 0)
        {
            foreach (var bh in benchHistory)
                sb.AppendLine($"  [{bh.Label}] {bh.Timestamp} -- Read: {bh.Read.IOPS} IOPS, Write: {bh.Write.IOPS} IOPS");
        }
        else sb.AppendLine("  No benchmark history");

        sb.AppendLine().AppendLine("ACTIVITY LOG").AppendLine("------------");
        sb.AppendLine(string.Join("\n", logHistory));
        sb.AppendLine().AppendLine("================================================================================");
        sb.AppendLine("End of Diagnostics Report");

        try
        {
            File.WriteAllText(outputPath, sb.ToString());
            return outputPath;
        }
        catch { return null; }
    }
}
