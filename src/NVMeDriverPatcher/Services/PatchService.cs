using System.Diagnostics;
using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class PatchOperationResult
{
    public bool Success { get; set; }
    public int AppliedCount { get; set; }
    public int TotalExpected { get; set; }
    public PatchSnapshot? BeforeSnapshot { get; set; }
    public PatchSnapshot? AfterSnapshot { get; set; }
    public bool NeedsRestart { get; set; }
    public bool WasRolledBack { get; set; }
}

public static class PatchService
{
    public static PatchOperationResult Install(
        AppConfig config,
        bool bitLockerEnabled,
        bool veraCryptDetected,
        NativeNVMeStatus? nativeStatus,
        BypassIOResult? bypassStatus,
        Action<string>? log = null,
        Action<int, string>? progress = null)
    {
        var result = new PatchOperationResult();
        result.BeforeSnapshot = RegistryService.GetPatchSnapshot(nativeStatus, bypassStatus);
        try { DataService.SaveSnapshot(result.BeforeSnapshot, "Before patch install", isPrePatch: true); } catch { }

        log?.Invoke("========================================");
        log?.Invoke("STARTING PATCH INSTALLATION");
        log?.Invoke("========================================");
        EventLogService.Write("NVMe Driver Patch installation started");

        var appliedKeys = new List<(string Type, string ID)>();
        int successCount = 0;

        var featureIDsToApply = new List<string>(AppConfig.FeatureIDs);
        int effectiveTotal = AppConfig.TotalComponents;
        if (config.IncludeServerKey)
        {
            featureIDsToApply.Add(AppConfig.ServerFeatureID);
            effectiveTotal++;
            log?.Invoke("Including optional Microsoft Server 2025 key (1176759950)");
        }
        result.TotalExpected = effectiveTotal;

        try
        {
            // Step 0: Suspend BitLocker — MUST succeed before touching drivers
            if (bitLockerEnabled)
            {
                log?.Invoke("Suspending BitLocker for one reboot cycle...");
                try
                {
                    var sysDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
                    var psi = new ProcessStartInfo("manage-bde", $"-protectors -disable {sysDrive} -RebootCount 1")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    bool exited = proc?.WaitForExit(30000) ?? false;
                    if (!exited || proc?.ExitCode != 0)
                    {
                        log?.Invoke("[ERROR] BitLocker suspension FAILED - aborting to prevent boot failure");
                        EventLogService.Write("Patch aborted: BitLocker suspension failed", EventLogEntryType.Error, 3002);
                        return result;
                    }
                    log?.Invoke("[SUCCESS] BitLocker suspended - will auto-resume after reboot");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[ERROR] BitLocker suspension FAILED: {ex.Message} - aborting");
                    EventLogService.Write($"Patch aborted: BitLocker exception: {ex.Message}", EventLogEntryType.Error, 3002);
                    return result;
                }
            }

            // Step 1: Backup
            log?.Invoke("Step 1/3: Creating system backup...");
            progress?.Invoke(10, "Creating registry backup...");
            RegistryService.ExportRegistryBackup(config.WorkingDir, "Pre_Patch");
            progress?.Invoke(30, "Creating restore point...");
            CreateRestorePoint("Pre-NVMe-Driver-Patch", log);

            // Step 2: Apply registry components
            log?.Invoke($"Step 2/3: Applying {effectiveTotal} registry components...");
            progress?.Invoke(60, "Applying registry changes...");

            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            // Ensure path exists
            using var overrides = hklm.CreateSubKey(AppConfig.RegistrySubKey);
            if (overrides is null)
            {
                log?.Invoke("[ERROR] Failed to create registry path");
                return result;
            }

            foreach (var id in featureIDsToApply)
            {
                string friendlyName = AppConfig.FeatureNames.TryGetValue(id, out var fn) ? fn : "Feature Flag";
                try
                {
                    overrides.SetValue(id, 1, RegistryValueKind.DWord);
                    var verify = overrides.GetValue(id);
                    if (verify is int v && v == 1)
                    {
                        log?.Invoke($"  [OK] {id} - {friendlyName}");
                        successCount++;
                        appliedKeys.Add(("Feature", id));
                    }
                    else
                    {
                        log?.Invoke($"  [FAIL] {id} - {friendlyName}");
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  [FAIL] {id} - {ex.Message}");
                }
            }

            // SafeBoot Minimal
            try
            {
                using var safeMin = hklm.CreateSubKey(AppConfig.SafeBootMinimalPath);
                safeMin?.SetValue("", AppConfig.SafeBootValue);
                var val = safeMin?.GetValue("") as string;
                if (val == AppConfig.SafeBootValue)
                {
                    log?.Invoke("  [OK] SafeBoot Minimal Support");
                    successCount++;
                    appliedKeys.Add(("SafeBoot", "Minimal"));
                }
                else log?.Invoke("  [FAIL] SafeBoot Minimal Support");
            }
            catch (Exception ex) { log?.Invoke($"  [FAIL] SafeBoot Minimal: {ex.Message}"); }

            // SafeBoot Network
            try
            {
                using var safeNet = hklm.CreateSubKey(AppConfig.SafeBootNetworkPath);
                safeNet?.SetValue("", AppConfig.SafeBootValue);
                var val = safeNet?.GetValue("") as string;
                if (val == AppConfig.SafeBootValue)
                {
                    log?.Invoke("  [OK] SafeBoot Network Support");
                    successCount++;
                    appliedKeys.Add(("SafeBoot", "Network"));
                }
                else log?.Invoke("  [FAIL] SafeBoot Network Support");
            }
            catch (Exception ex) { log?.Invoke($"  [FAIL] SafeBoot Network: {ex.Message}"); }

            // Step 3: Validate
            progress?.Invoke(95, "Validating...");
            log?.Invoke("Step 3/3: Validating installation...");
            log?.Invoke("========================================");

            result.AppliedCount = successCount;

            if (successCount == effectiveTotal)
            {
                result.Success = true;
                result.NeedsRestart = true;
                log?.Invoke($"[SUCCESS] Patch Status: SUCCESS - Applied {successCount}/{effectiveTotal} components");
                log?.Invoke("[WARNING] Please RESTART your computer to apply changes");
                EventLogService.Write($"NVMe Driver Patch applied successfully ({successCount}/{effectiveTotal} components)");
            }
            else
            {
                log?.Invoke($"[WARNING] Patch Status: PARTIAL - Applied {successCount}/{effectiveTotal} components");
                log?.Invoke("[WARNING] Rolling back partial installation...");
                progress?.Invoke(96, "Rolling back...");

                Rollback(hklm, appliedKeys, log);
                result.WasRolledBack = true;

                log?.Invoke("[WARNING] Rollback complete - system returned to pre-patch state");
                EventLogService.Write($"NVMe Driver Patch rolled back after partial failure ({successCount}/{effectiveTotal})",
                    EventLogEntryType.Warning, 2001);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] INSTALLATION FAILED: {ex.Message}");
            EventLogService.Write($"NVMe Driver Patch installation failed: {ex.Message}", EventLogEntryType.Error, 3001);
        }

        progress?.Invoke(0, "");
        result.AfterSnapshot = RegistryService.GetPatchSnapshot(nativeStatus, bypassStatus);
        try
        {
            var description = result.Success
                ? "After patch install"
                : result.WasRolledBack
                    ? "After failed install rollback"
                    : "After patch install attempt";
            DataService.SaveSnapshot(result.AfterSnapshot, description, isPrePatch: false);
        }
        catch { }
        return result;
    }

    public static PatchOperationResult Uninstall(
        AppConfig config,
        NativeNVMeStatus? nativeStatus,
        BypassIOResult? bypassStatus,
        Action<string>? log = null,
        Action<int, string>? progress = null)
    {
        var result = new PatchOperationResult();
        result.BeforeSnapshot = RegistryService.GetPatchSnapshot(nativeStatus, bypassStatus);
        try { DataService.SaveSnapshot(result.BeforeSnapshot, "Before patch removal", isPrePatch: true); } catch { }

        log?.Invoke("========================================");
        log?.Invoke("STARTING PATCH REMOVAL");
        log?.Invoke("========================================");
        EventLogService.Write("NVMe Driver Patch removal started");

        progress?.Invoke(10, "Creating backup...");
        RegistryService.ExportRegistryBackup(config.WorkingDir, "Pre_Removal");
        int removedCount = 0;

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            log?.Invoke("Removing registry components...");
            progress?.Invoke(30, "Removing feature flags...");

            using var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey, writable: true);
            if (overrides is not null)
            {
                var allIds = AppConfig.FeatureIDs.Append(AppConfig.ServerFeatureID);
                foreach (var id in allIds)
                {
                    string friendlyName = AppConfig.FeatureNames.TryGetValue(id, out var fn) ? fn : "Feature Flag";
                    try
                    {
                        if (overrides.GetValue(id) is not null)
                        {
                            overrides.DeleteValue(id);
                            log?.Invoke($"  [REMOVED] {id} - {friendlyName}");
                            removedCount++;
                        }
                        else
                        {
                            log?.Invoke($"  [ABSENT] {id} (Already gone)");
                        }
                    }
                    catch (Exception ex) { log?.Invoke($"  [FAIL] {id}: {ex.Message}"); }
                }
            }

            progress?.Invoke(60, "Removing SafeBoot keys...");

            // SafeBoot Minimal
            try
            {
                using var safeMinParent = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal", writable: true);
                if (safeMinParent is not null)
                {
                    try
                    {
                        safeMinParent.DeleteSubKeyTree(AppConfig.SafeBootGuid, false);
                        log?.Invoke("  [REMOVED] SafeBoot Minimal");
                        removedCount++;
                    }
                    catch { log?.Invoke("  [ABSENT] SafeBoot Minimal"); }
                }
            }
            catch (Exception ex) { log?.Invoke($"  [FAIL] SafeBoot Minimal: {ex.Message}"); }

            // SafeBoot Network
            try
            {
                using var safeNetParent = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\Network", writable: true);
                if (safeNetParent is not null)
                {
                    try
                    {
                        safeNetParent.DeleteSubKeyTree(AppConfig.SafeBootGuid, false);
                        log?.Invoke("  [REMOVED] SafeBoot Network");
                        removedCount++;
                    }
                    catch { log?.Invoke("  [ABSENT] SafeBoot Network"); }
                }
            }
            catch (Exception ex) { log?.Invoke($"  [FAIL] SafeBoot Network: {ex.Message}"); }

            progress?.Invoke(90, "Validating...");
            result.AppliedCount = removedCount;
            result.Success = true;
            result.NeedsRestart = true;

            log?.Invoke("========================================");
            log?.Invoke($"[SUCCESS] Patch Status: REMOVED - Removed {removedCount} components");
            log?.Invoke("[INFO] After reboot: Drives will return to 'Disk drives' using stornvme.sys");
            EventLogService.Write($"NVMe Driver Patch removed ({removedCount} components)");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] REMOVAL FAILED: {ex.Message}");
            EventLogService.Write($"NVMe Driver Patch removal failed: {ex.Message}", EventLogEntryType.Error, 3001);
        }

        progress?.Invoke(0, "");
        result.AfterSnapshot = RegistryService.GetPatchSnapshot(nativeStatus, bypassStatus);
        try { DataService.SaveSnapshot(result.AfterSnapshot, "After patch removal", isPrePatch: false); } catch { }
        return result;
    }

    private static void Rollback(RegistryKey hklm, List<(string Type, string ID)> appliedKeys, Action<string>? log)
    {
        foreach (var (type, id) in appliedKeys)
        {
            try
            {
                if (type == "Feature")
                {
                    using var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey, writable: true);
                    overrides?.DeleteValue(id, false);
                    string friendlyName = AppConfig.FeatureNames.TryGetValue(id, out var fn) ? fn : "Feature Flag";
                    log?.Invoke($"  [ROLLBACK] {id} - {friendlyName}");
                }
                else if (type == "SafeBoot")
                {
                    string parentPath = id == "Minimal"
                        ? @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal"
                        : @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network";
                    using var parent = hklm.OpenSubKey(parentPath, writable: true);
                    parent?.DeleteSubKeyTree(AppConfig.SafeBootGuid, false);
                    log?.Invoke($"  [ROLLBACK] SafeBoot {id}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [ROLLBACK FAIL] {type} {id}: {ex.Message}");
            }
        }
    }

    private static void CreateRestorePoint(string description, Action<string>? log)
    {
        try
        {
            var safeDesc = description.Replace("'", "''");
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"Checkpoint-Computer -Description '{safeDesc}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(60000);
            if (proc?.ExitCode == 0)
                log?.Invoke("[SUCCESS] System restore point created");
            else
                log?.Invoke("[WARNING] Restore point may have failed (24h limit or disabled)");
        }
        catch
        {
            log?.Invoke("[WARNING] Could not create restore point");
        }
    }

    public static void InitiateRestart(int delaySeconds)
    {
        delaySeconds = Math.Clamp(delaySeconds, 0, 3600);
        var psi = new ProcessStartInfo("shutdown.exe",
            $"/r /t {delaySeconds} /c \"NVMe Driver Patch - Restarting in {delaySeconds} seconds. Save your work!\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
    }
}
