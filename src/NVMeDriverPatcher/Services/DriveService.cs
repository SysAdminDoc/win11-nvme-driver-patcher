using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class DriveService
{
    // Each ManagementObject from a ManagementObjectCollection holds an unmanaged COM
    // pointer that is NOT released by the GC until finalization. We must Dispose
    // every object we touch — and the collection itself — to avoid a slow native leak
    // when these calls run on a recurring poll.
    private static IEnumerable<ManagementObject> Enumerate(ManagementObjectSearcher searcher)
    {
        using var collection = searcher.Get();
        foreach (var obj in collection)
        {
            if (obj is ManagementObject mo)
            {
                try { yield return mo; }
                finally { mo.Dispose(); }
            }
        }
    }

    private static int? AsInt(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToInt32(value); }
        catch { return null; }
    }

    private static long? AsLong(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToInt64(value); }
        catch { return null; }
    }

    public static List<SystemDrive> GetSystemDrives()
    {
        var drives = new List<SystemDrive>();
        try
        {
            using var msftSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Disk");
            using var win32Search = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");

            // Snapshot Win32 disks (need to disposable-wrap them too) — keyed by Index for fast lookup.
            var win32ByIndex = new Dictionary<int, (string Model, string Pnp)>();
            try
            {
                foreach (var wDisk in Enumerate(win32Search))
                {
                    int? idx = AsInt(wDisk["Index"]);
                    if (idx is null) continue;
                    win32ByIndex[idx.Value] = (
                        wDisk["Model"]?.ToString() ?? "",
                        wDisk["PNPDeviceID"]?.ToString() ?? "");
                }
            }
            catch { /* Win32 enumeration is supplementary, not fatal */ }

            foreach (var mDisk in Enumerate(msftSearch))
            {
                // Each drive in its own try so a single bad WMI row never wipes the rest.
                try
                {
                    int? numberMaybe = AsInt(mDisk["Number"]);
                    if (numberMaybe is null) continue;
                    int number = numberMaybe.Value;

                    win32ByIndex.TryGetValue(number, out var w);
                    string friendlyName = !string.IsNullOrEmpty(w.Model)
                        ? w.Model
                        : mDisk["FriendlyName"]?.ToString() ?? "Unknown";
                    string pnpId = !string.IsNullOrEmpty(w.Pnp) ? w.Pnp : "Unknown";

                    int busEnum = AsInt(mDisk["BusType"]) ?? 0;
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
                    long size = AsLong(mDisk["Size"]) ?? 0;
                    int sizeGB = (int)Math.Round(size / 1073741824.0);

                    drives.Add(new SystemDrive
                    {
                        Number = number,
                        Name = friendlyName,
                        Size = sizeGB > 0 ? $"{sizeGB} GB" : "Unknown",
                        IsNVMe = isNVMe,
                        BusType = busLabel,
                        IsBoot = isBoot,
                        PNPDeviceID = pnpId
                    });
                }
                catch { /* one bad drive shouldn't poison the inventory */ }
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

            // Stream drivers once: capture stornvme + scan for known 3rd-party in the same pass.
            string inboxVersion = "";
            using (var collection = search.Get())
            {
                foreach (var raw in collection)
                {
                    if (raw is not ManagementObject driver) continue;
                    using (driver)
                    {
                        try
                        {
                            var infName = driver["InfName"]?.ToString();
                            var devName = driver["DeviceName"]?.ToString() ?? "";
                            var deviceClass = driver["DeviceClass"]?.ToString() ?? "";
                            var manufacturer = driver["Manufacturer"]?.ToString() ?? "";
                            var driverVersion = driver["DriverVersion"]?.ToString() ?? "";

                            if (infName == "stornvme.inf" && string.IsNullOrEmpty(inboxVersion))
                                inboxVersion = driverVersion;

                            if (info.HasThirdParty) continue;
                            bool isNvmeCandidate = deviceClass == "SCSIAdapter"
                                || devName.Contains("NVMe", StringComparison.OrdinalIgnoreCase);
                            if (!isNvmeCandidate) continue;

                            foreach (var (pattern, name) in thirdPartyPatterns)
                            {
                                if (Regex.IsMatch(devName, pattern, RegexOptions.IgnoreCase) ||
                                    Regex.IsMatch(manufacturer, pattern, RegexOptions.IgnoreCase))
                                {
                                    info.HasThirdParty = true;
                                    info.ThirdPartyName = name;
                                    info.CurrentDriver = $"{devName} v{driverVersion}";
                                    break;
                                }
                            }
                        }
                        catch { /* Skip malformed driver row */ }
                    }
                }
            }

            info.InboxVersion = inboxVersion;
            if (!info.HasThirdParty && !string.IsNullOrEmpty(inboxVersion))
                info.CurrentDriver = $"Windows Inbox (stornvme) v{inboxVersion}";

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

            // Firmware versions
            try
            {
                using var physSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, FirmwareVersion FROM MSFT_PhysicalDisk WHERE BusType=17");
                foreach (var pd in Enumerate(physSearch))
                {
                    var fw = pd["FirmwareVersion"]?.ToString();
                    var id = pd["DeviceId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(fw) && !string.IsNullOrEmpty(id))
                        info.FirmwareVersions[id] = fw;
                }
            }
            catch { }
        }
        catch
        {
            if (string.IsNullOrEmpty(info.CurrentDriver))
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

            foreach (var pd in Enumerate(search))
            {
                string diskNum = pd["DeviceId"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(diskNum)) continue;

                var info = new NVMeHealthInfo
                {
                    HealthStatus = pd["HealthStatus"]?.ToString() ?? "Unknown",
                    OperationalStatus = pd["OperationalStatus"]?.ToString() ?? "Unknown"
                };

                // StorageReliabilityCounter via CIM
                try
                {
                    var objectId = (pd["ObjectId"]?.ToString() ?? "").Replace("'", "''");
                    if (objectId.Length > 0)
                    {
                        using var relSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                            $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId='{objectId}'}} WHERE AssocClass=MSFT_PhysicalDiskToStorageReliabilityCounter");
                        foreach (var rel in Enumerate(relSearch))
                        {
                            int? temp = AsInt(rel["Temperature"]);
                            if (temp is not null) info.Temperature = $"{temp.Value}C";

                            int? wear = AsInt(rel["Wear"]);
                            if (wear is not null) info.Wear = $"{Math.Max(0, 100 - wear.Value)}%";

                            int? mediaErr = AsInt(rel["MediaErrors"]);
                            if (mediaErr is not null) info.MediaErrors = mediaErr.Value;

                            long? poh = AsLong(rel["PowerOnHours"]);
                            if (poh is not null) info.PowerOnHours = $"{poh.Value}h";

                            int? readErr = AsInt(rel["ReadErrorsTotal"]);
                            if (readErr is not null) info.ReadErrors = readErr.Value;

                            int? writeErr = AsInt(rel["WriteErrorsTotal"]);
                            if (writeErr is not null) info.WriteErrors = writeErr.Value;
                        }
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
            using var driverSearch = new ManagementObjectSearcher("SELECT Name, State FROM Win32_SystemDriver WHERE Name='nvmedisk'");
            foreach (var drv in Enumerate(driverSearch))
            {
                if (drv["State"]?.ToString() == "Running")
                {
                    result.IsActive = true;
                    result.ActiveDriver = "nvmedisk.sys (Native NVMe)";
                    result.Details = "Native NVMe driver is running";
                }
            }

            using var pnpSearch = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_PnPEntity WHERE ClassGuid='{AppConfig.SafeBootGuid}'");
            var storageDiskNames = new List<string>();
            foreach (var dev in Enumerate(pnpSearch))
            {
                var name = dev["Name"]?.ToString();
                if (!string.IsNullOrEmpty(name)) storageDiskNames.Add(name);
            }

            if (storageDiskNames.Count > 0)
            {
                result.IsActive = true;
                result.DeviceCategory = "Storage disks";
                result.StorageDisks = storageDiskNames;
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
                "SELECT InfName, DriverVersion FROM Win32_PnPSignedDriver WHERE InfName='stornvme.inf' OR InfName='nvmedisk.inf'");
            foreach (var drv in Enumerate(signedSearch))
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
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
            if (string.IsNullOrEmpty(systemDrive)) systemDrive = "C:";
            var psi = new ProcessStartInfo("fsutil", $"bypassio state {systemDrive}\\")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return result;
            // Read stdout asynchronously while we wait — guards against deadlock if the buffer fills.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(10000))
            {
                try { proc.Kill(true); } catch { }
                result.RawOutput = "fsutil bypassio query timed out after 10s";
                return result;
            }
            var output = outputTask.GetAwaiter().GetResult();
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
            using var search = new ManagementObjectSearcher("SELECT BuildNumber, Caption FROM Win32_OperatingSystem");
            foreach (var os in Enumerate(search))
            {
                int.TryParse(os["BuildNumber"]?.ToString(), out var buildNum);
                details.BuildNumber = buildNum;
                details.Caption = os["Caption"]?.ToString() ?? "";
                break; // single OS row
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
            using var storageSearch = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE PNPClass='Storage Disks' AND Status='OK'");
            foreach (var dev in Enumerate(storageSearch))
            {
                var name = dev["Name"]?.ToString();
                if (!string.IsNullOrEmpty(name)) result.Migrated.Add(name);
            }

            using var legacySearch = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass='DiskDrive' AND Status='OK'");
            foreach (var dev in Enumerate(legacySearch))
            {
                var instanceId = dev["DeviceID"]?.ToString() ?? "";
                if (instanceId.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                {
                    var name = dev["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name)) result.Legacy.Add(name);
                }
            }
        }
        catch { }
        return result;
    }

    public static bool TestBitLockerEnabled()
    {
        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            using var search = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
            foreach (var vol in Enumerate(search))
            {
                if (vol["DriveLetter"]?.ToString() == systemDrive &&
                    AsInt(vol["ProtectionStatus"]) == 1)
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

            // Path.Combine treats "C:" as a drive-relative spec — must append a separator
            // or we end up testing "C:EFI\VeraCrypt", which silently never exists.
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            if (!systemDrive.EndsWith(System.IO.Path.DirectorySeparatorChar) &&
                !systemDrive.EndsWith(System.IO.Path.AltDirectorySeparatorChar))
            {
                systemDrive += System.IO.Path.DirectorySeparatorChar;
            }
            var efiPath = Path.Combine(systemDrive, "EFI", "VeraCrypt");
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
            foreach (var chassis in Enumerate(search))
            {
                if (chassis["ChassisTypes"] is ushort[] types)
                {
                    foreach (var ct in types)
                    {
                        if (laptopTypes.Contains(ct)) return true;
                    }
                }
            }

            using var battSearch = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            using var batteryCollection = battSearch.Get();
            if (batteryCollection.Count > 0)
            {
                foreach (var b in batteryCollection) (b as IDisposable)?.Dispose();
                return true;
            }
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
            var allServices = new List<string>();
            foreach (var s in Enumerate(svcSearch))
            {
                var name = s["Name"]?.ToString();
                if (!string.IsNullOrEmpty(name)) allServices.Add(name);
            }

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
                using var pools = poolSearch.Get();
                bool any = pools.Count > 0;
                foreach (var p in pools) (p as IDisposable)?.Dispose();
                if (any)
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
