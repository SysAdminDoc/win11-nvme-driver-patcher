using System.Diagnostics;
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
    public BitLockerRecoveryProof? BitLockerRecovery { get; set; }
    public bool VeraCryptDetected { get; set; }
    public bool IsLaptop { get; set; }
    public List<IncompatibleSoftwareInfo> IncompatibleSoftware { get; set; } = [];
    public NVMeDriverDetails? DriverInfo { get; set; }
    public List<FirmwareCompatFinding> FirmwareCompatibility { get; set; } = [];
    public NativeNVMeStatus? NativeNVMeStatus { get; set; }
    public BypassIOResult? BypassIOStatus { get; set; }
    public List<CodeIntegrityBlockedDriverEvent> CodeIntegrityBlockedDrivers { get; set; } = [];
    public List<DataFileProvenance> DataFileProvenance { get; set; } = [];
    public bool? TestSigningEnabled { get; set; }
    public PerControllerAuditReport? ControllerAudit { get; set; }
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

            // 26300+ ships a native "Feature flags" page (Settings > Windows Update > Windows
            // Insider Program). Microsoft may expose an official NVMe toggle there — always
            // preferable to our overrides. Informational, never blocks.
            if (build is not null && AppConfig.HasNativeFeatureFlagsPage(build.BuildNumber))
                checks["FeatureFlagsPage"] = new(CheckStatus.Info,
                    "Windows 11 26300+ has a native 'Feature flags' page (Settings > Windows Update > " +
                    "Windows Insider Program) — check there for an official NVMe toggle before using overrides");

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

        // SafeBoot writability (issue #13): classify the two boot-critical GUID keys BEFORE any
        // feature write. On newer builds Windows ships these keys itself (a named "NvmeDisk" value)
        // and may deny writes; blindly proceeding risks a failed apply or, on removal, erasing
        // OS-owned state. Surface denied/conflicting/foreign keys honestly.
        try
        {
            var (min, net) = SafeBootStateService.ClassifyGuidKeys(new RealSafeBootRegistry());
            var worst = new[] { min, net }.Max();
            checks["SafeBootWritable"] = worst switch
            {
                SafeBootKeyDisposition.AccessDenied => new(CheckStatus.Fail,
                    "SafeBoot GUID key write access DENIED — Windows owns these keys on this build (issue #13). Apply would fail and could not add Safe Mode protection.", true),
                SafeBootKeyDisposition.ForeignValuesPresent => new(CheckStatus.Warning,
                    "SafeBoot GUID keys already carry OS-owned values (e.g. NvmeDisk). The patch will preserve them and remove only what it adds."),
                SafeBootKeyDisposition.ConflictingDefault => new(CheckStatus.Warning,
                    "SafeBoot GUID keys hold a different default value than expected — the patch will record and restore it on removal."),
                SafeBootKeyDisposition.AlreadyCorrect => new(CheckStatus.Pass, "SafeBoot GUID keys already set correctly"),
                _ => new(CheckStatus.Pass, "SafeBoot GUID keys are writable"),
            };
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
            result.BitLockerRecovery = BitLockerRecoveryService.InspectSystemVolume();
            result.BitLockerEnabled = result.BitLockerRecovery.Volume.IsEncrypted;
            checks["BitLocker"] = !result.BitLockerRecovery.ReadyForMutation
                ? new(CheckStatus.Fail, result.BitLockerRecovery.Detail, true)
                : result.BitLockerEnabled
                    ? new(CheckStatus.Warning, result.BitLockerRecovery.Detail, true)
                    : new(CheckStatus.Pass, result.BitLockerRecovery.Detail, true);

            // The swap changes the driver stack for ALL NVMe controllers, not just the OS volume.
            // A BitLocker-protected NON-system volume WITHOUT auto-unlock re-locks after the reboot.
            var dataVols = DriveService.DataVolumesNeedingAttention(DriveService.GetBitLockerVolumes());
            if (dataVols.Count > 0)
                checks["BitLockerDataDrives"] = new(CheckStatus.Warning,
                    $"BitLocker data volume(s) {string.Join(", ", dataVols.Select(v => v.DriveLetter))} have no auto-unlock — they will re-lock after reboot. The patch suspends them for one reboot; keep their recovery keys handy.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"    [ERROR] BitLocker check failed: {ex.Message}");
            checks["BitLocker"] = new(CheckStatus.Fail, "Unable to verify BitLocker recoverability", true);
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

            // Modern Standby laptops carry a distinct sleep-wake DATA risk (drives vanishing on
            // wake), separate from the battery-life warning above. Desktops and non-Modern-Standby
            // laptops see nothing new.
            if (result.IsLaptop)
            {
                var msWarn = ApstInspectorService.ModernStandbyApstWarning(true, ApstInspectorService.IsModernStandbyEnabled());
                if (msWarn is not null) checks["ModernStandbyApst"] = new(CheckStatus.Warning, msWarn);
            }
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

            result.FirmwareCompatibility = BuildFirmwareCompatibilityFindings(
                FirmwareCompatService.LoadDatabase(),
                result.CachedDrives,
                result.DriverInfo?.FirmwareVersions);
            var powerLossCheck = ClassifyFirmwarePowerLossRisk(result.FirmwareCompatibility);
            if (powerLossCheck is not null)
                checks["FirmwarePowerLossRisk"] = powerLossCheck;
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

        // Driver-method detection: nvmedisk.sys bound with none of this tool's breadcrumbs
        // (no override keys, no known fallback flags) is an "untracked" activation — official
        // rollout OR a forced Device Manager / PnPUtil install. Inform so the user isn't told
        // "not applied" and knows a forced install reverts via Device Manager, not registry.
        try
        {
            if (result.NativeNVMeStatus is not null && result.NativeNVMeStatus.IsActive)
            {
                int keyCount = RegistryService.GetPatchStatus().Count;
                bool fallbackEvidence;
                try { fallbackEvidence = FeatureStoreWriterService.HasFallbackEvidence(); }
                catch { fallbackEvidence = false; }
                if (PatchVerificationService.IsUntrackedDriverActivation(true, keyCount, fallbackEvidence))
                    checks["DriverActivation"] = new(CheckStatus.Info, PatchVerificationService.UntrackedDriverActivationNote);
            }
        }
        catch { /* informational only */ }

        try
        {
            result.TestSigningEnabled = DetectBcdTestSigningEnabled();
            result.ControllerAudit = PerControllerAuditService.Audit();
            var customWorkaround = ClassifyCustomNativeWorkaround(
                result.TestSigningEnabled, result.ControllerAudit);
            if (customWorkaround is not null)
                checks["CustomNativeWorkaround"] = customWorkaround;
        }
        catch { /* driver-store workaround evidence is advisory */ }

        // 10. BypassIO
        log?.Invoke("  [10/11] Checking BypassIO / DirectStorage...");
        try
        {
            result.BypassIOStatus = DriveService.GetBypassIOStatus();
            if (result.BypassIOStatus is not null && result.BypassIOStatus.Supported)
                checks["BypassIO"] = new(CheckStatus.Pass, "Supported");
            else if (result.NativeNVMeStatus is not null && result.NativeNVMeStatus.IsActive)
            {
                var bypassWarning = result.BypassIOStatus is null
                    ? string.Empty
                    : !string.IsNullOrWhiteSpace(result.BypassIOStatus.Warning)
                        ? result.BypassIOStatus.Warning
                        : result.BypassIOStatus.GamingImpact;
                checks["BypassIO"] = new(CheckStatus.Warning,
                    string.IsNullOrWhiteSpace(bypassWarning)
                        ? "BypassIO not supported; DirectStorage games may fall back to legacy I/O."
                        : bypassWarning);
            }
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

    internal static List<FirmwareCompatFinding> BuildFirmwareCompatibilityFindings(
        FirmwareCompatDatabase db,
        IEnumerable<SystemDrive> drives,
        IReadOnlyDictionary<string, string>? firmwareVersions)
    {
        var findings = new List<FirmwareCompatFinding>();
        foreach (var drive in drives.Where(d => d.IsNVMe))
        {
            var key = drive.Number.ToString();
            var firmware = firmwareVersions is not null && firmwareVersions.TryGetValue(key, out var fw)
                ? fw
                : string.Empty;
            var finding = FirmwareCompatService.Lookup(db, drive.Name, firmware);
            if (finding.Level != FirmwareCompatLevel.Unknown || finding.PowerLossRisk)
                findings.Add(finding);
        }
        return findings;
    }

    internal static PreflightCheck? ClassifyFirmwarePowerLossRisk(IReadOnlyCollection<FirmwareCompatFinding> findings)
    {
        var risky = findings.Where(f => f.PowerLossRisk).ToList();
        if (risky.Count == 0) return null;

        var names = risky.Select(f => string.IsNullOrWhiteSpace(f.DriveModel) ? "matched NVMe drive" : f.DriveModel).ToList();
        var summary = names.Count <= 2 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(2))} +{names.Count - 2} more";
        return new(
            CheckStatus.Warning,
            $"Power-loss advisory: {summary} matches a Phison E18/E26 power-loss risk entry. Use UPS/power protection and current backups before enabling nvmedisk.sys.");
    }

    internal static PreflightCheck? ClassifyCustomNativeWorkaround(
        bool? testSigningEnabled,
        PerControllerAuditReport? controllerAudit)
    {
        List<ControllerAudit> customControllers = controllerAudit is null
            ? []
            : PerControllerAuditService.FindCustomNativeWorkaroundEvidence(controllerAudit.Controllers);

        if (testSigningEnabled != true && customControllers.Count == 0)
            return null;

        var signals = new List<string>();
        if (testSigningEnabled == true)
            signals.Add("BCD TESTSIGNING is ON");
        if (customControllers.Count > 0)
        {
            var names = customControllers
                .Select(c => string.IsNullOrWhiteSpace(c.FriendlyName) ? c.InstanceId : c.FriendlyName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            signals.Add(names.Count == 0
                ? $"{customControllers.Count} custom native NVMe driver binding(s)"
                : $"custom native NVMe binding(s): {string.Join(", ", names.Take(2))}" +
                  (names.Count > 2 ? $" +{names.Count - 2} more" : string.Empty));
        }

        return new(
            CheckStatus.Warning,
            "Custom/test-signed native NVMe workaround detected (" + string.Join("; ", signals) + "). " +
            "This app will not automate or remove this driver-store route. Capture evidence with " +
            "`pnputil /enum-drivers /files`; revert through Device Manager or " +
            "`pnputil /delete-driver <oem#.inf> /uninstall` only after confirming the OEM INF.");
    }

    internal static bool? ParseBcdTestSigning(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("testsigning", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line["testsigning".Length..].Trim();
            if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.OrdinalIgnoreCase))
                return true;

            if (value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("0", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return null;
    }

    internal static bool? DetectBcdTestSigningEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("bcdedit.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/enum");
            psi.ArgumentList.Add("{current}");

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            return ParseBcdTestSigning(stdout + Environment.NewLine + stderr);
        }
        catch
        {
            return null;
        }
    }
}
