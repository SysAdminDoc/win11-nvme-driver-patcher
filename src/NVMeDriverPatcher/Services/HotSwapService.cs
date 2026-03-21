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
    public static async Task<HotSwapResult> SwapAsync(SystemDrive drive, Action<string>? log = null)
    {
        var result = new HotSwapResult();

        // ================================================================
        // Step 1: Safety verification
        // ================================================================
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
                    foreach (string vol in volumes)
                    {
                        if (DismountVolume(vol))
                            log?.Invoke($"  [OK] Dismounted {vol}");
                        else
                            log?.Invoke($"  [WARNING] Could not dismount {vol} - may have open handles");
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
        try
        {
            using var partSearch = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\\\\\.\\\\PHYSICALDRIVE{driveNumber}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");

            foreach (ManagementObject partition in partSearch.Get())
            {
                string partId = partition["DeviceID"]?.ToString() ?? "";
                using var logicalSearch = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");

                foreach (ManagementObject logical in logicalSearch.Get())
                {
                    string? letter = logical["DeviceID"]?.ToString();
                    if (!string.IsNullOrEmpty(letter))
                        volumes.Add(letter);
                }
            }
        }
        catch
        {
            // Best effort
        }
        return volumes;
    }

    /// <summary>
    /// Dismounts a volume by its drive letter using mountvol.
    /// </summary>
    private static bool DismountVolume(string driveLetter)
    {
        try
        {
            // Use mountvol to dismount
            var psi = new ProcessStartInfo("mountvol", $"{driveLetter}\\ /P")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
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
        try
        {
            using var search = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT Number FROM MSFT_Disk WHERE Number={driveNumber}");

            return search.Get().Count > 0;
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
        try
        {
            // PNP device IDs for NVMe disks are typically under their controller
            // e.g., SCSI\DISK&VEN_NVME&PROD_... -> parent is the NVMe controller
            using var search = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_PnPEntity WHERE DeviceID='{pnpDeviceId.Replace("\\", "\\\\")}'");

            foreach (ManagementObject dev in search.Get())
            {
                // Get parent via ASSOCIATORS
                using var parentSearch = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_PnPEntity.DeviceID='{pnpDeviceId.Replace("\\", "\\\\")}'}} WHERE AssocClass=CIM_BusController");

                foreach (ManagementObject parent in parentSearch.Get())
                {
                    return parent["DeviceID"]?.ToString();
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
