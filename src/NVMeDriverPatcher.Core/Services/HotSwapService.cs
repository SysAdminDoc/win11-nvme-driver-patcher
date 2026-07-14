using System.Diagnostics;
using System.Management;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// ============================================================================
// HIGH RISK: This service performs live driver hot-swap on NVMe devices.
// It dismounts volumes and restarts controller device state, which can cause data loss
// if used on a boot device or if volumes have open file handles.
// This should ONLY be used on non-boot NVMe drives with no actively-used volumes.
// ============================================================================

/// <summary>
/// Service for live NVMe driver hot-swap without requiring a full system reboot.
/// Only operates on non-boot NVMe drives. The transaction implementation lives in
/// HotSwapTransactionService.cs; this partial contains Windows volume primitives.
/// </summary>
public static partial class HotSwapService
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
    /// Record of a single mounted volume captured before dismount. The drive letter is what
    /// the user sees; the volume GUID path (e.g. <c>\\?\Volume{GUID}\</c>) is what survives
    /// controller state changes and is the stable key we hand to <c>mountvol</c> when restoring the
    /// letter afterwards.
    /// </summary>
    internal sealed record MountedVolume(string Letter, string VolumeGuidPath);

    internal sealed class VolumeCaptureResult
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
    /// drive letter and the volume GUID path. The GUID path is stable across controller restart
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
            // Batch WMI to stay inside the device-return window on slow-WMI systems.
            // Prefetch the full partition→logical-disk mapping in ONE query, then filter
            // locally — avoids per-partition ASSOCIATORS round trips that compound latency.
            var partToLogical = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (var mapSearch = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition"))
            using (var mapResults = WmiQueryHelper.ExecuteWithTimeout(mapSearch))
            {
                foreach (var rawMap in mapResults)
                {
                    if (rawMap is not ManagementObject map) continue;
                    using (map)
                    {
                        string? antecedent = map["Antecedent"]?.ToString();
                        string? dependent = map["Dependent"]?.ToString();
                        if (string.IsNullOrEmpty(antecedent) || string.IsNullOrEmpty(dependent)) continue;
                        string partKey = ExtractWmiPropertyValue(antecedent, "DeviceID");
                        string logicalKey = ExtractWmiPropertyValue(dependent, "DeviceID");
                        if (string.IsNullOrEmpty(partKey) || string.IsNullOrEmpty(logicalKey)) continue;
                        if (!partToLogical.TryGetValue(partKey, out var list))
                        {
                            list = new List<string>();
                            partToLogical[partKey] = list;
                        }
                        list.Add(logicalKey);
                    }
                }
            }

            // One ASSOCIATORS query to get partitions for this drive.
            using var partSearch = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\\\\\.\\\\PHYSICALDRIVE{driveNumber}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");

            using var partitions = WmiQueryHelper.ExecuteWithTimeout(partSearch);
            foreach (var rawPart in partitions)
            {
                if (rawPart is not ManagementObject partition) continue;
                using (partition)
                {
                    string partId = partition["DeviceID"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(partId)) continue;

                    if (!partToLogical.TryGetValue(partId, out var logicals)) continue;
                    foreach (string letter in logicals)
                    {
                        if (string.IsNullOrEmpty(letter)) continue;

                        var guidPath = TryResolveVolumeGuid(letter);
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

    private static string ExtractWmiPropertyValue(string wmiPath, string propertyName)
    {
        int idx = wmiPath.IndexOf($"{propertyName}=\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;
        int start = idx + propertyName.Length + 2;
        int end = wmiPath.IndexOf('"', start);
        return end > start ? wmiPath[start..end] : string.Empty;
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
            using var results = WmiQueryHelper.ExecuteWithTimeout(search);
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
            using var results = WmiQueryHelper.ExecuteWithTimeout(search);
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
            using var results = WmiQueryHelper.ExecuteWithTimeout(search);
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

            using var collection = WmiQueryHelper.ExecuteWithTimeout(search);
            int count = collection.Count;
            foreach (var obj in collection) (obj as IDisposable)?.Dispose();
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

}

/// <summary>
/// Result of a hot-swap operation.
/// </summary>
public class HotSwapResult
{
    /// <summary>Typed final state. Only <see cref="HotSwapOutcome.Succeeded"/> is safe to treat as complete.</summary>
    public HotSwapOutcome Outcome { get; set; } = HotSwapOutcome.Blocked;

    /// <summary>Whether the hot-swap completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Whether the physical disk returned after the controller state change.</summary>
    public bool DeviceReturned { get; set; }

    /// <summary>Whether a retry was needed to bring the device back.</summary>
    public bool RequiredRetry { get; set; }

    /// <summary>Whether SetupAPI or incomplete post-state proof requires a machine restart.</summary>
    public bool RebootRequired { get; set; }

    /// <summary>Native Win32/CONFIGRET error associated with the failed transition, when available.</summary>
    public int NativeError { get; set; }

    /// <summary>The exact parent controller device instance targeted by SetupAPI.</summary>
    public string ControllerDeviceId { get; set; } = string.Empty;

    /// <summary>Driver and service proof captured after the property state change.</summary>
    public bool DriverProofVerified { get; set; }
    public string ActiveDriver { get; set; } = string.Empty;
    public string DriverService { get; set; } = string.Empty;
    public string DriverServiceState { get; set; } = string.Empty;

    /// <summary>Original mounted letters captured before any flush or dismount.</summary>
    public List<string> CapturedVolumeLetters { get; set; } = [];

    /// <summary>True only when every dismounted volume GUID is verified on its original letter.</summary>
    public bool VolumeRestoreVerified { get; set; }

    /// <summary>Explicit operator recovery instruction for partial or failed transitions.</summary>
    public string RecoveryAction { get; set; } = string.Empty;

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
