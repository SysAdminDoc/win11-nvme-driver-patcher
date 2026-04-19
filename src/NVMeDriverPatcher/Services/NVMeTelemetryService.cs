using System.Runtime.InteropServices;
using System.Numerics;
using Microsoft.Win32.SafeHandles;
using NVMeDriverPatcher.Interop;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>
/// Service for reading NVMe SMART / Health telemetry data via direct DeviceIoControl calls.
/// Uses IOCTL_STORAGE_QUERY_PROPERTY with ProtocolSpecificProperty to retrieve Log Page 02h.
/// </summary>
public static class NVMeTelemetryService
{
    /// <summary>
    /// Opens a read-only handle to \\.\PhysicalDriveN for IOCTL operations.
    /// </summary>
    /// <param name="driveNumber">Physical drive number (0, 1, 2, ...).</param>
    /// <returns>A SafeFileHandle to the drive, or null if the drive cannot be opened.</returns>
    public static SafeFileHandle? OpenDriveHandle(int driveNumber)
    {
        if (driveNumber < 0)
            return null;

        try
        {
            string path = $@"\\.\PhysicalDrive{driveNumber}";
            var handle = NativeMethods.CreateFile(
                path,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                // SafeFileHandle on an invalid handle still wraps the INVALID_HANDLE_VALUE
                // sentinel — we don't want to leak the wrapper to the GC finalizer.
                handle.Dispose();
                return null;
            }

            return handle;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the NVMe SMART / Health Information Log (Log Page 02h) from an open drive handle.
    /// </summary>
    /// <param name="handle">Open SafeFileHandle to a physical drive.</param>
    /// <returns>Raw 512-byte log page data, or null if the query fails or is unsupported.</returns>
    public static byte[]? ReadSmartLog(SafeFileHandle handle)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed)
            return null;

        try
        {
            // Build input buffer
            byte[] inputBuffer = StorageStructs.BuildSmartLogQueryBuffer();

            // Output buffer: descriptor header + 512 bytes of NVMe health data
            int descriptorSize = Marshal.SizeOf<StorageStructs.STORAGE_PROTOCOL_DATA_DESCRIPTOR>();
            int outputSize = descriptorSize + 512;
            byte[] outputBuffer = new byte[outputSize];

            bool success = NativeMethods.DeviceIoControl(
                handle,
                StorageStructs.IOCTL_STORAGE_QUERY_PROPERTY,
                ref inputBuffer[0],
                (uint)inputBuffer.Length,
                ref outputBuffer[0],
                (uint)outputBuffer.Length,
                out uint bytesReturned,
                IntPtr.Zero);

            if (!success || bytesReturned < descriptorSize)
                return null;

            // Extract the SMART data from after the descriptor header
            GCHandle gcHandle = GCHandle.Alloc(outputBuffer, GCHandleType.Pinned);
            try
            {
                var descriptor = Marshal.PtrToStructure<StorageStructs.STORAGE_PROTOCOL_DATA_DESCRIPTOR>(
                    gcHandle.AddrOfPinnedObject());

                int dataOffset = descriptor.ProtocolSpecificData.ProtocolDataOffset;
                int dataLength = descriptor.ProtocolSpecificData.ProtocolDataLength;

                if (dataOffset < 0 || dataLength < 512)
                    return null;

                // Data offset is relative to the start of ProtocolSpecificData within the descriptor.
                // Use Marshal.OffsetOf instead of a hard-coded "8" so we stay correct if the layout
                // ever changes (e.g. struct alignment differences on a future runtime).
                int protocolSpecificStart = (int)Marshal.OffsetOf<StorageStructs.STORAGE_PROTOCOL_DATA_DESCRIPTOR>(
                    nameof(StorageStructs.STORAGE_PROTOCOL_DATA_DESCRIPTOR.ProtocolSpecificData));
                int absoluteOffset = protocolSpecificStart + dataOffset;
                if (absoluteOffset < 0 || absoluteOffset + 512 > outputBuffer.Length)
                    return null;

                byte[] smartData = new byte[512];
                Buffer.BlockCopy(outputBuffer, absoluteOffset, smartData, 0, 512);
                return smartData;
            }
            finally
            {
                gcHandle.Free();
            }
        }
        catch
        {
            // Graceful degradation: unsupported controller or insufficient privileges
            return null;
        }
    }

    /// <summary>
    /// Parses a raw 512-byte NVMe SMART / Health Information Log into a typed model.
    /// </summary>
    /// <param name="raw">Raw 512-byte log page data.</param>
    /// <returns>Parsed NVMeHealthData, or null if the data is invalid.</returns>
    public static NVMeHealthData? ParseHealthInfoLog(byte[] raw)
    {
        if (raw is null || raw.Length < 512)
            return null;

        try
        {
            GCHandle handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
            StorageStructs.NVME_HEALTH_INFO_LOG log;
            try
            {
                log = Marshal.PtrToStructure<StorageStructs.NVME_HEALTH_INFO_LOG>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            var data = new NVMeHealthData
            {
                CriticalWarningRaw = log.CriticalWarning,
                AvailableSpareBelow = (log.CriticalWarning & 0x01) != 0,
                TemperatureExceeded = (log.CriticalWarning & 0x02) != 0,
                ReliabilityDegraded = (log.CriticalWarning & 0x04) != 0,
                ReadOnlyMode = (log.CriticalWarning & 0x08) != 0,
                VolatileMemoryBackupFailed = (log.CriticalWarning & 0x10) != 0,

                TemperatureKelvin = log.CompositeTemperature,
                AvailableSpare = log.AvailableSpare,
                AvailableSpareThreshold = log.AvailableSpareThreshold,
                PercentageUsed = log.PercentageUsed,

                DataUnitsRead = Read128BitLE(log.DataUnitsRead),
                DataUnitsWritten = Read128BitLE(log.DataUnitsWritten),
                HostReadCommands = Read128BitLE(log.HostReadCommands),
                HostWriteCommands = Read128BitLE(log.HostWriteCommands),
                ControllerBusyTime = Read128BitLE(log.ControllerBusyTime),
                PowerCycles = Read128BitLE(log.PowerCycles),
                PowerOnHours = Read128BitLE(log.PowerOnHours),
                UnsafeShutdowns = Read128BitLE(log.UnsafeShutdowns),
                MediaErrors = Read128BitLE(log.MediaErrors),
                ErrorLogEntries = Read128BitLE(log.ErrorLogEntries),

                WarningCompositeTemp = log.WarningCompositeTemperatureTime,
                CriticalCompositeTemp = log.CriticalCompositeTemperatureTime,

                TemperatureSensors =
                [
                    log.TemperatureSensor1, log.TemperatureSensor2,
                    log.TemperatureSensor3, log.TemperatureSensor4,
                    log.TemperatureSensor5, log.TemperatureSensor6,
                    log.TemperatureSensor7, log.TemperatureSensor8
                ],

                ThermalMgmtTemp1TransitionCount = log.ThermalMgmtTemp1TransitionCount,
                ThermalMgmtTemp2TransitionCount = log.ThermalMgmtTemp2TransitionCount,
                TotalTimeThermalMgmt1 = log.TotalTimeForThermalMgmtTemp1,
                TotalTimeThermalMgmt2 = log.TotalTimeForThermalMgmtTemp2,

                Timestamp = DateTime.UtcNow
            };

            return data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Performs a single telemetry poll for the specified drive.
    /// Opens the handle, reads SMART data, parses it, and returns the result.
    /// </summary>
    /// <param name="driveNumber">Physical drive number.</param>
    /// <returns>Parsed health data, or null if the drive is unsupported or inaccessible.</returns>
    public static async Task<NVMeHealthData?> PollAsync(int driveNumber)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var handle = OpenDriveHandle(driveNumber);
                if (handle is null)
                    return null;

                byte[]? raw = ReadSmartLog(handle);
                if (raw is null)
                    return null;

                var data = ParseHealthInfoLog(raw);
                if (data is not null)
                    data.DriveNumber = driveNumber;

                return data;
            }
            catch
            {
                // Graceful degradation: return null on any failure
                return null;
            }
        });
    }

    /// <summary>
    /// Reads a 128-bit little-endian unsigned integer from a 16-byte array into a decimal.
    /// .NET decimal is a 96-bit mantissa with a 28-29 significant-digit range, which is enough
    /// for any practical NVMe SMART counter (data units &lt; 10^15, host commands &lt; 10^16).
    /// Values that genuinely exceed decimal.MaxValue are clamped rather than throwing.
    /// </summary>
    internal static decimal Read128BitLE(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 16)
            return 0m;

        try
        {
            var value = new BigInteger(bytes.AsSpan(0, 16), isUnsigned: true, isBigEndian: false);
            if (value > new BigInteger(decimal.MaxValue))
                return decimal.MaxValue;

            return (decimal)value;
        }
        catch
        {
            // 128-bit value genuinely exceeds decimal.MaxValue (~7.9e28). Surface the cap rather
            // than letting the parser report a corrupted SMART read.
            return decimal.MaxValue;
        }
    }
}
