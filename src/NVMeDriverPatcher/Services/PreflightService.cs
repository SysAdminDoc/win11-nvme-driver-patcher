using System.IO;
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
    public List<CodeIntegrityBlockedDriverEvent> CodeIntegrityBlockedDrivers { get; set; } = [];
    public List<DataFileProvenance> DataFileProvenance { get; set; } = [];
    public Dictionary<string, NVMeHealthInfo> CachedHealth { get; set; } = [];
    public StorageMigrationResult? CachedMigration { get; set; }
    public UpdateInfo? UpdateAvailable { get; set; }

    /// <summary>
    /// Background task resolving the GitHub update-check. Non-null when the check was kicked
    /// off during preflight; callers can `await` it (usually with a short timeout) to pull in
    /// a late-arriving result without having blocked the preflight render for it. See
    /// <see cref="PreflightService.RunAll"/>.
    /// </summary>
    public Task<UpdateInfo?>? UpdateCheckTask { get; set; }
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

        // Admin should already be enforced by the assembly manifest, but verify at runtime
        // anyway so a side-loaded launch (e.g. weird shell that strips elevation) still fails
        // loudly instead of silently doing nothing when registry writes are denied.
        checks["AdminPrivileges"] = IsRunningAsAdmin()
            ? new(CheckStatus.Pass, "Administrator", true)
            : new(CheckStatus.Fail, "Administrator privileges required", true);

        // 1. Windows Version
        log?.Invoke("  [1/11] Checking Windows version...");
        try
        {
            result.BuildDetails = DriveService.GetWindowsBuildDetails();
            var build = result.BuildDetails;
            if (build is null)
                checks["WindowsVersion"] = new(CheckStatus.Warning, "Unable to detect build", true);
            else if (build.BuildNumber < AppConfig.MinWinBuild)
                checks["WindowsVersion"] = new(CheckStatus.Fail, $"Build {build.BuildNumber} < {AppConfig.MinWinBuild}", true);
            else if (!build.Is24H2OrLater)
                checks["WindowsVersion"] = new(CheckStatus.Warning, $"Build {build.BuildNumber} ({build.DisplayVersion}) - 24H2+ recommended", true);
            else
                checks["WindowsVersion"] = new(CheckStatus.Pass, $"Win 11 {build.DisplayVersion} (Build {build.BuildNumber})", true);

            // Known driver-bind block: 26200.8524+ removed stornvme's GenNvmeDisk compatible
            // ID, so nvmedisk.inf can never match — registry AND ViVeTool routes both enable
            // flags that have no effect (thebookisclosed/ViVe issue #164). Community-reported,
            // stable-channel impact unconfirmed → Warning, not a blocker.
            if (build is not null && AppConfig.IsKnownBindBlockedBuild(build.BuildNumber, build.UBR))
                checks["NativeBindSupport"] = new(CheckStatus.Warning,
                    $"Build {build.BuildNumber}.{build.UBR}: nvmedisk may be unable to bind on this build — " +
                    "the patch (and the ViVeTool fallback) may have no effect");

            // Matched enablement rule (AR-2026-006): one updatable data file explains what
            // route is expected to work on this exact build instead of generic copy.
            try
            {
                result.DataFileProvenance = DataFileProvenanceService.InspectAll();
                var rule = WindowsBuildRulesService.MatchCurrent();
                checks["EnablementRule"] = rule is null
                    ? new(CheckStatus.Info, "No enablement rule matches this build — behavior unknown, proceed conservatively")
                    : new(rule.ExpectedPath == "none-known" ? CheckStatus.Warning : CheckStatus.Info,
                        WindowsBuildRulesService.Describe(rule));
                checks["DataFileProvenance"] = new(
                    result.DataFileProvenance.Any(f => f.IsStale || !f.Exists)
                        ? CheckStatus.Warning
                        : CheckStatus.Info,
                    DataFileProvenanceService.DescribeForPreflight(result.DataFileProvenance));
            }
            catch { /* rules are advisory — never block preflight */ }
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] Windows version check failed: {ex.Message}");
            checks["WindowsVersion"] = new(CheckStatus.Warning, "Check failed", true);
        }

        // SafeBoot upgrade state (RD-002): patches applied before v4.6.1 wrote only the
        // GUID-class SafeBoot entries; KB5079391 made 25H2 Safe Mode require the
        // service-name entries too. Only surfaces when an upgrade is actually needed.
        try
        {
            var safeBoot = SafeBootUpgradeService.Evaluate();
            if (safeBoot.UpgradeNeeded)
                checks["SafeBootEntries"] = new(CheckStatus.Warning,
                    "SafeBoot entries predate KB5079391 — run the SafeBoot upgrade (Safe Mode risk on 25H2+)");
        }
        catch { /* probe is best-effort */ }

        // 2. NVMe Drives
        log?.Invoke("  [2/11] Scanning drives...");
        try
        {
            result.CachedDrives = DriveService.GetSystemDrives() ?? [];
            int nvmeCount = result.CachedDrives.Count(d => d.IsNVMe);
            result.HasNVMeDrives = nvmeCount > 0;
            checks["NVMeDrives"] = nvmeCount > 0
                ? new(CheckStatus.Pass, $"{nvmeCount} NVMe drive(s)")
                : new(CheckStatus.Warning, "No NVMe drives");
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] Drive scan failed: {ex.Message}");
            checks["NVMeDrives"] = new(CheckStatus.Warning, "Scan failed");
        }

        // 3. BitLocker
        log?.Invoke("  [3/11] Checking BitLocker...");
        try
        {
            result.BitLockerEnabled = DriveService.TestBitLockerEnabled();
            checks["BitLocker"] = result.BitLockerEnabled
                ? new(CheckStatus.Warning, "Encryption active")
                : new(CheckStatus.Pass, "Not detected");
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] BitLocker check failed: {ex.Message}");
            checks["BitLocker"] = new(CheckStatus.Warning, "Unable to verify");
        }

        // 4. VeraCrypt
        log?.Invoke("  [4/11] Checking VeraCrypt...");
        try
        {
            result.VeraCryptDetected = DriveService.TestVeraCryptSystemEncryption();
            checks["VeraCrypt"] = result.VeraCryptDetected
                ? new(CheckStatus.Fail, "BLOCKS PATCH - breaks boot", true)
                : new(CheckStatus.Pass, "Not detected", true);
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] VeraCrypt check failed: {ex.Message}");
            checks["VeraCrypt"] = new(CheckStatus.Warning, "Unable to verify", true);
        }

        // 5. Laptop / Power
        log?.Invoke("  [5/11] Checking chassis type...");
        try
        {
            result.IsLaptop = DriveService.TestLaptopChassis();
            checks["LaptopPower"] = result.IsLaptop
                ? new(CheckStatus.Warning, "Laptop -- APST broken, ~15% battery impact")
                : new(CheckStatus.Pass, "Desktop");
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] Chassis check failed: {ex.Message}");
            checks["LaptopPower"] = new(CheckStatus.Warning, "Unable to verify");
        }

        // 6. Incompatible Software
        log?.Invoke("  [6/11] Checking software compatibility...");
        try
        {
            result.IncompatibleSoftware = DriveService.GetIncompatibleSoftware() ?? [];

            // Critical items (Intel RST, Intel VMD) are hard blockers — they cause BSOD/boot
            // failures and must surface as a blocking Fail, not a dismissible warning.
            var criticalSw = result.IncompatibleSoftware.Where(s => s.Severity == "Critical").ToList();
            var warnSw     = result.IncompatibleSoftware.Where(s => s.Severity != "Critical").ToList();

            if (criticalSw.Count > 0)
            {
                var names = criticalSw.Select(s => s.Name).ToList();
                string msg = names.Count <= 3
                    ? string.Join(", ", names)
                    : $"{string.Join(", ", names.Take(3))} +{names.Count - 3} more";
                checks["Compatibility"] = new(CheckStatus.Fail, $"BLOCKS PATCH: {msg}", critical: true);
            }
            else if (warnSw.Count > 0)
            {
                var names = warnSw.Select(s => s.Name).ToList();
                string msg = names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} +{names.Count - 3} more";
                checks["Compatibility"] = new(CheckStatus.Warning, msg);
            }
            else
                checks["Compatibility"] = new(CheckStatus.Pass, "No conflicts");
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] Compatibility check failed: {ex.Message}");
            checks["Compatibility"] = new(CheckStatus.Warning, "Check failed");
        }

        try
        {
            result.CodeIntegrityBlockedDrivers = CodeIntegrityEventService.RecentBackupDriverBlocks();
            if (result.CodeIntegrityBlockedDrivers.Count > 0)
            {
                checks["BackupDriverBlocklist"] = new(
                    CheckStatus.Warning,
                    CodeIntegrityEventService.DescribeForPreflight(result.CodeIntegrityBlockedDrivers));
            }
        }
        catch { /* event-log evidence is advisory */ }

        // 7. Third-party Driver
        log?.Invoke("  [7/11] Checking NVMe drivers...");
        try
        {
            result.DriverInfo = DriveService.GetNVMeDriverInfo();
            if (result.DriverInfo is null)
                checks["ThirdPartyDriver"] = new(CheckStatus.Warning, "Unable to detect");
            else
                checks["ThirdPartyDriver"] = result.DriverInfo.HasThirdParty
                    ? new(CheckStatus.Warning, result.DriverInfo.ThirdPartyName)
                    : new(CheckStatus.Pass, "Using inbox driver");
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] Driver check failed: {ex.Message}");
            checks["ThirdPartyDriver"] = new(CheckStatus.Warning, "Check failed");
        }

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

        // Pending reboot: applying registry changes while Windows already has a reboot queued
        // (Windows Update, a prior patch attempt, a driver install) can interact unpredictably —
        // KB5055621 (April 2026) independently triggers reboots that compound with the swap.
        // Warning, not a blocker. Only surfaces when actually pending.
        try
        {
            bool cbs = RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            bool wu = RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            var pending = ClassifyPendingReboot(cbs, wu);
            if (pending is not null) checks["PendingReboot"] = pending;
        }
        catch { /* advisory */ }

        // Working-directory free space: the patch writes are tiny, but recovery kits, benchmark
        // files, diagnostics exports, support bundles, and logs all land in the working dir — a
        // full disk makes those fail silently. Warning, only when actually low.
        try
        {
            var space = ClassifyWorkingDirSpace(GetWorkingDirFreeBytes(), MinWorkingDirFreeBytes);
            if (space is not null) checks["WorkingDirSpace"] = space;
        }
        catch { /* advisory */ }

        // 9. Driver Status
        log?.Invoke("  [9/11] Checking native NVMe driver...");
        try
        {
            result.NativeNVMeStatus = DriveService.TestNativeNVMeActive();
            if (result.NativeNVMeStatus is not null && result.NativeNVMeStatus.IsActive)
                checks["DriverStatus"] = new(CheckStatus.Pass, "nvmedisk.sys active");
            else
            {
                var patchStatus = RegistryService.GetPatchStatus();
                checks["DriverStatus"] = patchStatus.Applied
                    ? new(CheckStatus.Warning, "Patch set, reboot needed")
                    : new(CheckStatus.Info, "stornvme (legacy)");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] Driver status check failed: {ex.Message}");
            checks["DriverStatus"] = new(CheckStatus.Warning, "Check failed");
        }

        // 10. BypassIO
        log?.Invoke("  [10/11] Checking BypassIO / DirectStorage...");
        try
        {
            result.BypassIOStatus = DriveService.GetBypassIOStatus();
            if (result.BypassIOStatus is not null && result.BypassIOStatus.Supported)
                checks["BypassIO"] = new(CheckStatus.Pass, "Supported");
            else if (result.NativeNVMeStatus is not null && result.NativeNVMeStatus.IsActive)
                checks["BypassIO"] = new(CheckStatus.Warning, "Not supported (gaming impact)");
            else
            {
                string blockedMsg = result.BypassIOStatus is not null && !string.IsNullOrEmpty(result.BypassIOStatus.BlockedBy)
                    ? $"Blocked: {result.BypassIOStatus.BlockedBy}" : "Not available";
                checks["BypassIO"] = new(CheckStatus.Info, blockedMsg);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] BypassIO check failed: {ex.Message}");
            checks["BypassIO"] = new(CheckStatus.Warning, "Check failed");
        }

        log?.Invoke("  [11/11] Admin privileges verified");

        // Health and migration data — only meaningful when there are NVMe drives present.
        try { result.CachedHealth = DriveService.GetNVMeHealthData() ?? []; } catch { }
        try { result.CachedMigration = DriveService.GetStorageDiskMigration(); } catch { }

        // Update check — previously we waited up to 3 seconds for GitHub to reply so the badge
        // could render on the first preflight pass. On slow networks that's 3 seconds of
        // user-visible dead time before the rest of the UI wakes up. Kick it off here but
        // don't block: the viewmodel stashes the Task on `UpdateCheckTask`, and a small async
        // helper awaits it (with its own timeout) on a background continuation, firing a UI
        // refresh only if a result arrives. UpdateService.Check is already cached and short-
        // circuits repeat callers so kicking the task off multiple times is cheap.
        try
        {
            result.UpdateCheckTask = Task.Run<UpdateInfo?>(UpdateService.Check);
        }
        catch { }

        result.Checks = checks;
        return result;
    }

    public static bool AllCriticalPassed(Dictionary<string, PreflightCheck>? checks)
    {
        if (checks is null) return false;
        return checks.Values.All(c => !c.Critical || c.Status != CheckStatus.Fail);
    }

    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // Minimum free space we want on the working-dir drive before backups/bundles/logs risk failing.
    internal const long MinWorkingDirFreeBytes = 100L * 1024 * 1024; // 100 MB

    private static bool RegistryKeyExists(string subKey)
    {
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = hklm.OpenSubKey(subKey);
        return key is not null;
    }

    private static long? GetWorkingDirFreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(AppConfig.GetWorkingDir()));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    /// <summary>Pure: Warning when either standard pending-reboot signal is set, else null (no check).</summary>
    internal static PreflightCheck? ClassifyPendingReboot(bool cbsRebootPending, bool windowsUpdateRebootRequired)
    {
        if (!cbsRebootPending && !windowsUpdateRebootRequired) return null;
        var sources = new List<string>();
        if (cbsRebootPending) sources.Add("servicing");
        if (windowsUpdateRebootRequired) sources.Add("Windows Update");
        return new(CheckStatus.Warning,
            $"Reboot pending ({string.Join(" + ", sources)}) — restart Windows first, then retry the patch");
    }

    /// <summary>Pure: Warning when free space is known and below the floor, else null (unknown or healthy).</summary>
    internal static PreflightCheck? ClassifyWorkingDirSpace(long? availableBytes, long minBytes)
    {
        if (availableBytes is null || availableBytes.Value >= minBytes) return null;
        long mb = availableBytes.Value / (1024 * 1024);
        return new(CheckStatus.Warning,
            $"Low disk space on working-dir drive (~{mb} MB free) — recovery kit, bundles, and logs may fail to write");
    }

    internal static PreflightCheck ClassifyCompatibility(IReadOnlyCollection<IncompatibleSoftwareInfo> incompatibleSoftware)
    {
        var criticalSw = incompatibleSoftware.Where(s => s.Severity == "Critical").ToList();
        var warnSw = incompatibleSoftware.Where(s => s.Severity != "Critical").ToList();

        if (criticalSw.Count > 0)
        {
            var names = criticalSw.Select(s => s.Name).ToList();
            string msg = names.Count <= 3
                ? string.Join(", ", names)
                : $"{string.Join(", ", names.Take(3))} +{names.Count - 3} more";
            return new(CheckStatus.Fail, $"BLOCKS PATCH: {msg}", critical: true);
        }

        if (warnSw.Count > 0)
        {
            var names = warnSw.Select(s => s.Name).ToList();
            string msg = names.Count <= 3
                ? string.Join(", ", names)
                : $"{string.Join(", ", names.Take(3))} +{names.Count - 3} more";
            return new(CheckStatus.Warning, msg);
        }

        return new(CheckStatus.Pass, "No conflicts");
    }
}
