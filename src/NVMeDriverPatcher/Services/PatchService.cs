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
    // Set to false when Rollback itself couldn't reverse every write — the caller must warn
    // the user and point them at the pre-patch backup / System Restore. Meaningful only when
    // WasRolledBack is true.
    public bool RollbackFullyReversed { get; set; } = true;
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
        string workingDir = string.IsNullOrWhiteSpace(config.WorkingDir)
            ? AppConfig.GetWorkingDir()
            : config.WorkingDir;
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

        // Profile-driven key set. Safe = primary flag only (community-recommended default —
        // the extended flags are correlated with BSOD reports). Full = all three.
        var profile = config.PatchProfile;
        bool includeServer = config.IncludeServerKey;
        var featureIDsToApply = new List<string>(AppConfig.GetFeatureIDsForProfile(profile));
        int effectiveTotal = AppConfig.GetTotalComponents(profile, includeServer);
        log?.Invoke($"Mode: {profile.ToString().ToUpperInvariant()} ({(profile == PatchProfile.Safe ? "primary flag only" : "primary + extended flags")})");
        if (includeServer)
        {
            featureIDsToApply.Add(AppConfig.ServerFeatureID);
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
            ReportProgress(progress, 10, "Creating registry backup...");
            RegistryService.ExportRegistryBackup(workingDir, "Pre_Patch");
            ReportProgress(progress, 30, "Creating restore point...");
            CreateRestorePoint("Pre-NVMe-Driver-Patch", log);

            // Step 2: Apply registry components
            log?.Invoke($"Step 2/3: Applying {effectiveTotal} registry components...");
            ReportProgress(progress, 60, "Applying registry changes...");

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

            // SafeBoot Minimal — track in appliedKeys ONLY after we've confirmed the write took.
            // If CreateSubKey succeeds but SetValue fails, the empty subkey is still present and
            // must still be tracked for rollback so we don't leak it. We register it pre-SetValue
            // for that reason.
            try
            {
                bool safeMinKeyCreated = false;
                using (var safeMin = hklm.CreateSubKey(AppConfig.SafeBootMinimalPath))
                {
                    if (safeMin is not null)
                    {
                        safeMinKeyCreated = true;
                        appliedKeys.Add(("SafeBoot", "Minimal"));
                        safeMin.SetValue("", AppConfig.SafeBootValue);
                        var val = safeMin.GetValue("") as string;
                        if (val == AppConfig.SafeBootValue)
                        {
                            log?.Invoke("  [OK] SafeBoot Minimal Support");
                            successCount++;
                        }
                        else
                        {
                            log?.Invoke("  [FAIL] SafeBoot Minimal Support (write verify failed)");
                        }
                    }
                    else
                    {
                        log?.Invoke("  [FAIL] SafeBoot Minimal Support (CreateSubKey returned null)");
                    }
                }
                if (!safeMinKeyCreated)
                {
                    // CreateSubKey didn't actually land — don't pretend we need to roll it back.
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [FAIL] SafeBoot Minimal: {ex.Message}");
                // If we made it past CreateSubKey before throwing, the subkey may still exist;
                // ensure rollback is registered so it doesn't leak.
                try { using var probe = hklm.OpenSubKey(AppConfig.SafeBootMinimalPath); if (probe is not null && !appliedKeys.Contains(("SafeBoot", "Minimal"))) appliedKeys.Add(("SafeBoot", "Minimal")); } catch { }
            }

            // SafeBoot Network — same pattern.
            try
            {
                bool safeNetKeyCreated = false;
                using (var safeNet = hklm.CreateSubKey(AppConfig.SafeBootNetworkPath))
                {
                    if (safeNet is not null)
                    {
                        safeNetKeyCreated = true;
                        appliedKeys.Add(("SafeBoot", "Network"));
                        safeNet.SetValue("", AppConfig.SafeBootValue);
                        var val = safeNet.GetValue("") as string;
                        if (val == AppConfig.SafeBootValue)
                        {
                            log?.Invoke("  [OK] SafeBoot Network Support");
                            successCount++;
                        }
                        else
                        {
                            log?.Invoke("  [FAIL] SafeBoot Network Support (write verify failed)");
                        }
                    }
                    else
                    {
                        log?.Invoke("  [FAIL] SafeBoot Network Support (CreateSubKey returned null)");
                    }
                }
                if (!safeNetKeyCreated)
                {
                    // See note above.
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [FAIL] SafeBoot Network: {ex.Message}");
                try { using var probe = hklm.OpenSubKey(AppConfig.SafeBootNetworkPath); if (probe is not null && !appliedKeys.Contains(("SafeBoot", "Network"))) appliedKeys.Add(("SafeBoot", "Network")); } catch { }
            }

            // Flush registry to disk so a hard power cycle before reboot doesn't lose the writes.
            try { overrides.Flush(); } catch { }

            // Step 3: Validate
            ReportProgress(progress, 95, "Validating...");
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
                ReportProgress(progress, 96, "Rolling back...");

                bool rollbackFullyReversed = Rollback(hklm, appliedKeys, log);
                result.WasRolledBack = true;
                result.RollbackFullyReversed = rollbackFullyReversed;

                if (rollbackFullyReversed)
                {
                    log?.Invoke("[WARNING] Rollback complete - system returned to pre-patch state");
                    EventLogService.Write($"NVMe Driver Patch rolled back after partial failure ({successCount}/{effectiveTotal})",
                        EventLogEntryType.Warning, 2001);
                }
                else
                {
                    log?.Invoke("[ERROR] Rollback INCOMPLETE - some writes could not be reversed");
                    log?.Invoke("[ERROR] Use the pre-patch registry backup or System Restore to recover");
                    EventLogService.Write(
                        $"NVMe Driver Patch rollback INCOMPLETE after partial failure ({successCount}/{effectiveTotal}). Some registry keys may remain set; consult the registry backup.",
                        EventLogEntryType.Error, 3003);
                }
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
            var sysDrive = NormalizeSystemDrive(Environment.GetEnvironmentVariable("SystemDrive"));
            var psi = new ProcessStartInfo("manage-bde")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-protectors");
            psi.ArgumentList.Add("-disable");
            psi.ArgumentList.Add(sysDrive);
            psi.ArgumentList.Add("-RebootCount");
            psi.ArgumentList.Add("1");

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

    internal static string NormalizeSystemDrive(string? systemDrive)
    {
        var root = DriveService.NormalizeDriveRoot(systemDrive);
        return string.IsNullOrEmpty(root) ? "C:" : root[..2];
    }

    internal static void ReportProgress(Action<int, string>? progress, int value, string text)
    {
        try
        {
            progress?.Invoke(value, text);
        }
        catch
        {
            // UI progress callbacks are best-effort. They should never change whether a
            // registry operation succeeds, nor mask rollback/finalization work.
        }
    }

    private static void FinalizeResult(
        PatchOperationResult result,
        NativeNVMeStatus? nativeStatus,
        BypassIOResult? bypassStatus,
        Action<int, string>? progress)
    {
        ReportProgress(progress, 0, "");
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
        string workingDir = string.IsNullOrWhiteSpace(config.WorkingDir)
            ? AppConfig.GetWorkingDir()
            : config.WorkingDir;
        result.BeforeSnapshot = RegistryService.GetPatchSnapshot(nativeStatus, bypassStatus);
        try { DataService.SaveSnapshot(result.BeforeSnapshot, "Before patch removal", isPrePatch: true); } catch { }

        log?.Invoke("========================================");
        log?.Invoke("STARTING PATCH REMOVAL");
        log?.Invoke("========================================");
        EventLogService.Write("NVMe Driver Patch removal started");

        ReportProgress(progress, 10, "Creating backup...");
        RegistryService.ExportRegistryBackup(workingDir, "Pre_Removal");
        int removedCount = 0;

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            log?.Invoke("Removing registry components...");
            ReportProgress(progress, 30, "Removing feature flags...");

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

            ReportProgress(progress, 60, "Removing SafeBoot keys...");

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

            ReportProgress(progress, 90, "Validating...");
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
            ReportProgress(progress, 0, "");
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

    // Returns true only when every registered write was reversed. A partial rollback is a
    // dangerous state — the user thinks the system is back to pre-patch but some keys are
    // still present. Callers MUST act on the false return by warning the user and pointing
    // them at the recovery kit / restore point.
    private static bool Rollback(RegistryKey hklm, List<(string Type, string ID)> appliedKeys, Action<string>? log)
    {
        // Open the overrides key once so we don't keep re-acquiring a writable handle (and so a
        // single Flush() at the end is cheap and authoritative).
        RegistryKey? overrides = null;
        try
        {
            overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey, writable: true);
        }
        catch { /* Continue — per-key open below will surface the issue if needed */ }

        bool allReversed = true;

        foreach (var (type, id) in appliedKeys)
        {
            try
            {
                if (type == "Feature")
                {
                    var key = overrides ?? hklm.OpenSubKey(AppConfig.RegistrySubKey, writable: true);
                    key?.DeleteValue(id, throwOnMissingValue: false);
                    string friendlyName = AppConfig.FeatureNames.TryGetValue(id, out var fn) ? fn : "Feature Flag";
                    // Verify the value is really gone — a silent no-op would leave a leak.
                    if (key is not null)
                    {
                        if (key.GetValue(id) is not null)
                        {
                            log?.Invoke($"  [ROLLBACK FAIL] {id} - {friendlyName} still present after DeleteValue");
                            allReversed = false;
                        }
                        else
                        {
                            log?.Invoke($"  [ROLLBACK] {id} - {friendlyName}");
                        }
                    }
                    if (key is not null && !ReferenceEquals(key, overrides)) key.Dispose();
                }
                else if (type == "SafeBoot")
                {
                    string parentPath = id == "Minimal"
                        ? @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal"
                        : @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network";
                    using var parent = hklm.OpenSubKey(parentPath, writable: true);
                    if (parent is null)
                    {
                        log?.Invoke($"  [ROLLBACK SKIP] SafeBoot {id} — parent key unavailable");
                        // Can't verify; err on the side of reporting failure so the caller warns.
                        allReversed = false;
                        continue;
                    }
                    try { parent.DeleteSubKeyTree(AppConfig.SafeBootGuid, throwOnMissingSubKey: false); }
                    catch (System.Security.SecurityException)
                    {
                        log?.Invoke($"  [ROLLBACK FAIL] SafeBoot {id} — permission denied");
                        allReversed = false;
                        continue;
                    }
                    // Verify the subkey is really gone.
                    using (var probe = parent.OpenSubKey(AppConfig.SafeBootGuid))
                    {
                        if (probe is not null)
                        {
                            log?.Invoke($"  [ROLLBACK FAIL] SafeBoot {id} subkey still present after delete");
                            allReversed = false;
                            continue;
                        }
                    }
                    log?.Invoke($"  [ROLLBACK] SafeBoot {id}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [ROLLBACK FAIL] {type} {id}: {ex.Message}");
                allReversed = false;
            }
        }

        try { overrides?.Flush(); } catch { }
        try { overrides?.Dispose(); } catch { }
        return allReversed;
    }

    private static void CreateRestorePoint(string description, Action<string>? log)
    {
        try
        {
            var psi = CreateRestorePointStartInfo(description);
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

    internal static ProcessStartInfo CreateRestorePointStartInfo(string? description)
    {
        var safeDesc = SanitizeRestorePointDescription(description);
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add($"Checkpoint-Computer -Description '{safeDesc}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop");
        return psi;
    }

    internal static string SanitizeRestorePointDescription(string? description)
    {
        // Single-quote escaping for PowerShell. Strings inside single quotes treat ''
        // as a literal single quote. We also strip control chars + cap length to avoid
        // a pathological description making the command line too long.
        var safeDesc = string.IsNullOrWhiteSpace(description)
            ? "Pre-NVMe-Driver-Patch"
            : description;
        safeDesc = safeDesc
            .Replace("'", "''")
            .Replace("\r", " ")
            .Replace("\n", " ");
        return safeDesc.Length > 200 ? safeDesc[..200] : safeDesc;
    }

    public static bool InitiateRestart(int delaySeconds, Action<string>? log = null)
    {
        delaySeconds = Math.Clamp(delaySeconds, 0, 3600);
        try
        {
            // Pass args via ArgumentList so each argument is individually escaped —
            // defense-in-depth even though delaySeconds is already clamped.
            var psi = new ProcessStartInfo("shutdown.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/r");
            psi.ArgumentList.Add("/t");
            psi.ArgumentList.Add(delaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"NVMe Driver Patch - Restarting in {delaySeconds} seconds. Save your work!");
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
            var psi = new ProcessStartInfo("shutdown.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/a");
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
            return IsCancelRestartSuccessExitCode(proc.ExitCode);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[WARNING] Could not cancel restart: {ex.Message}");
            return false;
        }
    }

    internal static bool IsCancelRestartSuccessExitCode(int exitCode) =>
        exitCode is 0 or 1116;
}
