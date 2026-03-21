using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class PreflightResult
{
    public Dictionary<string, PreflightCheck> Checks { get; set; } = [];
    public WindowsBuildDetails? BuildDetails { get; set; }
    public List<SystemDrive> CachedDrives { get; set; } = [];
    public bool HasNVMeDrives { get; set; }
    public bool BitLockerEnabled { get; set; }
    public bool VeraCryptDetected { get; set; }
    public bool IsLaptop { get; set; }
    public List<IncompatibleSoftwareInfo> IncompatibleSoftware { get; set; } = [];
    public NVMeDriverDetails? DriverInfo { get; set; }
    public NativeNVMeStatus? NativeNVMeStatus { get; set; }
    public BypassIOResult? BypassIOStatus { get; set; }
    public Dictionary<string, NVMeHealthInfo> CachedHealth { get; set; } = [];
    public StorageMigrationResult? CachedMigration { get; set; }
    public UpdateInfo? UpdateAvailable { get; set; }
}

public static class PreflightService
{
    public static async Task<PreflightResult> RunAllAsync(Action<string>? log = null)
    {
        return await Task.Run(() => RunAll(log));
    }

    public static PreflightResult RunAll(Action<string>? log = null)
    {
        var result = new PreflightResult();
        var checks = new Dictionary<string, PreflightCheck>();

        // Admin is guaranteed by manifest
        checks["AdminPrivileges"] = new(CheckStatus.Pass, "Administrator", true);

        // 1. Windows Version
        log?.Invoke("  [1/11] Checking Windows version...");
        result.BuildDetails = DriveService.GetWindowsBuildDetails();
        var build = result.BuildDetails;
        if (build.BuildNumber < AppConfig.MinWinBuild)
            checks["WindowsVersion"] = new(CheckStatus.Fail, $"Build {build.BuildNumber} < {AppConfig.MinWinBuild}", true);
        else if (!build.Is24H2OrLater)
            checks["WindowsVersion"] = new(CheckStatus.Warning, $"Build {build.BuildNumber} ({build.DisplayVersion}) - 24H2+ recommended", true);
        else
            checks["WindowsVersion"] = new(CheckStatus.Pass, $"Win 11 {build.DisplayVersion} (Build {build.BuildNumber})", true);

        // 2. NVMe Drives
        log?.Invoke("  [2/11] Scanning drives...");
        result.CachedDrives = DriveService.GetSystemDrives();
        int nvmeCount = result.CachedDrives.Count(d => d.IsNVMe);
        result.HasNVMeDrives = nvmeCount > 0;
        checks["NVMeDrives"] = nvmeCount > 0
            ? new(CheckStatus.Pass, $"{nvmeCount} NVMe drive(s)")
            : new(CheckStatus.Warning, "No NVMe drives");

        // 3. BitLocker
        log?.Invoke("  [3/11] Checking BitLocker...");
        result.BitLockerEnabled = DriveService.TestBitLockerEnabled();
        checks["BitLocker"] = result.BitLockerEnabled
            ? new(CheckStatus.Warning, "Encryption active")
            : new(CheckStatus.Pass, "Not detected");

        // 4. VeraCrypt
        log?.Invoke("  [4/11] Checking VeraCrypt...");
        result.VeraCryptDetected = DriveService.TestVeraCryptSystemEncryption();
        checks["VeraCrypt"] = result.VeraCryptDetected
            ? new(CheckStatus.Fail, "BLOCKS PATCH - breaks boot", true)
            : new(CheckStatus.Pass, "Not detected", true);

        // 5. Laptop / Power
        log?.Invoke("  [5/11] Checking chassis type...");
        result.IsLaptop = DriveService.TestLaptopChassis();
        checks["LaptopPower"] = result.IsLaptop
            ? new(CheckStatus.Warning, "Laptop -- APST broken, ~15% battery impact")
            : new(CheckStatus.Pass, "Desktop");

        // 6. Incompatible Software
        log?.Invoke("  [6/11] Checking software compatibility...");
        result.IncompatibleSoftware = DriveService.GetIncompatibleSoftware();
        var warnSw = result.IncompatibleSoftware.Where(s => s.Severity != "Critical").ToList();
        if (warnSw.Count > 0)
        {
            var names = warnSw.Select(s => s.Name).ToList();
            string msg = names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} +{names.Count - 3} more";
            checks["Compatibility"] = new(CheckStatus.Warning, msg);
        }
        else
            checks["Compatibility"] = new(CheckStatus.Pass, "No conflicts");

        // 7. Third-party Driver
        log?.Invoke("  [7/11] Checking NVMe drivers...");
        result.DriverInfo = DriveService.GetNVMeDriverInfo();
        checks["ThirdPartyDriver"] = result.DriverInfo.HasThirdParty
            ? new(CheckStatus.Warning, result.DriverInfo.ThirdPartyName)
            : new(CheckStatus.Pass, "Using inbox driver");

        // 8. System Protection
        log?.Invoke("  [8/11] Checking System Protection...");
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SystemRestore");
            if (key?.GetValue("DisableSR") is int dis && dis == 1)
                checks["SystemProtection"] = new(CheckStatus.Warning, "Disabled globally");
            else
                checks["SystemProtection"] = new(CheckStatus.Pass, "Enabled");
        }
        catch
        {
            checks["SystemProtection"] = new(CheckStatus.Warning, "Unable to verify");
        }

        // 9. Driver Status
        log?.Invoke("  [9/11] Checking native NVMe driver...");
        result.NativeNVMeStatus = DriveService.TestNativeNVMeActive();
        if (result.NativeNVMeStatus.IsActive)
            checks["DriverStatus"] = new(CheckStatus.Pass, "nvmedisk.sys active");
        else
        {
            var patchStatus = RegistryService.GetPatchStatus();
            checks["DriverStatus"] = patchStatus.Applied
                ? new(CheckStatus.Warning, "Patch set, reboot needed")
                : new(CheckStatus.Info, "stornvme (legacy)");
        }

        // 10. BypassIO
        log?.Invoke("  [10/11] Checking BypassIO / DirectStorage...");
        result.BypassIOStatus = DriveService.GetBypassIOStatus();
        if (result.BypassIOStatus.Supported)
            checks["BypassIO"] = new(CheckStatus.Pass, "Supported");
        else if (result.NativeNVMeStatus.IsActive)
            checks["BypassIO"] = new(CheckStatus.Warning, "Not supported (gaming impact)");
        else
        {
            string blockedMsg = !string.IsNullOrEmpty(result.BypassIOStatus.BlockedBy)
                ? $"Blocked: {result.BypassIOStatus.BlockedBy}" : "Not available";
            checks["BypassIO"] = new(CheckStatus.Info, blockedMsg);
        }

        log?.Invoke("  [11/11] Admin privileges verified");

        // Health and migration data
        result.CachedHealth = DriveService.GetNVMeHealthData();
        result.CachedMigration = DriveService.GetStorageDiskMigration();

        // Update check
        try { result.UpdateAvailable = UpdateService.Check(); } catch { }

        result.Checks = checks;
        return result;
    }

    public static bool AllCriticalPassed(Dictionary<string, PreflightCheck> checks)
    {
        return checks.Values.All(c => !c.Critical || c.Status != CheckStatus.Fail);
    }
}
