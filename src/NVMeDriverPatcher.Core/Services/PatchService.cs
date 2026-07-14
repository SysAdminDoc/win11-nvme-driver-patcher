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

    // Status of the native FeatureStore / ViVeTool fallback undo attempted during Uninstall.
    // Null when no removal ran; otherwise a human-readable summary surfaced in the activity rail.
    public string? FeatureStoreResetSummary { get; set; }

    // Components still present after a post-removal re-probe of every store. Non-empty means the
    // removal was PARTIAL: Success is false, the watchdog stays armed, and the CLI exits non-zero.
    // Surfaced to the activity log and (via snapshots) to support bundles as structured evidence.
    public List<string> Residue { get; set; } = new();
}

public enum PatchPreRegistryAbortReason
{
    None,
    VeraCryptSystemEncryption,
    BitLockerSuspensionFailed
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
        try { DataService.SaveBypassIoSnapshot(BypassIoInspectorService.Inspect(), "Before patch install", isPrePatch: true); } catch { }

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
            // Step 0: Suspend BitLocker — MUST succeed before touching drivers.
            // On failure we abort, but route through the outer `finally` so the progress bar
            // clears and an after-snapshot is still captured — matches the VeraCrypt branch
            // above. Throwing here is the cleanest way to reuse that cleanup path without
            // duplicating FinalizeResult at every early return.
            if (bitLockerEnabled)
            {
                log?.Invoke("Suspending BitLocker for one reboot cycle...");
                if (!SuspendBitLocker(log))
                {
                    EventLogService.Write("Patch aborted: BitLocker suspension failed", EventLogEntryType.Error, 3002);
                    throw new PatchAbortedException("BitLocker suspension failed — patch aborted before any registry writes.");
                }
                log?.Invoke("[SUCCESS] BitLocker suspended - will auto-resume after reboot");
            }

            // Protected NVMe DATA volumes without auto-unlock re-lock after the reboot. Suspend them
            // too (best-effort — access loss, not a boot brick, so a failure warns rather than aborts).
            SuspendBitLockerDataVolumes(log);

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
                    appliedKeys.Add(("Feature", id));
                    var verify = overrides.GetValue(id);
                    if (verify is int v && v == 1)
                    {
                        log?.Invoke($"  [OK] {id} - {friendlyName}");
                        successCount++;
                    }
                    else
                    {
                        log?.Invoke($"  [FAIL] {id} - {friendlyName} (write verify failed)");
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  [FAIL] {id} - {ex.Message}");
                }
            }

            // Capture the exact prior state of every SafeBoot key BEFORE we touch them, so removal
            // can restore byte-for-byte and delete only what we create. Critically, on builds where
            // Windows already ships these keys (issue #13, a named "NvmeDisk" value) this journal is
            // what stops uninstall from erasing OS-owned state. Never changes ACLs.
            try
            {
                var journal = SafeBootStateService.CaptureJournal(new RealSafeBootRegistry(), DateTime.UtcNow.ToString("o"));
                SafeBootStateService.SaveJournal(workingDir, journal, log);
            }
            catch (Exception ex) { log?.Invoke($"  [WARN] SafeBoot journal capture failed: {ex.Message}"); }

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
                        try { safeMin.Flush(); } catch { }
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
                        try { safeNet.Flush(); } catch { }
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

            // Supplemental service-name SafeBoot entries for Windows 25H2 compatibility.
            // KB5079391 (March 2026) tightened how Safe Mode resolves storage drivers — the
            // GUID-class approach above is sufficient on 24H2 and earlier, but 25H2 now
            // requires the canonical service-name entry used by storport/stornvme/storahci.
            // These are BEST-EFFORT: they do NOT count toward successCount/effectiveTotal
            // and are NOT tracked in appliedKeys. A failure here is logged but never causes
            // the patch to fail or roll back on pre-25H2 machines.
            try
            {
                using var svcMin = hklm.CreateSubKey(AppConfig.SafeBootMinimalServicePath);
                if (svcMin is not null)
                {
                    svcMin.SetValue("", AppConfig.SafeBootServiceValue);
                    try { svcMin.Flush(); } catch { }
                    log?.Invoke("  [OK] SafeBoot Minimal (service name) -- 25H2 compat");
                }
            }
            catch (Exception ex) { log?.Invoke($"  [WARN] SafeBoot Minimal service entry: {ex.Message}"); }

            try
            {
                using var svcNet = hklm.CreateSubKey(AppConfig.SafeBootNetworkServicePath);
                if (svcNet is not null)
                {
                    svcNet.SetValue("", AppConfig.SafeBootServiceValue);
                    try { svcNet.Flush(); } catch { }
                    log?.Invoke("  [OK] SafeBoot Network (service name) -- 25H2 compat");
                }
            }
            catch (Exception ex) { log?.Invoke($"  [WARN] SafeBoot Network service entry: {ex.Message}"); }

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

                bool rollbackFullyReversed = Rollback(hklm, appliedKeys, workingDir, log);
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
        catch (PatchAbortedException)
        {
            // Already logged + event-logged at the throw site. Fall through to finally
            // so the progress bar clears and we still capture an after-snapshot.
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

    // Internal sentinel used by Install() to abort cleanly through the shared finally
    // block without emitting a second "INSTALLATION FAILED" log line. Not thrown outside
    // this class.
    private sealed class PatchAbortedException : Exception
    {
        public PatchAbortedException(string message) : base(message) { }
    }

    private static bool SuspendBitLocker(Action<string>? log)
    {
        var sysDrive = NormalizeSystemDrive(Environment.GetEnvironmentVariable("SystemDrive"));
        return SuspendBitLockerDrive(sysDrive, log);
    }

    // Best-effort suspension of protected NVMe DATA volumes without auto-unlock. Never aborts the
    // patch — a data volume re-locking is an access-loss the user can recover with the key, not a
    // boot brick — but each failure is surfaced so the user knows to have the recovery key ready.
    private static void SuspendBitLockerDataVolumes(Action<string>? log)
    {
        try
        {
            var dataVols = DriveService.DataVolumesNeedingAttention(DriveService.GetBitLockerVolumes());
            foreach (var v in dataVols)
            {
                log?.Invoke($"[WARNING] BitLocker data volume {v.DriveLetter} has no auto-unlock — suspending it for one reboot so it doesn't re-lock.");
                if (!SuspendBitLockerDrive(v.DriveLetter, log))
                    log?.Invoke($"[WARNING] Could not suspend BitLocker on {v.DriveLetter}. Keep its recovery key handy — it may prompt after reboot.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[WARNING] BitLocker data-volume suspension probe failed: {ex.Message}");
        }
    }

    private static bool SuspendBitLockerDrive(string drive, Action<string>? log)
    {
        try
        {
            var psi = new ProcessStartInfo("manage-bde")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-protectors");
            psi.ArgumentList.Add("-disable");
            psi.ArgumentList.Add(drive);
            psi.ArgumentList.Add("-RebootCount");
            psi.ArgumentList.Add("1");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                log?.Invoke($"[ERROR] BitLocker suspension FAILED on {drive} - manage-bde could not start");
                return false;
            }
            // Drain stdout/stderr asynchronously to avoid the buffer-full deadlock on chatty
            // manage-bde output (rare, but possible on systems with many TPM messages).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(true); } catch { }
                log?.Invoke($"[ERROR] BitLocker suspension FAILED on {drive} - manage-bde timed out after 30s");
                return false;
            }
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
            {
                log?.Invoke($"[ERROR] BitLocker suspension FAILED on {drive} (exit {proc.ExitCode})");
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
            log?.Invoke($"[ERROR] BitLocker suspension FAILED on {drive}: {ex.Message}");
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

    internal static PatchPreRegistryAbortReason ClassifyPreRegistryAbort(
        bool veraCryptDetected,
        bool bitLockerEnabled,
        bool bitLockerSuspended)
    {
        if (veraCryptDetected)
            return PatchPreRegistryAbortReason.VeraCryptSystemEncryption;

        if (bitLockerEnabled && !bitLockerSuspended)
            return PatchPreRegistryAbortReason.BitLockerSuspensionFailed;

        return PatchPreRegistryAbortReason.None;
    }

    internal static bool RequiresManualRecoveryWarning(PatchOperationResult result) =>
        result.WasRolledBack && !result.RollbackFullyReversed;

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
                try { DataService.SaveBypassIoSnapshot(BypassIoInspectorService.Inspect(), description, isPrePatch: false); } catch { }
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
        try { DataService.SaveBypassIoSnapshot(BypassIoInspectorService.Inspect(), "Before patch removal", isPrePatch: true); } catch { }

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

            ReportProgress(progress, 60, "Restoring SafeBoot keys...");

            // Restore SafeBoot keys to their pre-apply state from the journal captured at install.
            // This deletes ONLY keys/values the app created and preserves any OS-owned state (e.g.
            // a pre-existing GUID key carrying a "NvmeDisk" value — issue #13). Never changes ACLs.
            var safeBootJournal = SafeBootStateService.LoadJournal(workingDir);
            if (safeBootJournal is not null)
            {
                var restoreFailures = SafeBootStateService.RestoreFromJournal(new RealSafeBootRegistry(), safeBootJournal, log);
                if (restoreFailures.Count == 0)
                {
                    // Count the keys we actually created (and therefore removed) toward removedCount
                    // so NeedsRestart/messaging stay meaningful.
                    removedCount += safeBootJournal.Entries.Count(e =>
                        !e.Existed &&
                        (e.Path == AppConfig.SafeBootMinimalPath || e.Path == AppConfig.SafeBootNetworkPath));
                }
                else
                {
                    foreach (var f in restoreFailures)
                        log?.Invoke($"  [FAIL] SafeBoot restore incomplete: {f}");
                    // Leave the journal in place so a re-run can retry the restore.
                }
            }
            else
            {
                // No journal (patch predates journalling): fall back to a SAFE delete that removes
                // the app's GUID key ONLY when it has no OS-owned named values. Never blow away a
                // key that Windows populated (issue #13).
                RemoveOwnedSafeBootKey(hklm, @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal", AppConfig.SafeBootGuid, "SafeBoot Minimal", ref removedCount, log);
                RemoveOwnedSafeBootKey(hklm, @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network", AppConfig.SafeBootGuid, "SafeBoot Network", ref removedCount, log);
                int svc = 0;
                RemoveOwnedSafeBootKey(hklm, @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal", AppConfig.SafeBootServiceName, "SafeBoot Minimal (service name)", ref svc, log);
                RemoveOwnedSafeBootKey(hklm, @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network", AppConfig.SafeBootServiceName, "SafeBoot Network (service name)", ref svc, log);
            }

            // Undo the native FeatureStore / ViVeTool fallback enablement too. The registry
            // override deletions above do NOT touch the FeatureStore configuration the fallback
            // path writes, so a fallback user would otherwise "remove" the patch yet keep
            // nvmedisk bound after reboot. Best-effort: a registry-only install has no fallback
            // IDs enabled and this reports a clean no-op; never fails the uninstall.
            ReportProgress(progress, 80, "Undoing FeatureStore fallback...");
            try
            {
                var fsReset = FeatureStoreWriterService.ResetAppliedFallback();
                result.FeatureStoreResetSummary = fsReset.Summary;
                log?.Invoke($"  [FeatureStore] {fsReset.Summary}");
                if (fsReset.Success && fsReset.AppliedIds.Length > 0)
                    result.NeedsRestart = true;
            }
            catch (Exception ex)
            {
                result.FeatureStoreResetSummary = $"FeatureStore fallback reset skipped: {ex.Message}";
                log?.Invoke($"  [FeatureStore] reset skipped: {ex.Message}");
            }

            // Residue re-probe: re-read EVERY store instead of trusting that the per-component
            // deletes above (whose failures are individually logged and swallowed) succeeded.
            // Success is possible ONLY when nothing this app is responsible for remains.
            ReportProgress(progress, 90, "Validating removal (residue probe)...");
            result.AppliedCount = removedCount;
            result.Residue = ProbeRemovalResidue(hklm, workingDir, log);
            result.Success = result.Residue.Count == 0;
            result.NeedsRestart = removedCount > 0 || result.NeedsRestart;

            // Only discard the SafeBoot journal once removal is verified clean — the residue probe
            // above needs it to tell app-added state from pre-existing OS/user state.
            if (result.Success)
                SafeBootStateService.DeleteJournal(workingDir);

            log?.Invoke("========================================");
            if (result.Success)
            {
                log?.Invoke($"[SUCCESS] Patch Status: REMOVED - Removed {removedCount} components (zero residue verified)");
                if (result.NeedsRestart)
                    log?.Invoke("[INFO] After reboot: Drives will return to 'Disk drives' using stornvme.sys");
                else
                    log?.Invoke("[INFO] No patch components were present. No reboot needed.");
                EventLogService.Write($"NVMe Driver Patch removed ({removedCount} components, zero residue)");
            }
            else
            {
                log?.Invoke($"[PARTIAL] Patch removal INCOMPLETE — {result.Residue.Count} component(s) still present after removal:");
                foreach (var r in result.Residue)
                    log?.Invoke($"  [RESIDUE] {r}");
                log?.Invoke("[RECOVERY] Re-run 'Remove Patch' as Administrator. If residue persists, restore the");
                log?.Invoke("[RECOVERY] pre-removal registry backup or run the Recovery Kit from WinRE. The watchdog");
                log?.Invoke("[RECOVERY] stays armed until removal is clean.");
                EventLogService.Write(
                    $"NVMe Driver Patch removal INCOMPLETE — residue: {string.Join(", ", result.Residue)}",
                    EventLogEntryType.Warning, 3002);
            }
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
                {
                    DataService.SaveSnapshot(result.AfterSnapshot, "After patch removal", isPrePatch: false);
                    try { DataService.SaveBypassIoSnapshot(BypassIoInspectorService.Inspect(), "After patch removal", isPrePatch: false); } catch { }
                }
            }
            catch { }
        }
        return result;
    }

    // Re-reads every store this app is responsible for and returns a human-readable list of
    // anything still present. An empty list is the ONLY basis for reporting a successful removal.
    // Fails closed: if a store cannot be read to confirm it is clean, that counts as residue.
    internal static List<string> ProbeRemovalResidue(RegistryKey hklm, string? workingDir, Action<string>? log)
    {
        var residue = new List<string>();

        // 1) Feature override values under the FeatureManagement Overrides key.
        try
        {
            using var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey, writable: false);
            if (overrides is not null)
            {
                foreach (var id in AppConfig.FeatureIDs.Append(AppConfig.ServerFeatureID))
                {
                    try
                    {
                        if (overrides.GetValue(id) is not null)
                            residue.Add($"Feature override value {id}");
                    }
                    catch (Exception ex) { residue.Add($"Feature override {id} unverifiable ({ex.GetType().Name})"); }
                }
            }
        }
        catch (Exception ex)
        {
            residue.Add($"Feature overrides key unverifiable ({ex.GetType().Name})");
        }

        // 2) SafeBoot: residue is ONLY the app's own default value still present. A preserved
        // OS-owned key (issue #13) or a correctly-restored pre-existing default are NOT residue —
        // the journal captured at apply distinguishes "our value" from state already there.
        residue.AddRange(ProbeSafeBootResidue(workingDir));

        // 3) FeatureStore / ViVeTool fallback IDs. A registry-only install has none and this is a
        // clean no-op; a fallback install must have had them reset above.
        try
        {
            if (FeatureStoreWriterService.HasFallbackEvidence())
                residue.Add("FeatureStore fallback feature IDs still enabled");
        }
        catch (Exception ex)
        {
            // Can't confirm the fallback store is clean — fail closed.
            residue.Add($"FeatureStore fallback state unverifiable ({ex.GetType().Name})");
        }

        return residue;
    }

    // Legacy fallback for patches applied before SafeBoot journalling. Deletes the app's subkey
    // ONLY when it has no OS-owned named values (issue #13): a key Windows populated with a
    // "NvmeDisk" value must never be blown away by our uninstall.
    private static void RemoveOwnedSafeBootKey(RegistryKey hklm, string parentPath, string leaf, string label, ref int removedCount, Action<string>? log)
    {
        try
        {
            using var parent = hklm.OpenSubKey(parentPath, writable: true);
            if (parent is null) return;

            bool existed;
            bool hasForeignValues = false;
            using (var probe = parent.OpenSubKey(leaf))
            {
                existed = probe is not null;
                if (probe is not null)
                    hasForeignValues = probe.GetValueNames().Any(n => n.Length > 0);
            }

            if (!existed)
            {
                log?.Invoke($"  [ABSENT] {label}");
                return;
            }
            if (hasForeignValues)
            {
                // OS owns this key — remove only our default value, keep the key + foreign values.
                using var key = parent.OpenSubKey(leaf, writable: true);
                try { key?.DeleteValue("", throwOnMissingValue: false); } catch { }
                log?.Invoke($"  [PRESERVED] {label} — OS-owned values kept; removed only the app default value");
                removedCount++;
                return;
            }
            parent.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
            log?.Invoke($"  [REMOVED] {label}");
            removedCount++;
        }
        catch (Exception ex) { log?.Invoke($"  [FAIL] {label}: {ex.Message}"); }
    }

    private static List<string> ProbeSafeBootResidue(string? workingDir)
    {
        var found = new List<string>();
        var reg = new RealSafeBootRegistry();
        var journal = string.IsNullOrWhiteSpace(workingDir) ? null : SafeBootStateService.LoadJournal(workingDir);

        foreach (var path in new[] { AppConfig.SafeBootMinimalPath, AppConfig.SafeBootNetworkPath })
        {
            var snap = reg.Read(path);
            if (snap.AccessDenied)
            {
                found.Add($"{path} unverifiable (access denied)");
                continue;
            }
            var current = snap.DefaultValue;
            bool ourValuePresent = current is not null &&
                string.Equals(current, AppConfig.SafeBootValue, StringComparison.OrdinalIgnoreCase);
            if (!ourValuePresent) continue;

            // Our value is present. If the pre-apply journal shows the SAME default was already
            // there, it is pre-existing OS/user state we correctly restored — not residue.
            var prior = journal?.Entries.FirstOrDefault(e => e.Path == path)?.ToSnapshot();
            bool priorHadOurValue = prior is not null && prior.Existed &&
                string.Equals(prior.DefaultValue, AppConfig.SafeBootValue, StringComparison.OrdinalIgnoreCase);
            if (!priorHadOurValue)
                found.Add($"SafeBoot default value at {path}");
        }
        return found;
    }

    // Returns true only when every registered write was reversed. A partial rollback is a
    // dangerous state — the user thinks the system is back to pre-patch but some keys are
    // still present. Callers MUST act on the false return by warning the user and pointing
    // them at the recovery kit / restore point.
    private static bool Rollback(RegistryKey hklm, List<(string Type, string ID)> appliedKeys, string workingDir, Action<string>? log)
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
        try
        {
        foreach (var (type, id) in appliedKeys)
        {
            try
            {
                if (type == "Feature")
                {
                    RegistryKey? fallbackKey = null;
                    try
                    {
                        var key = overrides ?? (fallbackKey = hklm.OpenSubKey(AppConfig.RegistrySubKey, writable: true));
                        key?.DeleteValue(id, throwOnMissingValue: false);
                        string friendlyName = AppConfig.FeatureNames.TryGetValue(id, out var fn) ? fn : "Feature Flag";
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
                    }
                    finally
                    {
                        fallbackKey?.Dispose();
                    }
                }
                else if (type == "SafeBoot")
                {
                    // Restore to the pre-apply state captured in the journal — NEVER blindly delete
                    // the GUID subtree, which on issue-#13 builds would erase the OS-owned key.
                    string path = id == "Minimal" ? AppConfig.SafeBootMinimalPath : AppConfig.SafeBootNetworkPath;
                    var reg = new RealSafeBootRegistry();
                    var priorEntry = SafeBootStateService.LoadJournal(workingDir)?.Entries
                        .FirstOrDefault(e => e.Path == path);

                    try
                    {
                        if (priorEntry is not null)
                        {
                            reg.ApplyRestore(path, SafeBootStateService.PlanRestore(priorEntry.ToSnapshot()));
                            log?.Invoke($"  [ROLLBACK] SafeBoot {id} restored to pre-apply state");
                        }
                        else
                        {
                            // No journal — safe delete that preserves any OS-owned named values.
                            string parentPath = id == "Minimal"
                                ? @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal"
                                : @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network";
                            int dummy = 0;
                            RemoveOwnedSafeBootKey(hklm, parentPath, AppConfig.SafeBootGuid, $"SafeBoot {id}", ref dummy, log);
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"  [ROLLBACK FAIL] SafeBoot {id}: {ex.Message}");
                        allReversed = false;
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [ROLLBACK FAIL] {type} {id}: {ex.Message}");
                allReversed = false;
            }
        }

        // Unconditionally clean up supplemental service-name SafeBoot entries. These are
        // not tracked in appliedKeys (best-effort compat writes during Install), but must
        // still be removed on rollback to avoid leaking them into a reverted state.
        try
        {
            using var safeMinParent = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal", writable: true);
            safeMinParent?.DeleteSubKeyTree(AppConfig.SafeBootServiceName, throwOnMissingSubKey: false);
        }
        catch { }
        try
        {
            using var safeNetParent = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\Network", writable: true);
            safeNetParent?.DeleteSubKeyTree(AppConfig.SafeBootServiceName, throwOnMissingSubKey: false);
        }
        catch { }

        try { overrides?.Flush(); } catch { }
        }
        finally
        {
            try { overrides?.Dispose(); } catch { }
        }
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
