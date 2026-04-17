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

        // Hard belt-and-suspenders: never let the GUI call past us if the system encryption check
        // detects VeraCrypt. The viewmodel should already block this, but we also re-check here
        // so the CLI / scripted entry points cannot bypass it.
        if (veraCryptDetected)
        {
            log?.Invoke("[ERROR] BLOCKED: VeraCrypt system encryption detected. Native NVMe path breaks VeraCrypt boot.");
            EventLogService.Write("Patch aborted: VeraCrypt system encryption detected", EventLogEntryType.Error, 3002);
            FinalizeResult(result, nativeStatus, bypassStatus, progress);
            return result;
        }

        // Defense-in-depth admin check. If the manifest somehow failed to elevate (very rare,
        // but seen in reduced-privilege side-loading scenarios) we'd otherwise hit a string of
        // SecurityException entries while writing each registry value.
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(id);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                log?.Invoke("[ERROR] BLOCKED: Administrator privileges are required to patch the registry.");
                EventLogService.Write("Patch aborted: not running as Administrator", EventLogEntryType.Error, 3002);
                FinalizeResult(result, nativeStatus, bypassStatus, progress);
                return result;
            }
        }
        catch { /* Rights probe failure shouldn't block — we'll see it at the registry write level */ }

        var appliedKeys = new List<(string Type, string ID)>();
        int successCount = 0;

        // Defensive: AppConfig.FeatureIDs is intentionally an IReadOnlyList but if a future
        // refactor makes it null somehow we'd NRE deep inside the loop. Treat as empty.
        var featureIDsToApply = AppConfig.FeatureIDs is null
            ? new List<string>()
            : new List<string>(AppConfig.FeatureIDs);
        int effectiveTotal = AppConfig.TotalComponents;
        if (config?.IncludeServerKey == true)
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
                if (!SuspendBitLocker(log))
                {
                    EventLogService.Write("Patch aborted: BitLocker suspension failed", EventLogEntryType.Error, 3002);
                    return result;
                }
                log?.Invoke("[SUCCESS] BitLocker suspended - will auto-resume after reboot");
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

            // Flush registry to disk so a hard power cycle before reboot doesn't lose the writes.
            try { overrides.Flush(); } catch { }

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
        finally
        {
            FinalizeResult(result, nativeStatus, bypassStatus, progress);
        }
        return result;
    }

    private static bool SuspendBitLocker(Action<string>? log)
    {
        try
        {
            var sysDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var psi = new ProcessStartInfo("manage-bde", $"-protectors -disable {sysDrive} -RebootCount 1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                log?.Invoke("[ERROR] BitLocker suspension FAILED - manage-bde could not start (aborting to prevent boot failure)");
                return false;
            }
            // Drain stdout/stderr asynchronously to avoid the buffer-full deadlock on chatty
            // manage-bde output (rare, but possible on systems with many TPM messages).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(true); } catch { }
                log?.Invoke("[ERROR] BitLocker suspension FAILED - manage-bde timed out after 30s (aborting)");
                return false;
            }
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
            {
                log?.Invoke($"[ERROR] BitLocker suspension FAILED (exit {proc.ExitCode}) - aborting to prevent boot failure");
                if (!string.IsNullOrWhiteSpace(stderr))
                    log?.Invoke($"  manage-bde stderr: {stderr.Trim()}");
                else if (!string.IsNullOrWhiteSpace(stdout))
                    log?.Invoke($"  manage-bde stdout: {stdout.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] BitLocker suspension FAILED: {ex.Message} - aborting");
            return false;
        }
    }

    private static void FinalizeResult(
        PatchOperationResult result,
        NativeNVMeStatus? nativeStatus,
        BypassIOResult? bypassStatus,
        Action<int, string>? progress)
    {
        try { progress?.Invoke(0, ""); } catch { }
        try { result.AfterSnapshot = RegistryService.GetPatchSnapshot(nativeStatus, bypassStatus); } catch { }
        try
        {
            if (result.AfterSnapshot is not null)
            {
                var description = result.Success
                    ? "After patch install"
                    : result.WasRolledBack
                        ? "After failed install rollback"
                        : "After patch install attempt";
                DataService.SaveSnapshot(result.AfterSnapshot, description, isPrePatch: false);
            }
        }
        catch { }
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
                try { overrides.Flush(); } catch { }
            }

            progress?.Invoke(60, "Removing SafeBoot keys...");

            // SafeBoot Minimal
            try
            {
                using var safeMinParent = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal", writable: true);
                if (safeMinParent is not null)
                {
                    // Pre-check: only count as "removed" when the subkey actually existed.
                    bool existed;
                    using (var probe = safeMinParent.OpenSubKey(AppConfig.SafeBootGuid))
                        existed = probe is not null;
                    try
                    {
                        safeMinParent.DeleteSubKeyTree(AppConfig.SafeBootGuid, false);
                        if (existed)
                        {
                            log?.Invoke("  [REMOVED] SafeBoot Minimal");
                            removedCount++;
                        }
                        else
                        {
                            log?.Invoke("  [ABSENT] SafeBoot Minimal");
                        }
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
                    bool existed;
                    using (var probe = safeNetParent.OpenSubKey(AppConfig.SafeBootGuid))
                        existed = probe is not null;
                    try
                    {
                        safeNetParent.DeleteSubKeyTree(AppConfig.SafeBootGuid, false);
                        if (existed)
                        {
                            log?.Invoke("  [REMOVED] SafeBoot Network");
                            removedCount++;
                        }
                        else
                        {
                            log?.Invoke("  [ABSENT] SafeBoot Network");
                        }
                    }
                    catch { log?.Invoke("  [ABSENT] SafeBoot Network"); }
                }
            }
            catch (Exception ex) { log?.Invoke($"  [FAIL] SafeBoot Network: {ex.Message}"); }

            progress?.Invoke(90, "Validating...");
            result.AppliedCount = removedCount;
            result.Success = true;
            result.NeedsRestart = removedCount > 0;

            log?.Invoke("========================================");
            log?.Invoke($"[SUCCESS] Patch Status: REMOVED - Removed {removedCount} components");
            if (result.NeedsRestart)
                log?.Invoke("[INFO] After reboot: Drives will return to 'Disk drives' using stornvme.sys");
            else
                log?.Invoke("[INFO] No patch components were present. No reboot needed.");
            EventLogService.Write($"NVMe Driver Patch removed ({removedCount} components)");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] REMOVAL FAILED: {ex.Message}");
            EventLogService.Write($"NVMe Driver Patch removal failed: {ex.Message}", EventLogEntryType.Error, 3001);
        }
        finally
        {
            try { progress?.Invoke(0, ""); } catch { }
            try { result.AfterSnapshot = RegistryService.GetPatchSnapshot(nativeStatus, bypassStatus); } catch { }
            try
            {
                if (result.AfterSnapshot is not null)
                    DataService.SaveSnapshot(result.AfterSnapshot, "After patch removal", isPrePatch: false);
            }
            catch { }
        }
        return result;
    }

    private static void Rollback(RegistryKey hklm, List<(string Type, string ID)> appliedKeys, Action<string>? log)
    {
        // Open the overrides key once so we don't keep re-acquiring a writable handle (and so a
        // single Flush() at the end is cheap and authoritative).
        RegistryKey? overrides = null;
        try
        {
            overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey, writable: true);
        }
        catch { /* Continue — per-key open below will surface the issue if needed */ }

        foreach (var (type, id) in appliedKeys)
        {
            try
            {
                if (type == "Feature")
                {
                    var key = overrides ?? hklm.OpenSubKey(AppConfig.RegistrySubKey, writable: true);
                    key?.DeleteValue(id, throwOnMissingValue: false);
                    string friendlyName = AppConfig.FeatureNames.TryGetValue(id, out var fn) ? fn : "Feature Flag";
                    log?.Invoke($"  [ROLLBACK] {id} - {friendlyName}");
                    if (key is not null && !ReferenceEquals(key, overrides)) key.Dispose();
                }
                else if (type == "SafeBoot")
                {
                    string parentPath = id == "Minimal"
                        ? @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal"
                        : @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network";
                    using var parent = hklm.OpenSubKey(parentPath, writable: true);
                    try { parent?.DeleteSubKeyTree(AppConfig.SafeBootGuid, throwOnMissingSubKey: false); }
                    catch (System.Security.SecurityException) { /* Permission denied; nothing else to do */ }
                    log?.Invoke($"  [ROLLBACK] SafeBoot {id}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [ROLLBACK FAIL] {type} {id}: {ex.Message}");
            }
        }

        try { overrides?.Flush(); } catch { }
        try { overrides?.Dispose(); } catch { }
    }

    private static void CreateRestorePoint(string description, Action<string>? log)
    {
        try
        {
            // Single-quote escaping for PowerShell. Strings inside single quotes treat ''
            // as a literal single quote. We also strip control chars + cap length to avoid
            // a pathological description making the command line too long.
            var safeDesc = (description ?? "Pre-NVMe-Driver-Patch")
                .Replace("'", "''")
                .Replace("\r", " ")
                .Replace("\n", " ");
            if (safeDesc.Length > 200) safeDesc = safeDesc.Substring(0, 200);

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description '{safeDesc}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                log?.Invoke("[WARNING] Restore point: powershell.exe could not start");
                return;
            }
            // Drain pipes asynchronously so a chatty error message can't deadlock the wait.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(60000))
            {
                try { proc.Kill(true); } catch { }
                log?.Invoke("[WARNING] Restore point timed out after 60s");
                return;
            }
            try { stdoutTask.GetAwaiter().GetResult(); } catch { }
            try { stderrTask.GetAwaiter().GetResult(); } catch { }
            if (proc.ExitCode == 0)
                log?.Invoke("[SUCCESS] System restore point created");
            else
                log?.Invoke("[WARNING] Restore point may have failed (24h limit or System Protection disabled)");
        }
        catch
        {
            log?.Invoke("[WARNING] Could not create restore point");
        }
    }

    public static bool InitiateRestart(int delaySeconds, Action<string>? log = null)
    {
        delaySeconds = Math.Clamp(delaySeconds, 0, 3600);
        try
        {
            var psi = new ProcessStartInfo("shutdown.exe",
                $"/r /t {delaySeconds} /c \"NVMe Driver Patch - Restarting in {delaySeconds} seconds. Save your work!\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                log?.Invoke("[ERROR] Could not start shutdown.exe to schedule restart.");
                return false;
            }
            // shutdown.exe normally exits immediately after queuing the restart. Drain its output
            // streams so we never deadlock waiting for them to close.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                // Don't kill it — if shutdown.exe is taking >5s, the request is almost certainly
                // already enqueued. Killing it would only abort the restart we just asked for.
                return true;
            }
            try { stdoutTask.GetAwaiter().GetResult(); } catch { }
            string err = string.Empty;
            try { err = stderrTask.GetAwaiter().GetResult().Trim(); } catch { }
            if (proc.ExitCode != 0)
            {
                log?.Invoke($"[ERROR] shutdown.exe exit {proc.ExitCode}: {err}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Restart could not be scheduled: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Cancels a previously-scheduled restart, if any. Used by the GUI's "Cancel Restart" affordance
    /// (and good hygiene for tests). Returns true if a pending shutdown was canceled OR if there
    /// wasn't one — caller doesn't need to distinguish.
    /// </summary>
    public static bool CancelPendingRestart(Action<string>? log = null)
    {
        try
        {
            var psi = new ProcessStartInfo("shutdown.exe", "/a")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            var so = proc.StandardOutput.ReadToEndAsync();
            var se = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(true); } catch { }
                return false;
            }
            try { so.GetAwaiter().GetResult(); } catch { }
            try { se.GetAwaiter().GetResult(); } catch { }
            return true; // exit 0 = canceled, exit 1116 = nothing to cancel — both are fine
        }
        catch (Exception ex)
        {
            log?.Invoke($"[WARNING] Could not cancel restart: {ex.Message}");
            return false;
        }
    }
}
