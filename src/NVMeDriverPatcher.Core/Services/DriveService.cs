using System.Diagnostics;
using System.Globalization;
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
        using var collection = WmiQueryHelper.ExecuteWithTimeout(searcher);
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

    // Pre-compiled third-party NVMe driver patterns. Compiling once avoids re-interpreting these
    // seven patterns for every signed PnP driver row (potentially hundreds) on each preflight run.
    private static readonly (Regex Pattern, string Name)[] ThirdPartyDriverPatterns =
    [
        (new(@"Samsung",                  RegexOptions.IgnoreCase | RegexOptions.Compiled), "Samsung NVMe"),
        (new(@"WD.*NVMe|Western Digital", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Western Digital NVMe"),
        (new(@"Intel.*RST|Rapid Storage", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Intel RST"),
        (new(@"AMD.*NVMe|AMD RAID",        RegexOptions.IgnoreCase | RegexOptions.Compiled), "AMD NVMe/RAID"),
        (new(@"Crucial",                  RegexOptions.IgnoreCase | RegexOptions.Compiled), "Crucial NVMe"),
        (new(@"SK.?hynix",                RegexOptions.IgnoreCase | RegexOptions.Compiled), "SK Hynix NVMe"),
        (new(@"Phison",                   RegexOptions.IgnoreCase | RegexOptions.Compiled), "Phison NVMe"),
    ];

    // Pre-compiled patterns for incompatible-software detection. Compiling once avoids
    // re-interpreting the same pattern string for every service name on each preflight run.
    private static readonly Regex RxAcronis    = new(@"acronis|AcronisAgent",          RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxMacrium    = new(@"macrium|ReflectService",         RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxVirtualBox = new(@"VBox",                           RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxIntelRst   = new(@"iaStorAC|iaStorE|iaLPSS",        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxIntelVmd   = new(@"^vmd$|vmd_bus",                  RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxHyperV     = new(@"^vmms$|^LxssManager$",           RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxVeeam      = new(@"VeeamAgent|VeeamEndpoint",       RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxUrBackup   = new(@"UrBackup|UrBackupClientBackend", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxNinjaOne   = new(@"NinjaOne|NinjaRMM|NinjaRMMAgent|NinjaAgent", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxParagon    = new(@"Paragon|UimFIO|Uim_IM|psmounter", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxCrystalDiskInfo = new(@"^(CrystalDiskInfo|DiskInfo|DiskInfo32|DiskInfo64|DiskInfoA64)(\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pre-compiled patterns for fsutil bypassio output parsing.
    private static readonly Regex RxStorageType   = new(@"Storage Type:\s*(.+)",   RegexOptions.Compiled);
    private static readonly Regex RxStorageDriver = new(@"Storage Driver:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex RxDriverName    = new(@"Driver Name:\s*(.+)",    RegexOptions.Compiled);
    private static readonly Regex RxDriverSys     = new(@"Driver:\s*(\S+\.sys)",   RegexOptions.Compiled);

    public static readonly IReadOnlyList<string> DirectStorageGameExamples =
    [
        "Ratchet & Clank: Rift Apart",
        "Forspoken",
        "Forza Motorsport",
        "Horizon Forbidden West"
    ];

    public static string DirectStorageGameExamplesText => string.Join(", ", DirectStorageGameExamples);

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

            string inboxVersion = "";
            using (var collection = WmiQueryHelper.ExecuteWithTimeout(search))
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

                            foreach (var (pattern, name) in ThirdPartyDriverPatterns)
                            {
                                if (pattern.IsMatch(devName) || pattern.IsMatch(manufacturer))
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

            // Firmware versions keyed by OS disk number so the UI can match them reliably.
            try
            {
                using var diskSearch = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    "SELECT Number, FirmwareVersion FROM MSFT_Disk WHERE BusType=17");
                foreach (var disk in Enumerate(diskSearch))
                {
                    var diskNumber = AsInt(disk["Number"]);
                    var fw = disk["FirmwareVersion"]?.ToString();
                    if (diskNumber is not null && !string.IsNullOrWhiteSpace(fw))
                        info.FirmwareVersions[diskNumber.Value.ToString(CultureInfo.InvariantCulture)] = fw;
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
            using var search = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT Number, ObjectId, HealthStatus, OperationalStatus FROM MSFT_Disk WHERE BusType=17");

            foreach (var disk in Enumerate(search))
            {
                int? diskNumber = AsInt(disk["Number"]);
                if (diskNumber is null) continue;

                var diskKey = diskNumber.Value.ToString(CultureInfo.InvariantCulture);
                var info = new NVMeHealthInfo
                {
                    HealthStatus = DescribeHealthStatus(disk["HealthStatus"]),
                    OperationalStatus = DescribeOperationalStatus(disk["OperationalStatus"])
                };

                try
                {
                    var objectId = EscapeWqlLiteral(disk["ObjectId"]?.ToString());
                    if (objectId.Length > 0)
                    {
                        using var relSearch = new ManagementObjectSearcher(
                            @"root\Microsoft\Windows\Storage",
                            $"ASSOCIATORS OF {{MSFT_Disk.ObjectId='{objectId}'}} WHERE AssocClass=MSFT_DiskToStorageReliabilityCounter");
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

                            break;
                        }
                    }
                }
                catch { }

                var tips = new List<string> { $"Health: {info.HealthStatus}" };
                if (!string.IsNullOrWhiteSpace(info.OperationalStatus) && info.OperationalStatus != "Unknown")
                    tips.Add($"Status: {info.OperationalStatus}");
                if (info.Temperature != "N/A") tips.Add($"Temp: {info.Temperature}");
                if (info.Wear != "N/A") tips.Add($"Life remaining: {info.Wear}");
                if (info.PowerOnHours != "N/A") tips.Add($"Power-on: {info.PowerOnHours}");
                if (info.MediaErrors > 0) tips.Add($"Media errors: {info.MediaErrors}");
                if (info.ReadErrors > 0) tips.Add($"Read errors: {info.ReadErrors}");
                info.SmartTooltip = string.Join(" | ", tips);

                health[diskKey] = info;
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
            var systemDrive = NormalizeDriveRoot(Environment.GetEnvironmentVariable("SystemDrive")) ?? "C:\\";
            var psi = new ProcessStartInfo("fsutil")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("bypassio");
            psi.ArgumentList.Add("state");
            psi.ArgumentList.Add(systemDrive);

            using var proc = Process.Start(psi);
            if (proc is null) return result;
            // Read both pipes asynchronously so stderr can't block process exit on failure.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(10000))
            {
                try { proc.Kill(true); } catch { }
                result.RawOutput = "fsutil bypassio query timed out after 10s";
                return result;
            }
            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            result.RawOutput = string.IsNullOrWhiteSpace(error)
                ? output.Trim()
                : $"{output}{Environment.NewLine}{error}".Trim();

            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(result.RawOutput))
            {
                result.RawOutput = $"fsutil bypassio exited with code {proc.ExitCode}";
                return result;
            }

            if (output.Contains("is currently supported")) result.Supported = true;
            else if (output.Contains("is not currently supported")) result.Supported = false;

            var storageMatch = RxStorageType.Match(output);
            if (storageMatch.Success) result.StorageType = storageMatch.Groups[1].Value.Trim();

            var driverMatch = RxStorageDriver.Match(output);
            if (driverMatch.Success) result.DriverCompat = driverMatch.Groups[1].Value.Trim();

            var blockedMatch = RxDriverName.Match(output);
            if (blockedMatch.Success) result.BlockedBy = blockedMatch.Groups[1].Value.Trim();
            else
            {
                var blockedAlt = RxDriverSys.Match(output);
                if (blockedAlt.Success) result.BlockedBy = blockedAlt.Groups[1].Value.Trim();
            }

            result.GamingImpact = BuildBypassIoGamingImpact(result);
            if (!result.Supported && IsNvmeStorage(result.StorageType))
                result.Warning = result.GamingImpact;
        }
        catch (Exception ex)
        {
            result.RawOutput = $"Unable to check BypassIO: {ex.Message}";
            result.GamingImpact = BuildBypassIoGamingImpact(result);
        }
        return result;
    }

    internal static string BuildBypassIoGamingImpact(BypassIOResult result)
    {
        var blocker = string.IsNullOrWhiteSpace(result.BlockedBy)
            ? "the current storage stack"
            : result.BlockedBy.Trim();

        if (result.Supported)
        {
            return $"DirectStorage impact: BypassIO is currently available. If this drive is patched to nvmedisk.sys, DirectStorage titles such as {DirectStorageGameExamplesText} can fall back to legacy I/O with higher CPU use or stutter. Keep game-library drives on stornvme.sys with per-drive scope when gaming performance matters.";
        }

        if (IsNvmeStorage(result.StorageType))
        {
            var eac = blocker.Contains("EOSSys", StringComparison.OrdinalIgnoreCase)
                ? " EasyAntiCheat's EOSSys.sys is the current BypassIO veto, so address that driver separately from the storage-driver choice."
                : " EasyAntiCheat's EOSSys.sys can also veto BypassIO independently on systems using that anti-cheat driver.";
            return $"DirectStorage impact: BypassIO is blocked by {blocker}. DirectStorage titles such as {DirectStorageGameExamplesText} can fall back to legacy I/O with higher CPU use or stutter. Keep game-library drives on stornvme.sys with per-drive scope when gaming performance matters.{eac}";
        }

        return "DirectStorage impact: no NVMe BypassIO regression detected on the queried system drive.";
    }

    private static bool IsNvmeStorage(string storageType) =>
        string.Equals(storageType, "NVMe", StringComparison.OrdinalIgnoreCase);

    internal static string? NormalizeDriveRoot(string? drive)
    {
        if (string.IsNullOrWhiteSpace(drive))
            return null;

        drive = drive.Trim();
        if (drive.Length >= 2 && char.IsAsciiLetter(drive[0]) && drive[1] == ':')
            return $"{char.ToUpperInvariant(drive[0])}:\\";

        return null;
    }

    private static string EscapeWqlLiteral(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("'", "''");

    internal static string DescribeHealthStatus(object? value)
    {
        return AsInt(value) switch
        {
            0 => "Healthy",
            1 => "Warning",
            2 => "Unhealthy",
            5 => "Unknown",
            null => "Unknown",
            var code => $"Code {code}"
        };
    }

    internal static string DescribeOperationalStatus(object? value)
    {
        var codes = ExtractStatusCodes(value);
        if (codes.Count == 0)
            return "Unknown";

        var labels = codes
            .Select(code => code switch
            {
                0 => "Unknown",
                1 => "Other",
                2 => "OK",
                3 => "Degraded",
                4 => "Stressed",
                5 => "Predictive Failure",
                6 => "Error",
                7 => "Non-Recoverable Error",
                8 => "Starting",
                9 => "Stopping",
                10 => "Stopped",
                11 => "In Service",
                12 => "No Contact",
                13 => "Lost Communication",
                14 => "Aborted",
                15 => "Dormant",
                16 => "Supporting Entity In Error",
                17 => "Completed",
                18 => "Power Mode",
                19 => "Relocating",
                0xD000 => "Read-only",
                0xD001 => "Incomplete",
                _ => $"Code {code}"
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return string.Join(", ", labels);
    }

    private static List<int> ExtractStatusCodes(object? value)
    {
        if (value is null)
            return [];

        if (value is Array array)
        {
            return array
                .Cast<object?>()
                .Select(AsInt)
                .Where(code => code is not null)
                .Select(code => code!.Value)
                .ToList();
        }

        var single = AsInt(value);
        return single is null ? [] : [single.Value];
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

    /// <summary>A BitLocker-encryptable fixed volume and the facts that decide whether the NVMe
    /// driver swap will leave it locked after the reboot.</summary>
    public sealed record BitLockerVolume(string DriveLetter, bool IsSystemDrive, bool ProtectionOn, bool AutoUnlockEnabled);

    /// <summary>
    /// Pure classifier: protected NON-system volumes WITHOUT auto-unlock re-lock after the post-patch
    /// reboot (their key isn't released automatically), so the user must be warned and their
    /// protectors suspended. The system volume is handled separately (its failure aborts the patch).
    /// </summary>
    public static IReadOnlyList<BitLockerVolume> DataVolumesNeedingAttention(IEnumerable<BitLockerVolume> volumes) =>
        volumes.Where(v => !v.IsSystemDrive && v.ProtectionOn && !v.AutoUnlockEnabled).ToList();

    /// <summary>Enumerate encryptable fixed volumes with protection + auto-unlock state. Auto-unlock
    /// defaults to false (the conservative "needs attention") when it can't be determined.</summary>
    public static List<BitLockerVolume> GetBitLockerVolumes()
    {
        var list = new List<BitLockerVolume>();
        try
        {
            var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
            using var search = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus, VolumeType FROM Win32_EncryptableVolume");
            foreach (var vol in Enumerate(search))
            {
                var letter = vol["DriveLetter"]?.ToString();
                if (string.IsNullOrWhiteSpace(letter)) continue;
                letter = letter.TrimEnd('\\');
                bool isSystem = string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase);
                bool protectionOn = AsInt(vol["ProtectionStatus"]) == 1;

                bool autoUnlock = false;
                if (!isSystem && protectionOn)
                {
                    // Win32_EncryptableVolume.IsAutoUnlockEnabled(out bool, out string). If we can't
                    // determine it, leave autoUnlock=false so the volume is flagged (fail-safe).
                    try
                    {
                        var outParams = vol.InvokeMethod("IsAutoUnlockEnabled", null, null);
                        if (outParams?["IsAutoUnlockEnabled"] is bool b) autoUnlock = b;
                    }
                    catch { }
                }
                list.Add(new BitLockerVolume(letter, isSystem, protectionOn, autoUnlock));
            }
        }
        catch { }
        return list;
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

    // SMBIOS chassis types that indicate a laptop/portable: Portable(8), Laptop(9), Notebook(10),
    // Sub-Notebook(14), Convertible(31), Detachable(32).
    private static readonly int[] LaptopChassisTypes = [8, 9, 10, 14, 31, 32];

    // WMI boxes ChassisTypes differently across SKUs/VMs/OEM images (ushort[], int[], uint[],
    // object[]). Match on Array and Convert each element — the same robustness ExtractStatusCodes
    // already uses — rather than a single `is ushort[]` cast that silently misses other boxings.
    internal static bool IsLaptopChassis(object? chassisTypes)
    {
        if (chassisTypes is not Array array) return false;
        foreach (var raw in array)
        {
            var ct = AsInt(raw);
            if (ct is not null && LaptopChassisTypes.Contains(ct.Value)) return true;
        }
        return false;
    }

    public static bool TestLaptopChassis()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (var chassis in Enumerate(search))
            {
                if (IsLaptopChassis(chassis["ChassisTypes"])) return true;
            }

            using var battSearch = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            using var batteryCollection = WmiQueryHelper.ExecuteWithTimeout(battSearch);
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

            var allProcesses = new List<string>();
            try
            {
                using var processSearch = new ManagementObjectSearcher("SELECT Name, ExecutablePath FROM Win32_Process");
                foreach (var p in Enumerate(processSearch))
                {
                    var name = p["Name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) allProcesses.Add(name);
                    var path = p["ExecutablePath"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(path)) allProcesses.Add(path);
                }
            }
            catch { }

            if (allServices.Any(s => RxAcronis.IsMatch(s)))
                found.Add(new() { Name = "Acronis", Severity = "High", Message = "Backup cannot see drives under Storage disks category" });

            if (allServices.Any(s => RxMacrium.IsMatch(s)))
                found.Add(new() { Name = "Macrium Reflect", Severity = "Medium", Message = "May need update for Storage disks compatibility" });

            if (allServices.Any(s => RxVirtualBox.IsMatch(s)))
                found.Add(new() { Name = "VirtualBox", Severity = "Low", Message = "Storage filter drivers may conflict" });

            // Intel RST and VMD are hard blockers — community reports confirm BSOD on boot
            // and boot failures. Severity = Critical so PreflightService surfaces these as
            // a blocking Fail rather than a warning that can be clicked past.
            if (allServices.Any(s => RxIntelRst.IsMatch(s)))
                found.Add(new() { Name = "Intel RST", Severity = "Critical", Message = "Conflicts with nvmedisk.sys -- BSOD on boot reported. Remove Intel RST before patching." });

            if (allServices.Any(s => RxIntelVmd.IsMatch(s)))
                found.Add(new() { Name = "Intel VMD", Severity = "Critical", Message = "Boot failures reported on Intel VMD systems. Do not patch while VMD is active." });

            if (allServices.Any(s => RxHyperV.IsMatch(s)))
                found.Add(new() { Name = "Hyper-V/WSL2", Severity = "Medium", Message = "WSL2 disk I/O ~40% slower with native NVMe (no paravirt)" });

            if (allServices.Any(s => RxVeeam.IsMatch(s)))
                found.Add(new() { Name = "Veeam", Severity = "High", Message = "Backup agent cannot detect drives under Storage disks" });

            if (allServices.Any(s => RxUrBackup.IsMatch(s)))
                found.Add(new() { Name = "UrBackup", Severity = "Medium", Message = "Check backup image-mount support after KB5083769 driver blocklist changes" });

            if (allServices.Any(s => RxNinjaOne.IsMatch(s)))
                found.Add(new() { Name = "NinjaOne", Severity = "Medium", Message = "Check backup/image-mount support after KB5083769 driver blocklist changes" });

            if (allServices.Any(s => RxParagon.IsMatch(s)))
                found.Add(new() { Name = "Paragon", Severity = "Medium", Message = "Backup image-mount driver may be blocked by Windows vulnerable-driver rules" });

            // KB5083769 (April 2026) expanded the vulnerable-driver blocklist and broke
            // image-mount (psmounterex.sys) in Macrium/Acronis/Veeam/UrBackup INDEPENDENTLY
            // of this patch. Users with these products often misattribute that breakage to
            // the NVMe swap — say so up front to cut support noise.
            if (found.Any(f => f.Name is "Acronis" or "Macrium Reflect" or "Veeam" or "UrBackup" or "NinjaOne" or "Paragon"))
                found.Add(new()
                {
                    Name = "Backup software note",
                    Severity = "Info",
                    Message = "Unrelated to this patch: the April 2026 Windows update (KB5083769 driver " +
                              "blocklist) broke image-mount in several backup products. If backups fail " +
                              "after that update, check your backup vendor's advisory before suspecting " +
                              "the NVMe driver swap."
                });

            // Data Deduplication is an optional Windows feature. Microsoft documents storage
            // stack incompatibilities here, and the legacy PowerShell path already warns on it.
            try
            {
                using var dedupFeatureSearch = new ManagementObjectSearcher(
                    "SELECT Name, InstallState FROM Win32_OptionalFeature WHERE Name='Dedup-Core'");
                foreach (var feature in Enumerate(dedupFeatureSearch))
                {
                    if (AsInt(feature["InstallState"]) == 1)
                    {
                        found.Add(new()
                        {
                            Name = "Data Deduplication",
                            Severity = "High",
                            Message = "Microsoft documents Data Deduplication as incompatible with the native NVMe path"
                        });
                    }
                    break;
                }
            }
            catch { }

            // Storage Spaces
            try
            {
                using var poolSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                    "SELECT FriendlyName, IsPrimordial FROM MSFT_StoragePool WHERE IsPrimordial=FALSE");
                using var pools = WmiQueryHelper.ExecuteWithTimeout(poolSearch);
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

            var crystalPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CrystalDiskInfo"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "CrystalDiskInfo"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Crystal Dew World", "CrystalDiskInfo"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Crystal Dew World", "CrystalDiskInfo")
            };
            if (DetectCrystalDiskInfo(allServices.Concat(allProcesses).Concat(crystalPaths.Where(Directory.Exists)))
                && found.All(f => f.Name != "CrystalDiskInfo"))
            {
                found.Add(CrystalDiskInfoFinding());
            }
        }
        catch { }
        return found;
    }

    internal static List<IncompatibleSoftwareInfo> DetectServiceIncompatibilities(IEnumerable<string> serviceNames)
    {
        var allServices = serviceNames.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var found = new List<IncompatibleSoftwareInfo>();

        if (allServices.Any(s => RxAcronis.IsMatch(s)))
            found.Add(new() { Name = "Acronis", Severity = "High", Message = "Backup cannot see drives under Storage disks category" });
        if (allServices.Any(s => RxMacrium.IsMatch(s)))
            found.Add(new() { Name = "Macrium Reflect", Severity = "Medium", Message = "May need update for Storage disks compatibility" });
        if (allServices.Any(s => RxVirtualBox.IsMatch(s)))
            found.Add(new() { Name = "VirtualBox", Severity = "Low", Message = "Storage filter drivers may conflict" });
        if (allServices.Any(s => RxIntelRst.IsMatch(s)))
            found.Add(new() { Name = "Intel RST", Severity = "Critical", Message = "Conflicts with nvmedisk.sys -- BSOD on boot reported. Remove Intel RST before patching." });
        if (allServices.Any(s => RxIntelVmd.IsMatch(s)))
            found.Add(new() { Name = "Intel VMD", Severity = "Critical", Message = "Boot failures reported on Intel VMD systems. Do not patch while VMD is active." });
        if (allServices.Any(s => RxHyperV.IsMatch(s)))
            found.Add(new() { Name = "Hyper-V/WSL2", Severity = "Medium", Message = "WSL2 disk I/O ~40% slower with native NVMe (no paravirt)" });
        if (allServices.Any(s => RxVeeam.IsMatch(s)))
            found.Add(new() { Name = "Veeam", Severity = "High", Message = "Backup agent cannot detect drives under Storage disks" });
        if (allServices.Any(s => RxUrBackup.IsMatch(s)))
            found.Add(new() { Name = "UrBackup", Severity = "Medium", Message = "Check backup image-mount support after KB5083769 driver blocklist changes" });
        if (allServices.Any(s => RxNinjaOne.IsMatch(s)))
            found.Add(new() { Name = "NinjaOne", Severity = "Medium", Message = "Check backup/image-mount support after KB5083769 driver blocklist changes" });
        if (allServices.Any(s => RxParagon.IsMatch(s)))
            found.Add(new() { Name = "Paragon", Severity = "Medium", Message = "Backup image-mount driver may be blocked by Windows vulnerable-driver rules" });

        if (found.Any(f => f.Name is "Acronis" or "Macrium Reflect" or "Veeam" or "UrBackup" or "NinjaOne" or "Paragon"))
            found.Add(new()
            {
                Name = "Backup software note",
                Severity = "Info",
                Message = "Unrelated to this patch: the April 2026 Windows update (KB5083769 driver " +
                          "blocklist) broke image-mount in several backup products. If backups fail " +
                          "after that update, check your backup vendor's advisory before suspecting " +
                          "the NVMe driver swap."
            });

        if (DetectCrystalDiskInfo(allServices))
            found.Add(CrystalDiskInfoFinding());

        return found;
    }

    internal static bool DetectCrystalDiskInfo(IEnumerable<string> processServiceOrPathNames) =>
        processServiceOrPathNames.Any(IsCrystalDiskInfoName);

    internal static bool IsCrystalDiskInfoName(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var value = candidate.Trim();

        string leaf;
        try
        {
            leaf = Path.GetFileName(value);
        }
        catch
        {
            leaf = value;
        }

        if (RxCrystalDiskInfo.IsMatch(leaf)) return true;

        var normalized = value.Replace('/', '\\');
        return normalized.Contains("\\CrystalDiskInfo\\", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("\\CrystalDiskInfo", StringComparison.OrdinalIgnoreCase);
    }

    private static IncompatibleSoftwareInfo CrystalDiskInfoFinding() => new()
    {
        Name = "CrystalDiskInfo",
        Severity = "Medium",
        Message = "SMART monitoring uses SCSI pass-through and may stop reading NVMe health under nvmedisk.sys; use Get-StorageReliabilityCounter instead."
    };
}
