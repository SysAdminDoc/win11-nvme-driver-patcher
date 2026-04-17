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
            try
            {
                // ================================================================
                // Step 2: Get volume mount points for this physical drive
                // ================================================================
                log?.Invoke("Step 1/4: Identifying mounted volumes...");
                var volumes = GetVolumesForDrive(drive.Number);

                if (volumes.Count > 0)
                {
                    log?.Invoke($"  Found {volumes.Count} volume(s): {string.Join(", ", volumes)}");
                }
                else
                {
                    log?.Invoke("  No mounted volumes found (raw/unpartitioned drive)");
                }

                // ================================================================
                // Step 3: Dismount volumes
                // ================================================================
                if (volumes.Count > 0)
                {
                    log?.Invoke("Step 2/4: Dismounting volumes...");
                    bool allDismounted = true;
                    foreach (string vol in volumes)
                    {
                        if (DismountVolume(vol))
                            log?.Invoke($"  [OK] Dismounted {vol}");
                        else
                        {
                            log?.Invoke($"  [FAIL] Could not dismount {vol} - open handles present");
                            allDismounted = false;
                        }
                    }
                    if (!allDismounted)
                    {
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
                    return result;
                }

                cmResult = NativeMethods.CM_Reenumerate_DevNode(
                    devInst,
                    NativeMethods.CM_REENUMERATE_SYNCHRONOUS);

                if (cmResult != NativeMethods.CR_SUCCESS)
                {
                    result.ErrorMessage = $"Device re-enumeration failed (CM error: 0x{cmResult:X8})";
                    log?.Invoke($"  [ERROR] {result.ErrorMessage}");
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
                    log?.Invoke("========================================");
                    log?.Invoke("[SUCCESS] Hot-swap complete. Drive is back online with updated driver stack.");
                    log?.Invoke("[INFO] Volumes will auto-remount. Verify drive access before resuming work.");
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
                        }
                    }

                    if (!result.DeviceReturned)
                    {
                        result.ErrorMessage = "Device did not return after hot-swap. A reboot may be required to restore the drive.";
                        log?.Invoke($"[ERROR] {result.ErrorMessage}");
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
                EventLogService.Write(
                    $"NVMe hot-swap exception for Drive {drive.Number}: {ex.Message}",
                    EventLogEntryType.Error, 3003);
            }

            return result;
        });
    }

    /// <summary>
    /// Gets the volume drive letters mounted from a specific physical drive number.
    /// Uses WMI to map PhysicalDrive -> Partitions -> LogicalDisks.
    /// </summary>
    private static List<string> GetVolumesForDrive(int driveNumber)
    {
        var volumes = new List<string>();
        if (driveNumber < 0)
            return volumes;

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
                            if (!string.IsNullOrEmpty(letter))
                                volumes.Add(letter);
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort
        }
        return volumes;
    }

    private static string EscapeWmiSingleQuotes(string value)
    {
        // WMI WQL uses single-quote string literals; escape both backslashes and quotes.
        // Backslashes in WQL must be doubled because they are escape characters.
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'");
    }

    /// <summary>
    /// Dismounts a volume by its drive letter using mountvol.
    /// </summary>
    private static bool DismountVolume(string driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            return false;
        // Defensive: only allow simple drive letters like "C:" — never let arbitrary text be
        // appended to a process command line.
        if (driveLetter.Length < 2 || driveLetter[1] != ':' || !char.IsLetter(driveLetter[0]))
            return false;

        try
        {
            var psi = new ProcessStartInfo("mountvol", $"{driveLetter[0]}:\\ /P")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

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
}
