using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using NVMeDriverPatcher.Interop;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// ============================================================================
// HIGH RISK: This service performs live driver hot-swap on NVMe devices.
// It dismounts volumes and re-enumerates device nodes, which can cause data loss
// if used on a boot device or if volumes have open file handles.
// This should ONLY be used on non-boot NVMe drives with no actively-used volumes.
// ============================================================================

/// <summary>
/// Service for live NVMe driver hot-swap without requiring a full system reboot.
/// Only operates on non-boot NVMe drives. Dismounts volumes, re-enumerates the
/// device node, and waits for the device to return with the new driver stack.
/// </summary>
public static class HotSwapService
{
    /// <summary>
    /// Determines whether the given drive can be safely hot-swapped.
    /// Returns false for boot devices, non-NVMe drives, or drives with unknown state.
    /// </summary>
    /// <param name="drive">The target system drive.</param>
    /// <returns>True if the drive is a non-boot NVMe that can be hot-swapped.</returns>
    public static bool CanHotSwap(SystemDrive drive)
    {
        if (drive is null) return false;
        if (drive.IsBoot) return false;
        if (!drive.IsNVMe) return false;
        if (string.IsNullOrEmpty(drive.PNPDeviceID) || drive.PNPDeviceID == "Unknown") return false;
        return true;
    }

    /// <summary>
    /// Performs a live driver hot-swap on a non-boot NVMe drive.
    /// Sequence: verify non-boot -> dismount volumes -> re-enumerate device node -> wait for return.
    /// If the device does not return within the timeout, attempts rollback.
    /// </summary>
    /// <param name="drive">Target non-boot NVMe drive.</param>
    /// <param name="log">Optional logging callback for status updates.</param>
    /// <returns>A result indicating success or failure with details.</returns>
    // HIGH RISK: This operation can cause data loss. Only call after explicit user confirmation.
    public static async Task<HotSwapResult> SwapAsync(SystemDrive? drive, Action<string>? log = null)
    {
        var result = new HotSwapResult();

        // ================================================================
        // Step 1: Safety verification
        // ================================================================
        if (drive is null)
        {
            result.ErrorMessage = "BLOCKED: No drive specified for hot-swap.";
            log?.Invoke($"[ERROR] {result.ErrorMessage}");
            return result;
        }

        if (!CanHotSwap(drive))
        {
            result.ErrorMessage = drive.IsBoot
                ? "BLOCKED: Cannot hot-swap the boot device. A full reboot is required."
                : "BLOCKED: Drive is not eligible for hot-swap (not NVMe or missing PNP ID).";
            log?.Invoke($"[ERROR] {result.ErrorMessage}");
            return result;
        }

        log?.Invoke("========================================");
        log?.Invoke($"HOT-SWAP: Drive {drive.Number} - {drive.Name}");
        log?.Invoke("========================================");
        log?.Invoke("[WARNING] HIGH RISK OPERATION - Ensure no files are open on this drive");

        return await Task.Run(() =>
        {
            // Captured BEFORE dismount so we can restore letters deterministically after the
            // device returns. `mountvol X: /P` removes the mount point; Windows auto-mount
            // MAY restore the letter but it's not guaranteed — especially on systems where
            // the NoAutoMount policy is set (common on servers). Re-attaching by the volume's
            // GUID is the only robust path back.
            List<MountedVolume> volumesCaptured = [];
            List<MountedVolume> volumesToRestore = [];

            void TrackRemountOutcome(RemountSummary remount)
            {
                result.FailedRemountLetters = remount.Failed;
                if (remount.Failed.Count == 0)
                {
                    volumesToRestore.Clear();
                    return;
                }

                var failed = remount.Failed.ToHashSet(StringComparer.OrdinalIgnoreCase);
                volumesToRestore = volumesToRestore
                    .Where(v => failed.Contains(v.Letter))
                    .ToList();
            }

            void RestoreCapturedVolumes(string reason)
            {
                if (volumesToRestore.Count == 0)
                    return;

                log?.Invoke($"Attempting to restore drive letters after {reason}...");
                var remount = RemountVolumes(volumesToRestore, log);
                TrackRemountOutcome(remount);
                if (remount.Failed.Count == 0)
                    log?.Invoke("  [OK] All previously dismounted volumes were reattached");
                else
                {
                    log?.Invoke("[WARNING] Some volumes could not be reattached automatically:");
                    foreach (var letter in remount.Failed)
                        log?.Invoke($"  - {letter} (use Disk Management -> Online to restore manually)");
                }
            }

            try
            {
                // ================================================================
                // Step 2: Get volume mount points for this physical drive
                // ================================================================
                log?.Invoke("Step 1/4: Identifying mounted volumes...");
                var volumeCapture = GetVolumesForDrive(drive.Number);
                if (!volumeCapture.Succeeded)
                {
                    result.ErrorMessage = "ABORTED: Could not enumerate mounted volumes for this drive. Close Disk Management/storage tools and retry.";
                    if (!string.IsNullOrWhiteSpace(volumeCapture.ErrorMessage))
                        result.ErrorMessage += $" Details: {volumeCapture.ErrorMessage}";
                    log?.Invoke($"[ERROR] {result.ErrorMessage}");
                    return result;
                }
                volumesCaptured = volumeCapture.Volumes;

                if (volumesCaptured.Count > 0)
                {
                    log?.Invoke($"  Found {volumesCaptured.Count} volume(s): {string.Join(", ", volumesCaptured.Select(v => v.Letter))}");

                    // Flag BitLocker-protected volumes that won't auto-unlock after remount.
                    // The hot-swap doesn't lose data, but the drive comes back LOCKED and the
                    // user will need the recovery key or password to access it again. Surfacing
                    // this up front lets the user back out BEFORE we dismount rather than
                    // discover it post-swap.
                    var lockedAfterSwap = DescribeBitLockerRisk(volumesCaptured);
                    if (lockedAfterSwap.Count > 0)
                    {
                        result.BitLockerLockedLetters = lockedAfterSwap;
                        log?.Invoke("[WARNING] BitLocker-protected volume(s) detected:");
                        foreach (var letter in lockedAfterSwap)
                            log?.Invoke($"  - {letter} will require BitLocker unlock after the hot-swap");
                        log?.Invoke("[WARNING] Have the recovery key or password ready before continuing.");
                    }
                }
                else
                {
                    log?.Invoke("  No mounted volumes found (raw/unpartitioned drive)");
                }

                // ================================================================
                // Step 3: Dismount volumes
                // ================================================================
                if (volumesCaptured.Count > 0)
                {
                    log?.Invoke("Step 2/4: Dismounting volumes...");
                    bool allDismounted = true;
                    foreach (var vol in volumesCaptured)
                    {
                        if (DismountVolume(vol.Letter))
                        {
                            volumesToRestore.Add(vol);
                            log?.Invoke($"  [OK] Dismounted {vol.Letter}");
                        }
                        else
                        {
                            log?.Invoke($"  [FAIL] Could not dismount {vol.Letter} - open handles present");
                            allDismounted = false;
                        }
                    }
                    if (!allDismounted)
                    {
                        // Best-effort restore of any volumes we already dismounted so the user
                        // isn't left in a worse state than they started.
                        RestoreCapturedVolumes("partial dismount failure");
                        result.ErrorMessage = "ABORTED: One or more volumes could not be dismounted. Close all files on this drive and retry.";
                        log?.Invoke($"[ERROR] {result.ErrorMessage}");
                        return result;
                    }
                }
                else
                {
                    log?.Invoke("Step 2/4: No volumes to dismount (skipped)");
                }

                // ================================================================
                // Step 4: Re-enumerate device node to trigger driver reload
                // ================================================================
                log?.Invoke("Step 3/4: Re-enumerating device node...");

                string pnpId = drive.PNPDeviceID;
                uint cmResult = NativeMethods.CM_Locate_DevNode(
                    out uint devInst,
                    pnpId,
                    NativeMethods.CM_LOCATE_DEVNODE_NORMAL);

                if (cmResult != NativeMethods.CR_SUCCESS)
                {
                    // Try parent device (NVMe controller rather than disk)
                    string? parentPnpId = GetParentDeviceId(pnpId);
                    if (parentPnpId is not null)
                    {
                        log?.Invoke($"  Disk node not found, trying parent controller: {parentPnpId}");
                        cmResult = NativeMethods.CM_Locate_DevNode(
                            out devInst,
                            parentPnpId,
                            NativeMethods.CM_LOCATE_DEVNODE_NORMAL);
                    }
                }

                if (cmResult != NativeMethods.CR_SUCCESS)
                {
                    result.ErrorMessage = $"Failed to locate device node (CM error: 0x{cmResult:X8})";
                    log?.Invoke($"  [ERROR] {result.ErrorMessage}");
                    RestoreCapturedVolumes("device-node lookup failure");
                    return result;
                }

                cmResult = NativeMethods.CM_Reenumerate_DevNode(
                    devInst,
                    NativeMethods.CM_REENUMERATE_SYNCHRONOUS);

                if (cmResult != NativeMethods.CR_SUCCESS)
                {
                    result.ErrorMessage = $"Device re-enumeration failed (CM error: 0x{cmResult:X8})";
                    log?.Invoke($"  [ERROR] {result.ErrorMessage}");
                    RestoreCapturedVolumes("device re-enumeration failure");
                    return result;
                }

                log?.Invoke("  [OK] Device node re-enumeration initiated");

                // ================================================================
                // Step 5: Wait for device to return (up to 10 seconds)
                // ================================================================
                log?.Invoke("Step 4/4: Waiting for device to return...");
                bool deviceReturned = false;

                for (int i = 0; i < 20; i++) // 20 x 500ms = 10 seconds
                {
                    Thread.Sleep(500);

                    if (IsDrivePresent(drive.Number))
                    {
                        deviceReturned = true;
                        log?.Invoke($"  [OK] Drive {drive.Number} returned after {(i + 1) * 500}ms");
                        break;
                    }

                    if (i % 4 == 3) // Log every 2 seconds
                        log?.Invoke($"  Waiting... ({(i + 1) * 500}ms elapsed)");
                }

                if (deviceReturned)
                {
                    result.Success = true;
                    result.DeviceReturned = true;

                    // Explicitly reattach any letters we dismounted. Rely on the captured
                    // (letter, volume GUID) pairing rather than trusting auto-mount, which
                    // can be disabled system-wide (SAN hosts, servers, hardened workstations).
                    if (volumesToRestore.Count > 0)
                    {
                        // Partitions don't always materialize on the moment CM_Reenumerate_DevNode
                        // returns — give the storage stack a brief window to rediscover them.
                        // Without this delay, mountvol can race and fail with "volume not found".
                        Thread.Sleep(1500);
                        var remount = RemountVolumes(volumesToRestore, log);
                        TrackRemountOutcome(remount);
                        if (remount.Failed.Count > 0)
                        {
                            log?.Invoke("[WARNING] Some volumes could not be reattached automatically:");
                            foreach (var letter in remount.Failed)
                                log?.Invoke($"  - {letter} (use Disk Management -> Online to restore manually)");
                        }
                    }

                    log?.Invoke("========================================");
                    log?.Invoke("[SUCCESS] Hot-swap complete. Drive is back online with updated driver stack.");
                    EventLogService.Write($"NVMe hot-swap completed for Drive {drive.Number} ({drive.Name})");
                }
                else
                {
                    // ================================================================
                    // Rollback: Device did not return in time
                    // ================================================================
                    log?.Invoke("[WARNING] Device did not return within 10 seconds");
                    log?.Invoke("Attempting recovery re-enumeration...");

                    // Try one more re-enumeration
                    cmResult = NativeMethods.CM_Locate_DevNode(
                        out devInst,
                        pnpId,
                        NativeMethods.CM_LOCATE_DEVNODE_NORMAL);

                    if (cmResult == NativeMethods.CR_SUCCESS)
                    {
                        NativeMethods.CM_Reenumerate_DevNode(devInst, NativeMethods.CM_REENUMERATE_NORMAL);
                        Thread.Sleep(3000);

                        if (IsDrivePresent(drive.Number))
                        {
                            result.Success = true;
                            result.DeviceReturned = true;
                            result.RequiredRetry = true;
                            log?.Invoke("[OK] Device returned after recovery re-enumeration");
                            if (volumesCaptured.Count > 0)
                            {
                                Thread.Sleep(1500);
                                RestoreCapturedVolumes("recovery re-enumeration");
                            }
                        }
                    }

                    if (!result.DeviceReturned)
                    {
                        result.ErrorMessage = "Device did not return after hot-swap. A reboot may be required to restore the drive.";
                        log?.Invoke($"[ERROR] {result.ErrorMessage}");
                        RestoreCapturedVolumes("device-return timeout");
                        EventLogService.Write(
                            $"NVMe hot-swap FAILED for Drive {drive.Number} ({drive.Name}) - device did not return",
                            EventLogEntryType.Error, 3002);
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Hot-swap failed with exception: {ex.Message}";
                log?.Invoke($"[ERROR] {result.ErrorMessage}");
                RestoreCapturedVolumes("hot-swap exception");
                EventLogService.Write(
                    $"NVMe hot-swap exception for Drive {drive.Number}: {ex.Message}",
                    EventLogEntryType.Error, 3003);
            }

            return result;
        });
    }

    /// <summary>
    /// Record of a single mounted volume captured before dismount. The drive letter is what
    /// the user sees; the volume GUID path (e.g. <c>\\?\Volume{GUID}\</c>) is what survives
    /// re-enumeration and is the stable key we hand to <c>mountvol</c> when restoring the
    /// letter afterwards.
    /// </summary>
    private sealed record MountedVolume(string Letter, string VolumeGuidPath);

    private sealed class VolumeCaptureResult
    {
        public bool Succeeded { get; init; }
        public string? ErrorMessage { get; init; }
        public List<MountedVolume> Volumes { get; init; } = [];
    }

    /// <summary>Pairs the successful and failed re-attach results so the caller can
    /// surface unrecoverable volumes to the user without inferring them from counts.</summary>
    public sealed class RemountSummary
    {
        public List<string> Restored { get; } = [];
        public List<string> Failed { get; } = [];
    }

    /// <summary>
    /// Enumerates each partitioned volume on the given physical drive and captures both the
    /// drive letter and the volume GUID path. The GUID path is stable across re-enumeration
    /// and is what we use to reattach the letter after the hot-swap.
    /// </summary>
    private static VolumeCaptureResult GetVolumesForDrive(int driveNumber)
    {
        var volumes = new List<MountedVolume>();
        if (driveNumber < 0)
            return new VolumeCaptureResult
            {
                Succeeded = false,
                ErrorMessage = "Drive number was invalid."
            };

        try
        {
            using var partSearch = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\\\\\.\\\\PHYSICALDRIVE{driveNumber}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");

            using var partitions = partSearch.Get();
            foreach (var rawPart in partitions)
            {
                if (rawPart is not ManagementObject partition) continue;
                using (partition)
                {
                    string partId = partition["DeviceID"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(partId)) continue;

                    string escaped = EscapeWmiSingleQuotes(partId);
                    using var logicalSearch = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{escaped}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");

                    using var logicalCollection = logicalSearch.Get();
                    foreach (var rawLogical in logicalCollection)
                    {
                        if (rawLogical is not ManagementObject logical) continue;
                        using (logical)
                        {
                            string? letter = logical["DeviceID"]?.ToString();
                            if (string.IsNullOrEmpty(letter)) continue;

                            var guidPath = TryResolveVolumeGuid(letter);
                            // If a logical drive exists but we cannot resolve its stable GUID,
                            // abort the swap. Treating it as "no volume" could dismount a user's
                            // drive letter with no reliable way to put it back afterwards.
                            if (string.IsNullOrEmpty(guidPath))
                            {
                                return new VolumeCaptureResult
                                {
                                    Succeeded = false,
                                    ErrorMessage = $"Could not resolve stable volume GUID for {letter}."
                                };
                            }

                            volumes.Add(new MountedVolume(letter, guidPath));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new VolumeCaptureResult
            {
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }
        return new VolumeCaptureResult { Succeeded = true, Volumes = volumes };
    }

    /// <summary>
    /// Returns the drive letters (of the captured set) that have BitLocker protection
    /// turned on AND do NOT have auto-unlock enabled. Those are the volumes that will come
    /// back locked after the hot-swap and need the recovery key/password to access again.
    /// Volumes where BitLocker is off, fully decrypted, or auto-unlock-enabled are omitted
    /// because they'll remount seamlessly.
    /// </summary>
    private static List<string> DescribeBitLockerRisk(List<MountedVolume> volumes)
    {
        var atRisk = new List<string>();
        if (volumes is null || volumes.Count == 0) return atRisk;

        try
        {
            // Win32_EncryptableVolume lives in a non-default namespace that's queryable under admin.
            // It's also not guaranteed to be present (older SKUs without BitLocker feature) — so
            // any exception becomes "no risk detected" rather than blocking the swap.
            using var search = new System.Management.ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus, IsVolumeInitializedForProtection FROM Win32_EncryptableVolume");
            using var results = search.Get();
            foreach (var raw in results)
            {
                if (raw is not System.Management.ManagementObject vol) continue;
                using (vol)
                {
                    var letter = vol["DriveLetter"]?.ToString();
                    if (string.IsNullOrEmpty(letter)) continue;

                    // Only care about volumes the caller actually captured for this physical drive.
                    if (!volumes.Any(v => string.Equals(v.Letter, letter, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // ProtectionStatus: 0 = off, 1 = on, 2 = unknown. Only 1 is "encryption active".
                    int protectionStatus = 0;
                    try { protectionStatus = Convert.ToInt32(vol["ProtectionStatus"] ?? 0); } catch { }
                    if (protectionStatus != 1) continue;

                    // Auto-unlock state isn't on Win32_EncryptableVolume as a property; it's exposed
                    // via GetKeyProtectors(KeyProtectorType = 1 [ExternalKey]). In practice this is
                    // complex to query reliably across Windows versions, so we take the conservative
                    // stance: ANY protected volume that's not the system drive is flagged. System
                    // drive auto-unlocks itself; others require explicit setup most users haven't done.
                    atRisk.Add(letter);
                }
            }
        }
        catch
        {
            // BitLocker not installed / WMI namespace missing / access denied → no warning.
        }
        return atRisk;
    }

    /// <summary>
    /// Resolves a drive letter (e.g. <c>"D:"</c>) to its volume GUID path
    /// (e.g. <c>\\?\Volume{GUID}\</c>) via <c>Win32_Volume</c>. Returns null if the volume
    /// isn't resolvable — in which case we refuse to track it (see <see cref="GetVolumesForDrive"/>).
    /// </summary>
    private static string? TryResolveVolumeGuid(string driveLetter)
    {
        if (!IsSimpleDriveLetter(driveLetter))
            return null;
        try
        {
            // Win32_Volume reports DriveLetter as "D:" (no trailing slash), so we match that form.
            var escaped = EscapeWmiSingleQuotes(driveLetter);
            using var search = new ManagementObjectSearcher(
                $"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter='{escaped}'");
            using var results = search.Get();
            foreach (var raw in results)
            {
                if (raw is not ManagementObject vol) continue;
                using (vol)
                {
                    var deviceId = vol["DeviceID"]?.ToString();
                    if (!string.IsNullOrEmpty(deviceId)) return deviceId;
                }
            }
        }
        catch
        {
            // WMI can fail transiently; the caller treats a null as "no stable reattach key".
        }
        return null;
    }

    /// <summary>
    /// Reattaches each captured volume to its original drive letter. Skips volumes whose
    /// letter already came back via auto-mount. Retries each remount up to 3 times with
    /// a 1-second delay between attempts — `mountvol` can race the storage stack on slow
    /// controllers and fail the first try with "volume not found" even though the device
    /// is visibly back. Returns the split list of restored vs failed letters so the caller
    /// can surface any that need manual intervention.
    /// </summary>
    private static RemountSummary RemountVolumes(List<MountedVolume> volumes, Action<string>? log)
    {
        const int maxAttempts = 3;
        var summary = new RemountSummary();
        if (volumes is null || volumes.Count == 0) return summary;

        log?.Invoke("Reattaching drive letters...");
        foreach (var vol in volumes)
        {
            // If Windows auto-mount already restored the original letter to the original
            // volume we don't need to touch it — calling mountvol again would error out.
            if (IsLetterBoundToVolume(vol.Letter, vol.VolumeGuidPath))
            {
                log?.Invoke($"  [OK] {vol.Letter} was restored by auto-mount");
                summary.Restored.Add(vol.Letter);
                continue;
            }

            bool restored = false;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // Give the storage stack a moment between retries. Common failure signature
                // on slow controllers: first mountvol sees no volume GUID yet, second sees it.
                if (attempt > 1)
                {
                    Thread.Sleep(1000);
                    // Auto-mount sometimes lands between retries — re-check so we don't shout
                    // over a letter Windows already restored for us.
                    if (IsLetterBoundToVolume(vol.Letter, vol.VolumeGuidPath))
                    {
                        log?.Invoke($"  [OK] {vol.Letter} was restored by auto-mount (attempt {attempt})");
                        restored = true;
                        break;
                    }
                }

                if (Mount(vol.Letter, vol.VolumeGuidPath, log))
                {
                    log?.Invoke(attempt == 1
                        ? $"  [OK] Reattached {vol.Letter}"
                        : $"  [OK] Reattached {vol.Letter} (attempt {attempt}/{maxAttempts})");
                    restored = true;
                    break;
                }

                if (attempt < maxAttempts)
                    log?.Invoke($"  [RETRY] {vol.Letter} reattach attempt {attempt}/{maxAttempts} failed; retrying...");
            }

            if (restored) summary.Restored.Add(vol.Letter);
            else summary.Failed.Add(vol.Letter);
        }
        return summary;
    }

    /// <summary>
    /// Returns true when the given drive letter is currently bound to the given volume GUID.
    /// Used to avoid a redundant remount when Windows auto-mount beat us to it.
    /// </summary>
    private static bool IsLetterBoundToVolume(string driveLetter, string volumeGuidPath)
    {
        if (!IsSimpleDriveLetter(driveLetter) || string.IsNullOrEmpty(volumeGuidPath)) return false;
        try
        {
            var escaped = EscapeWmiSingleQuotes(driveLetter);
            using var search = new ManagementObjectSearcher(
                $"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter='{escaped}'");
            using var results = search.Get();
            foreach (var raw in results)
            {
                if (raw is not ManagementObject vol) continue;
                using (vol)
                {
                    var deviceId = vol["DeviceID"]?.ToString();
                    if (!string.IsNullOrEmpty(deviceId) &&
                        string.Equals(deviceId, volumeGuidPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Assigns a drive letter to a volume via <c>mountvol &lt;letter&gt;: &lt;VolumeGuidPath&gt;</c>.
    /// Returns true on success. Failures are logged and the caller adds the letter to the
    /// unrecoverable list so the user can see it.
    /// </summary>
    private static bool Mount(string driveLetter, string volumeGuidPath, Action<string>? log)
    {
        if (!IsSimpleDriveLetter(driveLetter))
            return false;
        // Volume GUID paths have a very specific shape; refuse anything that doesn't look
        // like one so a corrupted input can't smuggle arbitrary arguments into mountvol.
        if (string.IsNullOrEmpty(volumeGuidPath) ||
            !volumeGuidPath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
            !volumeGuidPath.EndsWith("}\\", StringComparison.Ordinal))
            return false;

        try
        {
            var psi = new ProcessStartInfo("mountvol")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // ArgumentList escapes per-token so neither the letter nor the GUID path can
            // collide with cmd.exe-style quoting.
            psi.ArgumentList.Add($"{driveLetter[0]}:");
            psi.ArgumentList.Add(volumeGuidPath);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                log?.Invoke($"  [FAIL] Could not start mountvol for {driveLetter}");
                return false;
            }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(true); } catch { }
                log?.Invoke($"  [FAIL] mountvol timed out reattaching {driveLetter}");
                return false;
            }
            var stderr = string.Empty;
            try { stdoutTask.GetAwaiter().GetResult(); } catch { }
            try { stderr = (stderrTask.GetAwaiter().GetResult() ?? string.Empty).Trim(); } catch { }
            if (proc.ExitCode != 0)
            {
                log?.Invoke($"  [FAIL] mountvol {driveLetter} -> {volumeGuidPath}: exit {proc.ExitCode}{(string.IsNullOrEmpty(stderr) ? "" : $" — {stderr}")}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"  [FAIL] mountvol exception for {driveLetter}: {ex.Message}");
            return false;
        }
    }

    private static string EscapeWmiSingleQuotes(string value)
    {
        // WMI WQL uses single-quote string literals; escape both backslashes and quotes.
        // Backslashes in WQL must be doubled because they are escape characters.
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'");
    }

    internal static bool IsSimpleDriveLetter(string? driveLetter) =>
        !string.IsNullOrWhiteSpace(driveLetter)
        && driveLetter.Length == 2
        && char.IsAsciiLetter(driveLetter[0])
        && driveLetter[1] == ':';

    /// <summary>
    /// Dismounts a volume by its drive letter using mountvol.
    /// </summary>
    private static bool DismountVolume(string driveLetter)
    {
        // Defensive: only allow simple drive letters like "C:" - never let arbitrary text be
        // appended to a process command line.
        if (!IsSimpleDriveLetter(driveLetter))
            return false;

        try
        {
            var psi = new ProcessStartInfo("mountvol")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add($"{char.ToUpperInvariant(driveLetter[0])}:\\");
            psi.ArgumentList.Add("/P");

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            // Drain stdout/stderr to avoid pipe-buffer deadlocks on slow machines.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(true); } catch { }
                return false;
            }
            try { stdoutTask.GetAwaiter().GetResult(); } catch { }
            try { stderrTask.GetAwaiter().GetResult(); } catch { }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a physical drive is currently present and accessible.
    /// </summary>
    private static bool IsDrivePresent(int driveNumber)
    {
        if (driveNumber < 0) return false;
        try
        {
            using var search = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT Number FROM MSFT_Disk WHERE Number={driveNumber}");

            using var collection = search.Get();
            int count = collection.Count;
            foreach (var obj in collection) (obj as IDisposable)?.Dispose();
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to find the parent device ID (NVMe controller) for a disk PNP device ID.
    /// </summary>
    private static string? GetParentDeviceId(string pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId)) return null;
        try
        {
            string escaped = EscapeWmiSingleQuotes(pnpDeviceId);

            // PNP device IDs for NVMe disks are typically under their controller
            // e.g., SCSI\DISK&VEN_NVME&PROD_... -> parent is the NVMe controller.
            using var search = new ManagementObjectSearcher(
                $"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID='{escaped}'");

            using var devices = search.Get();
            foreach (var rawDev in devices)
            {
                if (rawDev is not ManagementObject dev) continue;
                using (dev)
                {
                    using var parentSearch = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_PnPEntity.DeviceID='{escaped}'}} WHERE AssocClass=CIM_BusController");

                    using var parents = parentSearch.Get();
                    foreach (var rawParent in parents)
                    {
                        if (rawParent is not ManagementObject parent) continue;
                        using (parent)
                        {
                            var id = parent["DeviceID"]?.ToString();
                            if (!string.IsNullOrEmpty(id)) return id;
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort
        }
        return null;
    }
}

/// <summary>
/// Result of a hot-swap operation.
/// </summary>
public class HotSwapResult
{
    /// <summary>Whether the hot-swap completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Whether the device returned after re-enumeration.</summary>
    public bool DeviceReturned { get; set; }

    /// <summary>Whether a retry was needed to bring the device back.</summary>
    public bool RequiredRetry { get; set; }

    /// <summary>Error message if the operation failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Drive letters that were dismounted but could not be restored automatically.
    /// Empty on clean success. Non-empty means the user must open Disk Management to
    /// bring those volumes back online manually.</summary>
    public List<string> FailedRemountLetters { get; set; } = [];

    /// <summary>Drive letters whose volumes were BitLocker-protected at the time of the swap
    /// and will come back LOCKED after the remount — the user will need the recovery key or
    /// password to access them. Empty if no BitLocker risk was detected.</summary>
    public List<string> BitLockerLockedLetters { get; set; } = [];
}
