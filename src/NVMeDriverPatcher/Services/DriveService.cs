using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class DriveService
{
    public static List<SystemDrive> GetSystemDrives()
    {
        var drives = new List<SystemDrive>();
        try
        {
            using var msftSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Disk");
            using var win32Search = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");

            var win32Disks = win32Search.Get().Cast<ManagementObject>().ToList();
            var msftDisks = msftSearch.Get().Cast<ManagementObject>().ToList();

            foreach (var mDisk in msftDisks)
            {
                int number = Convert.ToInt32(mDisk["Number"]);
                var wDisk = win32Disks.FirstOrDefault(w => Convert.ToInt32(w["Index"]) == number);

                string friendlyName = wDisk?["Model"]?.ToString() ?? mDisk["FriendlyName"]?.ToString() ?? "Unknown";
                string pnpId = wDisk?["PNPDeviceID"]?.ToString() ?? "Unknown";

                int busEnum = Convert.ToInt32(mDisk["BusType"]);
                bool isNVMe = busEnum == 17;

                if (!isNVMe && (pnpId.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                                friendlyName.Contains("NVMe", StringComparison.OrdinalIgnoreCase)))
                    isNVMe = true;

                string busLabel = busEnum switch
                {
                    17 => "NVMe",
                    11 => "SATA",
                    7 => "USB",
                    8 => "RAID",
                    _ => isNVMe ? "NVMe" : "Other"
                };

                bool isBoot = mDisk["IsBoot"] is true || mDisk["IsSystem"] is true;
                long size = Convert.ToInt64(mDisk["Size"]);
                int sizeGB = (int)Math.Round(size / 1073741824.0);

                drives.Add(new SystemDrive
                {
                    Number = number,
                    Name = friendlyName,
                    Size = $"{sizeGB} GB",
                    IsNVMe = isNVMe,
                    BusType = busLabel,
                    IsBoot = isBoot,
                    PNPDeviceID = pnpId
                });
            }

            drives.Sort((a, b) => a.Number.CompareTo(b.Number));
        }
        catch { /* Drive scan best-effort */ }
        return drives;
    }

    public static NVMeDriverDetails GetNVMeDriverInfo()
    {
        var info = new NVMeDriverDetails();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT * FROM Win32_PnPSignedDriver");
            var allDrivers = search.Get().Cast<ManagementObject>().ToList();

            var nvmeDrivers = allDrivers.Where(d =>
                d["DeviceClass"]?.ToString() == "SCSIAdapter" ||
                (d["DeviceName"]?.ToString()?.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

            var thirdPartyPatterns = new (string Pattern, string Name)[]
            {
                ("Samsung", "Samsung NVMe"),
                ("WD.*NVMe|Western Digital", "Western Digital NVMe"),
                ("Intel.*RST|Rapid Storage", "Intel RST"),
                ("AMD.*NVMe|AMD RAID", "AMD NVMe/RAID"),
                ("Crucial", "Crucial NVMe"),
                ("SK.?hynix", "SK Hynix NVMe"),
                ("Phison", "Phison NVMe")
            };

            foreach (var driver in nvmeDrivers)
            {
                foreach (var (pattern, name) in thirdPartyPatterns)
                {
                    var devName = driver["DeviceName"]?.ToString() ?? "";
                    var manufacturer = driver["Manufacturer"]?.ToString() ?? "";
                    if (Regex.IsMatch(devName, pattern, RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(manufacturer, pattern, RegexOptions.IgnoreCase))
                    {
                        info.HasThirdParty = true;
                        info.ThirdPartyName = name;
                        info.CurrentDriver = $"{devName} v{driver["DriverVersion"]}";
                        break;
                    }
                }
                if (info.HasThirdParty) break;
            }

            var stornvme = allDrivers.FirstOrDefault(d => d["InfName"]?.ToString() == "stornvme.inf");
            if (stornvme is not null)
            {
                info.InboxVersion = stornvme["DriverVersion"]?.ToString() ?? "";
                if (!info.HasThirdParty)
                    info.CurrentDriver = $"Windows Inbox (stornvme) v{info.InboxVersion}";
            }

            // Queue depth
            try
            {
                using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device");
                if (key is not null)
                {
                    var qd = key.GetValue("IoQueueDepth");
                    if (qd is not null) info.QueueDepth = qd.ToString()!;
                }
            }
            catch { }

            // Firmware versions via PowerShell (Get-PhysicalDisk requires StorageWMI)
            try
            {
                using var physSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk WHERE BusType=17");
                foreach (ManagementObject pd in physSearch.Get())
                {
                    var fw = pd["FirmwareVersion"]?.ToString();
                    var id = pd["DeviceId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(fw))
                        info.FirmwareVersions[id] = fw;
                }
            }
            catch { }
        }
        catch
        {
            info.CurrentDriver = "Unable to detect";
        }
        return info;
    }

    public static Dictionary<string, NVMeHealthInfo> GetNVMeHealthData()
    {
        var health = new Dictionary<string, NVMeHealthInfo>();
        try
        {
            using var search = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT * FROM MSFT_PhysicalDisk WHERE BusType=17 OR MediaType=4");

            foreach (ManagementObject pd in search.Get())
            {
                string diskNum = pd["DeviceId"]?.ToString() ?? "";
                var info = new NVMeHealthInfo
                {
                    HealthStatus = pd["HealthStatus"]?.ToString() ?? "Unknown",
                    OperationalStatus = pd["OperationalStatus"]?.ToString() ?? "Unknown"
                };

                // StorageReliabilityCounter via CIM
                try
                {
                    var objectId = (pd["ObjectId"]?.ToString() ?? "").Replace("'", "''");
                    using var relSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                        $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId='{objectId}'}} WHERE AssocClass=MSFT_PhysicalDiskToStorageReliabilityCounter");
                    foreach (ManagementObject rel in relSearch.Get())
                    {
                        var temp = rel["Temperature"];
                        if (temp is not null) info.Temperature = $"{temp}C";

                        var wear = rel["Wear"];
                        if (wear is not null) info.Wear = $"{Math.Max(0, 100 - Convert.ToInt32(wear))}%";

                        var mediaErr = rel["MediaErrors"];
                        if (mediaErr is not null) info.MediaErrors = Convert.ToInt32(mediaErr);

                        var poh = rel["PowerOnHours"];
                        if (poh is not null) info.PowerOnHours = $"{poh}h";

                        var readErr = rel["ReadErrorsTotal"];
                        if (readErr is not null) info.ReadErrors = Convert.ToInt32(readErr);

                        var writeErr = rel["WriteErrorsTotal"];
                        if (writeErr is not null) info.WriteErrors = Convert.ToInt32(writeErr);
                    }
                }
                catch { }

                var tips = new List<string> { $"Health: {info.HealthStatus}" };
                if (info.Temperature != "N/A") tips.Add($"Temp: {info.Temperature}");
                if (info.Wear != "N/A") tips.Add($"Life remaining: {info.Wear}");
                if (info.PowerOnHours != "N/A") tips.Add($"Power-on: {info.PowerOnHours}");
                if (info.MediaErrors > 0) tips.Add($"Media errors: {info.MediaErrors}");
                if (info.ReadErrors > 0) tips.Add($"Read errors: {info.ReadErrors}");
                info.SmartTooltip = string.Join(" | ", tips);

                health[diskNum] = info;
            }
        }
        catch { }
        return health;
    }

    public static NativeNVMeStatus TestNativeNVMeActive()
    {
        var result = new NativeNVMeStatus();
        try
        {
            using var driverSearch = new ManagementObjectSearcher("SELECT * FROM Win32_SystemDriver WHERE Name='nvmedisk'");
            foreach (ManagementObject drv in driverSearch.Get())
            {
                if (drv["State"]?.ToString() == "Running")
                {
                    result.IsActive = true;
                    result.ActiveDriver = "nvmedisk.sys (Native NVMe)";
                    result.Details = "Native NVMe driver is running";
                }
            }

            using var pnpSearch = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_PnPEntity WHERE ClassGuid='{AppConfig.SafeBootGuid}'");
            var storageDiskDevices = pnpSearch.Get().Cast<ManagementObject>().ToList();
            if (storageDiskDevices.Count > 0)
            {
                result.IsActive = true;
                result.DeviceCategory = "Storage disks";
                result.StorageDisks = storageDiskDevices.Select(d => d["Name"]?.ToString() ?? "").ToList();
                if (string.IsNullOrEmpty(result.Details))
                    result.Details = "Drives found under Storage disks category";
            }
            else if (!result.IsActive)
            {
                result.DeviceCategory = "Disk drives (legacy)";
                result.ActiveDriver = "stornvme.sys / disk.sys (Legacy SCSI)";
                result.Details = "Legacy NVMe stack active (pre-patch or reboot required)";
            }

            using var signedSearch = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPSignedDriver WHERE DeviceName LIKE '%NVMe%' OR InfName='stornvme.inf' OR InfName='nvmedisk.inf'");
            foreach (ManagementObject drv in signedSearch.Get())
            {
                if (drv["InfName"]?.ToString() == "nvmedisk.inf")
                {
                    result.IsActive = true;
                    result.ActiveDriver = $"nvmedisk.sys v{drv["DriverVersion"]}";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            result.Details = $"Unable to determine driver status: {ex.Message}";
        }
        return result;
    }

    public static BypassIOResult GetBypassIOStatus()
    {
        var result = new BypassIOResult();
        try
        {
            var psi = new ProcessStartInfo("fsutil", $"bypassio state {Environment.GetEnvironmentVariable("SystemDrive")}\\")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return result;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            result.RawOutput = output.Trim();

            if (output.Contains("is currently supported")) result.Supported = true;
            else if (output.Contains("is not currently supported")) result.Supported = false;

            var storageMatch = Regex.Match(output, @"Storage Type:\s*(.+)");
            if (storageMatch.Success) result.StorageType = storageMatch.Groups[1].Value.Trim();

            var driverMatch = Regex.Match(output, @"Storage Driver:\s*(.+)");
            if (driverMatch.Success) result.DriverCompat = driverMatch.Groups[1].Value.Trim();

            var blockedMatch = Regex.Match(output, @"Driver Name:\s*(.+)");
            if (blockedMatch.Success) result.BlockedBy = blockedMatch.Groups[1].Value.Trim();
            else
            {
                var blockedAlt = Regex.Match(output, @"Driver:\s*(\S+\.sys)");
                if (blockedAlt.Success) result.BlockedBy = blockedAlt.Groups[1].Value.Trim();
            }

            if (!result.Supported && result.StorageType == "NVMe")
                result.Warning = "Native NVMe driver does not support BypassIO. DirectStorage games may have higher CPU usage.";
        }
        catch (Exception ex)
        {
            result.RawOutput = $"Unable to check BypassIO: {ex.Message}";
        }
        return result;
    }

    public static WindowsBuildDetails GetWindowsBuildDetails()
    {
        var details = new WindowsBuildDetails();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
            var os = search.Get().Cast<ManagementObject>().FirstOrDefault();
            if (os is not null)
            {
                int.TryParse(os["BuildNumber"]?.ToString(), out var buildNum);
                details.BuildNumber = buildNum;
                details.Caption = os["Caption"]?.ToString() ?? "";
            }

            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                details.DisplayVersion = key.GetValue("DisplayVersion")?.ToString() ?? "Unknown";
                var ubr = key.GetValue("UBR");
                if (ubr is int u) details.UBR = u;
            }

            details.Is24H2OrLater = details.BuildNumber >= 26100;
            details.IsRecommended = details.BuildNumber >= AppConfig.RecommendedBuild;
        }
        catch { }
        return details;
    }

    public static StorageMigrationResult GetStorageDiskMigration()
    {
        var result = new StorageMigrationResult();
        try
        {
            // Storage Disks class
            using var storageSearch = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE PNPClass='Storage Disks' AND Status='OK'");
            foreach (ManagementObject dev in storageSearch.Get())
                result.Migrated.Add(dev["Name"]?.ToString() ?? "");

            // Legacy NVMe under Disk drives
            using var legacySearch = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE PNPClass='DiskDrive' AND Status='OK'");
            foreach (ManagementObject dev in legacySearch.Get())
            {
                var instanceId = dev["DeviceID"]?.ToString() ?? "";
                if (instanceId.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                    result.Legacy.Add(dev["Name"]?.ToString() ?? "");
            }
        }
        catch { }
        return result;
    }

    public static bool TestBitLockerEnabled()
    {
        try
        {
            using var search = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftVolumeEncryption",
                "SELECT * FROM Win32_EncryptableVolume");
            foreach (ManagementObject vol in search.Get())
            {
                if (vol["DriveLetter"]?.ToString() == Environment.GetEnvironmentVariable("SystemDrive") &&
                    Convert.ToInt32(vol["ProtectionStatus"]) == 1)
                    return true;
            }
        }
        catch { }
        return false;
    }

    public static bool TestVeraCryptSystemEncryption()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\veracrypt");
            if (key is not null)
            {
                var start = key.GetValue("Start");
                if (start is int s && s == 0) return true; // Boot-start driver
            }

            var efiPath = Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "EFI", "VeraCrypt");
            if (Directory.Exists(efiPath)) return true;
        }
        catch { }
        return false;
    }

    public static bool TestLaptopChassis()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            int[] laptopTypes = [8, 9, 10, 14, 31, 32];
            foreach (ManagementObject chassis in search.Get())
            {
                if (chassis["ChassisTypes"] is ushort[] types)
                {
                    foreach (var ct in types)
                    {
                        if (laptopTypes.Contains(ct)) return true;
                    }
                }
            }

            using var battSearch = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
            if (battSearch.Get().Count > 0) return true;
        }
        catch { }
        return false;
    }

    public static List<IncompatibleSoftwareInfo> GetIncompatibleSoftware()
    {
        var found = new List<IncompatibleSoftwareInfo>();
        try
        {
            using var svcSearch = new ManagementObjectSearcher("SELECT Name FROM Win32_Service");
            var allServices = svcSearch.Get().Cast<ManagementObject>()
                .Select(s => s["Name"]?.ToString() ?? "").ToList();

            if (allServices.Any(s => Regex.IsMatch(s, "acronis|AcronisAgent", RegexOptions.IgnoreCase)))
                found.Add(new() { Name = "Acronis", Severity = "High", Message = "Backup cannot see drives under Storage disks category" });

            if (allServices.Any(s => Regex.IsMatch(s, "macrium|ReflectService", RegexOptions.IgnoreCase)))
                found.Add(new() { Name = "Macrium Reflect", Severity = "Medium", Message = "May need update for Storage disks compatibility" });

            if (allServices.Any(s => Regex.IsMatch(s, "VBox", RegexOptions.IgnoreCase)))
                found.Add(new() { Name = "VirtualBox", Severity = "Low", Message = "Storage filter drivers may conflict" });

            if (allServices.Any(s => Regex.IsMatch(s, "iaStorAC|iaStorE|iaLPSS", RegexOptions.IgnoreCase)))
                found.Add(new() { Name = "Intel RST", Severity = "High", Message = "Conflicts with nvmedisk.sys -- may cause BSOD on boot" });

            bool hasVmd = allServices.Any(s => Regex.IsMatch(s, @"^vmd$|vmd_bus", RegexOptions.IgnoreCase));
            if (hasVmd)
                found.Add(new() { Name = "Intel VMD", Severity = "High", Message = "Boot failures reported on Intel VMD systems" });

            if (allServices.Any(s => Regex.IsMatch(s, @"^vmms$|^LxssManager$", RegexOptions.IgnoreCase)))
                found.Add(new() { Name = "Hyper-V/WSL2", Severity = "Medium", Message = "WSL2 disk I/O ~40% slower with native NVMe (no paravirt)" });

            if (allServices.Any(s => Regex.IsMatch(s, "VeeamAgent|VeeamEndpoint", RegexOptions.IgnoreCase)))
                found.Add(new() { Name = "Veeam", Severity = "High", Message = "Backup agent cannot detect drives under Storage disks" });

            // Storage Spaces
            try
            {
                using var poolSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                    "SELECT FriendlyName, IsPrimordial FROM MSFT_StoragePool WHERE IsPrimordial=FALSE");
                if (poolSearch.Get().Count > 0)
                    found.Add(new() { Name = "Storage Spaces", Severity = "High", Message = "Storage pool detected -- arrays may degrade under nvmedisk.sys" });
            }
            catch { }

            // Vendor SSD tools
            var vendorPaths = new (string Name, string[] Paths)[]
            {
                ("Samsung Magician", [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Samsung", "Samsung Magician"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Samsung", "Samsung Magician")
                ]),
                ("WD Dashboard", [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Western Digital"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Western Digital")
                ]),
                ("Crucial Storage Executive", [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Crucial", "Crucial Storage Executive"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Crucial")
                ])
            };
            foreach (var (name, paths) in vendorPaths)
            {
                if (paths.Any(Directory.Exists))
                    found.Add(new() { Name = name, Severity = "Low", Message = "Will not detect drives under native NVMe (uses SCSI pass-through)" });
            }
        }
        catch { }
        return found;
    }
}
