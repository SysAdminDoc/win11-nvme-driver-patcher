using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NVMeDriverPatcher.Interop;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static partial class HotSwapService
{
    internal sealed record PlatformOperation(bool Success, int NativeError, string Summary)
    {
        public static PlatformOperation Passed(string summary) => new(true, 0, summary);
        public static PlatformOperation Failed(string summary, int nativeError = 0) =>
            new(false, nativeError, summary);
    }

    internal sealed record DeviceStateChange(bool Success, bool RebootRequired, int NativeError, string Summary)
    {
        public static DeviceStateChange Failed(string summary, int nativeError = 0) =>
            new(false, false, nativeError, summary);
    }

    internal sealed record DriverProof(
        bool Success,
        string DriverName,
        string ServiceName,
        string ServiceState,
        string Summary)
    {
        public static DriverProof Failed(string summary) => new(false, string.Empty, string.Empty, string.Empty, summary);
    }

    internal interface IHotSwapPlatform
    {
        VolumeCaptureResult GetVolumesForDrive(int driveNumber);
        List<string> DescribeBitLockerRisk(List<MountedVolume> volumes);
        string? ResolveControllerDeviceId(string diskDeviceId);
        PlatformOperation FlushVolume(MountedVolume volume);
        PlatformOperation DismountVolume(MountedVolume volume);
        DeviceStateChange RequestControllerStateChange(string controllerDeviceId);
        bool IsDrivePresent(int driveNumber);
        DriverProof ProbeController(string controllerDeviceId);
        RemountSummary RemountVolumes(List<MountedVolume> volumes, Action<string>? log);
        void Delay(TimeSpan duration);
        void WriteEvent(string message, EventLogEntryType entryType, int eventId);
    }

    /// <summary>
    /// Runs a fail-closed device-state transaction. A successful SetupAPI call is only an
    /// attempted transition; success is declared later, after independent device, driver,
    /// service, and volume-mount proof.
    /// </summary>
    public static Task<HotSwapResult> SwapAsync(SystemDrive? drive, Action<string>? log = null) =>
        SwapAsync(drive, new WindowsHotSwapPlatform(), log);

    internal static Task<HotSwapResult> SwapAsync(
        SystemDrive? drive,
        IHotSwapPlatform platform,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return Task.Run(() => SwapCore(drive, platform, log));
    }

    private static HotSwapResult SwapCore(
        SystemDrive? drive,
        IHotSwapPlatform platform,
        Action<string>? log)
    {
        var result = new HotSwapResult { Outcome = HotSwapOutcome.Blocked };
        var recoverySafety = RecoverySafetyGateService.Snapshot();
        if (!recoverySafety.MutationAllowed)
        {
            result.ErrorMessage = "BLOCKED by unresolved startup recovery: " + recoverySafety.Summary;
            log?.Invoke("[ERROR] " + result.ErrorMessage);
            return result;
        }
        if (drive is null)
            return Fail("BLOCKED: No drive specified for hot-swap.");
        if (!CanHotSwap(drive))
            return Fail(drive.IsBoot
                ? "BLOCKED: Cannot hot-swap the boot device. A full reboot is required."
                : "BLOCKED: Drive is not eligible for hot-swap (not NVMe or missing PNP ID).");

        log?.Invoke("========================================");
        log?.Invoke($"VERIFIED HOT-SWAP: Drive {drive.Number} - {drive.Name}");
        log?.Invoke("========================================");
        log?.Invoke("[WARNING] HIGH RISK OPERATION - Ensure no files are open on this drive");

        var volumesToRestore = new List<MountedVolume>();
        var controllerChangeAttempted = false;

        HotSwapResult Fail(string message)
        {
            result.ErrorMessage = message;
            log?.Invoke($"[ERROR] {message}");
            return result;
        }

        bool RestoreDismountedVolumes(string reason)
        {
            if (volumesToRestore.Count == 0)
            {
                result.VolumeRestoreVerified = true;
                return true;
            }

            log?.Invoke($"Reattaching every captured volume after {reason}...");
            var remount = platform.RemountVolumes(volumesToRestore, log);
            var restored = remount.Restored.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failed = remount.Failed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var volume in volumesToRestore)
                if (!restored.Contains(volume.Letter)) failed.Add(volume.Letter);

            result.FailedRemountLetters = failed.Order(StringComparer.OrdinalIgnoreCase).ToList();
            result.VolumeRestoreVerified = failed.Count == 0;
            if (result.VolumeRestoreVerified)
            {
                volumesToRestore.Clear();
                log?.Invoke("  [OK] Every captured volume GUID is back on its original drive letter");
                return true;
            }

            log?.Invoke("[WARNING] Volume restoration is incomplete:");
            foreach (var letter in result.FailedRemountLetters)
                log?.Invoke($"  - {letter} (reboot, then use Disk Management if it remains offline)");
            return false;
        }

        try
        {
            log?.Invoke("Step 1/5: Capturing mounted volumes and controller identity...");
            var volumeCapture = platform.GetVolumesForDrive(drive.Number);
            if (!volumeCapture.Succeeded)
            {
                result.Outcome = HotSwapOutcome.Failed;
                var detail = string.IsNullOrWhiteSpace(volumeCapture.ErrorMessage)
                    ? string.Empty
                    : $" Details: {volumeCapture.ErrorMessage}";
                return Fail($"ABORTED: Could not enumerate mounted volumes for this drive.{detail}");
            }

            var volumesCaptured = volumeCapture.Volumes;
            result.CapturedVolumeLetters = volumesCaptured.Select(v => v.Letter).ToList();
            // No volume has been dismounted yet, so the original mount state is still intact.
            result.VolumeRestoreVerified = true;
            log?.Invoke(volumesCaptured.Count == 0
                ? "  No mounted volumes found (raw/unpartitioned drive)"
                : $"  Captured {volumesCaptured.Count} volume(s): {string.Join(", ", result.CapturedVolumeLetters)}");

            var controllerDeviceId = platform.ResolveControllerDeviceId(drive.PNPDeviceID);
            if (string.IsNullOrWhiteSpace(controllerDeviceId))
            {
                result.Outcome = HotSwapOutcome.Failed;
                return Fail("ABORTED: Could not resolve the target disk's parent NVMe controller before dismount.");
            }
            result.ControllerDeviceId = controllerDeviceId;
            log?.Invoke($"  Controller: {controllerDeviceId}");

            result.BitLockerLockedLetters = platform.DescribeBitLockerRisk(volumesCaptured);
            foreach (var letter in result.BitLockerLockedLetters)
                log?.Invoke($"[WARNING] {letter} is BitLocker-protected and may require unlock after restart");

            log?.Invoke("Step 2/5: Flushing every captured volume...");
            foreach (var volume in volumesCaptured)
            {
                var flush = platform.FlushVolume(volume);
                if (!flush.Success)
                {
                    result.Outcome = HotSwapOutcome.Failed;
                    result.NativeError = flush.NativeError;
                    return Fail($"ABORTED before dismount: FlushFileBuffers failed for {volume.Letter}. {flush.Summary}");
                }
                log?.Invoke($"  [OK] Durable volume flush confirmed for {volume.Letter}");
            }

            log?.Invoke("Step 3/5: Dismounting captured volumes...");
            foreach (var volume in volumesCaptured)
            {
                var dismount = platform.DismountVolume(volume);
                if (!dismount.Success)
                {
                    result.NativeError = dismount.NativeError;
                    var restored = RestoreDismountedVolumes("partial dismount failure");
                    result.Outcome = restored ? HotSwapOutcome.Failed : HotSwapOutcome.Partial;
                    result.RebootRequired = !restored;
                    result.RecoveryAction = restored
                        ? "Close every handle on the target volumes and retry."
                        : "Reboot now; then verify every listed drive letter in Disk Management.";
                    return Fail($"ABORTED: Could not dismount {volume.Letter}. {dismount.Summary}");
                }
                volumesToRestore.Add(volume);
                result.VolumeRestoreVerified = false;
                log?.Invoke($"  [OK] Dismounted {volume.Letter}");
            }

            log?.Invoke("Step 4/5: Requesting documented controller property-state change...");
            controllerChangeAttempted = true;
            var stateChange = platform.RequestControllerStateChange(controllerDeviceId);
            result.NativeError = stateChange.NativeError;
            if (!stateChange.Success)
            {
                var restored = RestoreDismountedVolumes("SetupAPI state-change failure");
                result.Outcome = restored ? HotSwapOutcome.Failed : HotSwapOutcome.Partial;
                result.RebootRequired = true;
                result.RecoveryAction = "Reboot now to restore a known controller and volume state.";
                result.ErrorMessage = $"Controller state change failed. {stateChange.Summary} Reboot required.";
                log?.Invoke($"[ERROR] {result.ErrorMessage}");
                platform.WriteEvent(
                    $"NVMe hot-swap FAILED for Drive {drive.Number}: {result.ErrorMessage}",
                    EventLogEntryType.Error, 3002);
                return result;
            }
            result.RebootRequired = stateChange.RebootRequired;
            log?.Invoke(stateChange.RebootRequired
                ? "  [WARNING] SetupAPI accepted the change but set DI_NEEDRESTART/DI_NEEDREBOOT"
                : "  [OK] SetupAPI controller state change completed without a restart flag");

            log?.Invoke("Step 5/5: Proving controller driver/service and restoring volumes...");
            var proof = DriverProof.Failed("Controller proof has not completed.");
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                platform.Delay(TimeSpan.FromMilliseconds(500));
                result.DeviceReturned = platform.IsDrivePresent(drive.Number);
                proof = platform.ProbeController(controllerDeviceId);
                if (result.DeviceReturned && proof.Success)
                {
                    log?.Invoke($"  [OK] Controller proof passed after {attempt * 500}ms: {proof.Summary}");
                    break;
                }
                if (attempt % 4 == 0)
                    log?.Invoke($"  Waiting for active driver proof... ({attempt * 500}ms elapsed; {proof.Summary})");
            }

            result.DriverProofVerified = proof.Success;
            result.ActiveDriver = proof.DriverName;
            result.DriverService = proof.ServiceName;
            result.DriverServiceState = proof.ServiceState;
            if (volumesToRestore.Count > 0) platform.Delay(TimeSpan.FromMilliseconds(1500));
            var allVolumesRestored = RestoreDismountedVolumes("controller state change");

            if (stateChange.RebootRequired || !result.DeviceReturned || !proof.Success || !allVolumesRestored)
            {
                result.Success = false;
                result.Outcome = result.DeviceReturned ? HotSwapOutcome.Partial : HotSwapOutcome.Failed;
                result.RebootRequired = true;
                result.RecoveryAction =
                    "Reboot now, then verify the controller driver and every captured drive letter before using the disk.";
                var reasons = new List<string>();
                if (stateChange.RebootRequired) reasons.Add("SetupAPI requested restart");
                if (!result.DeviceReturned) reasons.Add("physical disk did not return");
                if (!proof.Success) reasons.Add($"controller proof failed: {proof.Summary}");
                if (!allVolumesRestored)
                    reasons.Add($"unrestored volumes: {string.Join(", ", result.FailedRemountLetters)}");
                result.ErrorMessage = $"Hot-swap was not verified ({string.Join("; ", reasons)}). Reboot required.";
                log?.Invoke($"[ERROR] {result.ErrorMessage}");
                platform.WriteEvent(
                    $"NVMe hot-swap UNVERIFIED for Drive {drive.Number} ({drive.Name}): {string.Join("; ", reasons)}",
                    EventLogEntryType.Error, 3002);
                return result;
            }

            result.Success = true;
            result.Outcome = HotSwapOutcome.Succeeded;
            result.ErrorMessage = null;
            result.RecoveryAction = string.Empty;
            log?.Invoke("========================================");
            log?.Invoke(
                $"[SUCCESS] Verified hot-swap complete: {proof.DriverName} / {proof.ServiceName} running; all volumes restored.");
            platform.WriteEvent(
                $"Verified NVMe hot-swap completed for Drive {drive.Number} ({drive.Name}); " +
                $"driver={proof.DriverName}, service={proof.ServiceName}, volumes={string.Join(",", result.CapturedVolumeLetters)}",
                EventLogEntryType.Information, 3001);
            return result;
        }
        catch (Exception ex)
        {
            var restored = RestoreDismountedVolumes("hot-swap exception");
            result.Success = false;
            result.Outcome = restored ? HotSwapOutcome.Failed : HotSwapOutcome.Partial;
            result.RebootRequired = controllerChangeAttempted || volumesToRestore.Count > 0 || !restored;
            result.RecoveryAction = result.RebootRequired
                ? "Reboot now, then verify the controller and all captured volumes."
                : "No device mutation was confirmed; review the error and retry only after correction.";
            result.ErrorMessage = $"Hot-swap failed with exception: {ex.Message}";
            log?.Invoke($"[ERROR] {result.ErrorMessage}");
            platform.WriteEvent(
                $"NVMe hot-swap exception for Drive {drive.Number}: {ex.Message}",
                EventLogEntryType.Error, 3003);
            return result;
        }
    }

    internal static bool InstallFlagsRequireReboot(uint flags) =>
        (flags & (NativeMethods.DI_NEEDRESTART | NativeMethods.DI_NEEDREBOOT)) != 0;

    private sealed class WindowsHotSwapPlatform : IHotSwapPlatform
    {
        public VolumeCaptureResult GetVolumesForDrive(int driveNumber) =>
            HotSwapService.GetVolumesForDrive(driveNumber);

        public List<string> DescribeBitLockerRisk(List<MountedVolume> volumes) =>
            HotSwapService.DescribeBitLockerRisk(volumes);

        public unsafe string? ResolveControllerDeviceId(string diskDeviceId)
        {
            if (string.IsNullOrWhiteSpace(diskDeviceId)) return null;
            var locate = NativeMethods.CM_Locate_DevNode(
                out var diskDevInst, diskDeviceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);
            if (locate != NativeMethods.CR_SUCCESS) return null;
            var parentResult = NativeMethods.CM_Get_Parent(out var parentDevInst, diskDevInst, 0);
            if (parentResult != NativeMethods.CR_SUCCESS) return null;
            var sizeResult = NativeMethods.CM_Get_Device_ID_Size(out var length, parentDevInst, 0);
            if (sizeResult != NativeMethods.CR_SUCCESS || length == 0 || length > 4096) return null;

            var buffer = new char[length + 1];
            fixed (char* pointer = buffer)
            {
                var idResult = NativeMethods.CM_Get_Device_ID(parentDevInst, pointer, (uint)buffer.Length, 0);
                return idResult == NativeMethods.CR_SUCCESS ? new string(buffer, 0, (int)length) : null;
            }
        }

        public PlatformOperation FlushVolume(MountedVolume volume)
        {
            if (!IsSimpleDriveLetter(volume.Letter))
                return PlatformOperation.Failed($"Invalid drive letter '{volume.Letter}'.");
            try
            {
                using var handle = NativeMethods.CreateFile(
                    $@"\\.\{char.ToUpperInvariant(volume.Letter[0])}:",
                    NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    return PlatformOperation.Failed($"CreateFile(volume) failed with Win32 error {error}.", error);
                }
                if (!NativeMethods.FlushFileBuffers(handle))
                {
                    var error = Marshal.GetLastPInvokeError();
                    return PlatformOperation.Failed($"FlushFileBuffers failed with Win32 error {error}.", error);
                }
                return PlatformOperation.Passed("FlushFileBuffers returned success.");
            }
            catch (Exception ex)
            {
                return PlatformOperation.Failed(ex.Message);
            }
        }

        public PlatformOperation DismountVolume(MountedVolume volume) =>
            HotSwapService.DismountVolume(volume.Letter)
                ? PlatformOperation.Passed("mountvol /P completed.")
                : PlatformOperation.Failed("mountvol /P failed or timed out.");

        public DeviceStateChange RequestControllerStateChange(string controllerDeviceId)
        {
            var locate = NativeMethods.CM_Locate_DevNode(
                out var targetDevInst, controllerDeviceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);
            if (locate != NativeMethods.CR_SUCCESS)
                return DeviceStateChange.Failed($"CM_Locate_DevNode failed with CONFIGRET 0x{locate:X8}.", unchecked((int)locate));

            using var deviceSet = NativeMethods.SetupDiGetClassDevsAllClasses(
                IntPtr.Zero, null, IntPtr.Zero, NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES);
            if (deviceSet.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                return DeviceStateChange.Failed($"SetupDiGetClassDevs failed with Win32 error {error}.", error);
            }

            var device = NativeMethods.SP_DEVINFO_DATA.Create();
            var found = false;
            for (uint index = 0; NativeMethods.SetupDiEnumDeviceInfo(deviceSet, index, ref device); index++)
            {
                if (device.DevInst != targetDevInst) continue;
                found = true;
                break;
            }
            if (!found)
                return DeviceStateChange.Failed("The target controller is not in the present SetupAPI device set.", 1168);

            var change = new NativeMethods.SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = NativeMethods.SP_CLASSINSTALL_HEADER.Create(NativeMethods.DIF_PROPERTYCHANGE),
                StateChange = NativeMethods.DICS_PROPCHANGE,
                Scope = NativeMethods.DICS_FLAG_GLOBAL,
                HwProfile = 0
            };
            if (!NativeMethods.SetupDiSetClassInstallParams(
                    deviceSet, ref device, ref change, (uint)Marshal.SizeOf<NativeMethods.SP_PROPCHANGE_PARAMS>()))
            {
                var error = Marshal.GetLastPInvokeError();
                return DeviceStateChange.Failed($"SetupDiSetClassInstallParams failed with Win32 error {error}.", error);
            }
            if (!NativeMethods.SetupDiCallClassInstaller(NativeMethods.DIF_PROPERTYCHANGE, deviceSet, ref device))
            {
                var error = Marshal.GetLastPInvokeError();
                return DeviceStateChange.Failed($"DIF_PROPERTYCHANGE failed with Win32 error {error}.", error);
            }

            var installParams = NativeMethods.SP_DEVINSTALL_PARAMS.Create();
            if (!NativeMethods.SetupDiGetDeviceInstallParams(deviceSet, ref device, ref installParams))
            {
                var error = Marshal.GetLastPInvokeError();
                return DeviceStateChange.Failed(
                    $"SetupDiGetDeviceInstallParams failed after the state change with Win32 error {error}.", error);
            }
            var reboot = InstallFlagsRequireReboot(installParams.Flags);
            return new DeviceStateChange(
                true,
                reboot,
                0,
                reboot
                    ? $"State change completed; install flags 0x{installParams.Flags:X8} require restart."
                    : $"State change completed; install flags 0x{installParams.Flags:X8} require no restart.");
        }

        public bool IsDrivePresent(int driveNumber) => HotSwapService.IsDrivePresent(driveNumber);

        public DriverProof ProbeController(string controllerDeviceId)
        {
            try
            {
                var escapedDevice = EscapeWmiSingleQuotes(controllerDeviceId);
                var pnpOk = false;
                using (var pnpSearch = new ManagementObjectSearcher(
                           $"SELECT Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID='{escapedDevice}'"))
                using (var pnpResults = WmiQueryHelper.ExecuteWithTimeout(pnpSearch))
                {
                    foreach (var raw in pnpResults)
                    {
                        if (raw is not ManagementObject device) continue;
                        using (device)
                        {
                            var status = device["Status"]?.ToString();
                            var code = Convert.ToUInt32(device["ConfigManagerErrorCode"] ?? uint.MaxValue);
                            pnpOk = status?.Equals("OK", StringComparison.OrdinalIgnoreCase) == true && code == 0;
                        }
                    }
                }

                var driverName = string.Empty;
                using (var driverSearch = new ManagementObjectSearcher(
                           $"SELECT DriverName FROM Win32_PnPSignedDriver WHERE DeviceID='{escapedDevice}'"))
                using (var driverResults = WmiQueryHelper.ExecuteWithTimeout(driverSearch))
                {
                    foreach (var raw in driverResults)
                    {
                        if (raw is not ManagementObject driver) continue;
                        using (driver) driverName = driver["DriverName"]?.ToString() ?? string.Empty;
                    }
                }

                var enumPath = @"SYSTEM\CurrentControlSet\Enum\" + controllerDeviceId;
                using var enumKey = Registry.LocalMachine.OpenSubKey(enumPath, writable: false);
                var serviceName = enumKey?.GetValue("Service")?.ToString() ?? string.Empty;
                var serviceState = string.Empty;
                if (!string.IsNullOrWhiteSpace(serviceName))
                {
                    var escapedService = EscapeWmiSingleQuotes(serviceName);
                    using var serviceSearch = new ManagementObjectSearcher(
                        $"SELECT State FROM Win32_SystemDriver WHERE Name='{escapedService}'");
                    using var serviceResults = WmiQueryHelper.ExecuteWithTimeout(serviceSearch);
                    foreach (var raw in serviceResults)
                    {
                        if (raw is not ManagementObject service) continue;
                        using (service) serviceState = service["State"]?.ToString() ?? string.Empty;
                    }
                }

                var success = pnpOk && !string.IsNullOrWhiteSpace(driverName) &&
                    !string.IsNullOrWhiteSpace(serviceName) && serviceState.Equals("Running", StringComparison.OrdinalIgnoreCase);
                var summary = success
                    ? $"PnP status OK; driver={driverName}; service={serviceName} ({serviceState})."
                    : $"PnP OK={pnpOk}; driver={Blank(driverName)}; service={Blank(serviceName)} ({Blank(serviceState)}).";
                return new DriverProof(success, driverName, serviceName, serviceState, summary);
            }
            catch (Exception ex)
            {
                return DriverProof.Failed($"Controller proof unavailable: {ex.Message}");
            }
        }

        public RemountSummary RemountVolumes(List<MountedVolume> volumes, Action<string>? log) =>
            HotSwapService.RemountVolumes(volumes, log);

        public void Delay(TimeSpan duration) => Thread.Sleep(duration);

        public void WriteEvent(string message, EventLogEntryType entryType, int eventId) =>
            EventLogService.Write(message, entryType, eventId);

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "(missing)" : value;
    }
}

public enum HotSwapOutcome
{
    Blocked,
    Failed,
    Partial,
    Succeeded
}
