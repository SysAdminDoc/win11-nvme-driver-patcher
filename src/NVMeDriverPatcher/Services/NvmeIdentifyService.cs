using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NVMeDriverPatcher.Services;

public class NvmeIdentifyResult
{
    public bool Success { get; set; }
    public string DrivePath { get; set; } = string.Empty;
    public string ModelNumber { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string FirmwareRevision { get; set; } = string.Empty;
    public string VendorId { get; set; } = string.Empty;
    public string SubsystemVendorId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

// Raw NVMe Admin Identify Controller via IOCTL_STORAGE_PROTOCOL_COMMAND. Pulls fields WMI
// doesn't expose (PCI vendor/subvendor, exact firmware slot). Closes part of ROADMAP §2.8 +
// feeds FirmwareCompatService with the authoritative controller identity.
public static class NvmeIdentifyService
{
    private const uint IOCTL_STORAGE_PROTOCOL_COMMAND = 0x2DD4C0;
    private const uint STORAGE_PROTOCOL_TYPE_NVME = 3;
    private const uint STORAGE_PROTOCOL_NVME_DATA_TYPE_INQUIRY = 1;
    private const uint NVME_IDENTIFY_CNS_CONTROLLER = 1;
    private const uint STORAGE_PROTOCOL_COMMAND_FLAG_ADAPTER_REQUEST = 0x80000000;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct STORAGE_PROTOCOL_COMMAND
    {
        public uint Version;
        public uint Length;
        public uint ProtocolType;
        public uint Flags;
        public uint ReturnStatus;
        public uint ErrorCode;
        public uint CommandLength;
        public uint ErrorInfoLength;
        public uint DataToDeviceTransferLength;
        public uint DataFromDeviceTransferLength;
        public uint TimeOutValue;
        public uint ErrorInfoOffset;
        public uint DataToDeviceBufferOffset;
        public uint DataFromDeviceBufferOffset;
        public uint CommandSpecific;
        public uint Reserved0;
        public uint FixedProtocolReturnData;
        public uint Reserved1_0;
        public uint Reserved1_1;
        public uint Reserved1_2;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    public static NvmeIdentifyResult Query(int physicalDriveNumber)
    {
        var result = new NvmeIdentifyResult { DrivePath = $@"\\.\PhysicalDrive{physicalDriveNumber}" };
        var handle = CreateFileW(
            result.DrivePath,
            0x80000000u /* GENERIC_READ */ | 0x40000000u /* GENERIC_WRITE */,
            3u /* FILE_SHARE_READ | FILE_SHARE_WRITE */,
            IntPtr.Zero,
            3u /* OPEN_EXISTING */,
            0u,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            result.Summary = $"Could not open {result.DrivePath}: Win32 error {Marshal.GetLastWin32Error()}";
            return result;
        }

        using (handle)
        {
            int cmdSize = Marshal.SizeOf<STORAGE_PROTOCOL_COMMAND>();
            const int dataSize = 4096;   // Identify Controller payload is 4KB
            const int cdbSize = 64;
            const int errorInfoSize = 0;
            int totalSize = cmdSize + cdbSize + dataSize;

            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                // Zero the buffer before use — stack garbage in an IOCTL payload can return
                // a nonsense "successful" response on some controllers.
                for (int i = 0; i < totalSize; i++) Marshal.WriteByte(buffer, i, 0);

                var cmd = new STORAGE_PROTOCOL_COMMAND
                {
                    Version = 1,
                    Length = (uint)cmdSize,
                    ProtocolType = STORAGE_PROTOCOL_TYPE_NVME,
                    Flags = STORAGE_PROTOCOL_COMMAND_FLAG_ADAPTER_REQUEST,
                    CommandLength = cdbSize,
                    ErrorInfoLength = errorInfoSize,
                    DataFromDeviceTransferLength = dataSize,
                    TimeOutValue = 30,
                    DataFromDeviceBufferOffset = (uint)(cmdSize + cdbSize)
                };
                Marshal.StructureToPtr(cmd, buffer, fDeleteOld: false);

                // NVMe Identify Controller opcode = 0x06 at CDB byte 0, CNS = 1 at CDB byte 40.
                Marshal.WriteByte(buffer, cmdSize + 0, 0x06);
                Marshal.WriteInt32(buffer, cmdSize + 40, (int)NVME_IDENTIFY_CNS_CONTROLLER);

                if (!DeviceIoControl(handle, IOCTL_STORAGE_PROTOCOL_COMMAND,
                        buffer, (uint)totalSize,
                        buffer, (uint)totalSize,
                        out _, IntPtr.Zero))
                {
                    result.Summary = $"IOCTL_STORAGE_PROTOCOL_COMMAND failed: Win32 error {Marshal.GetLastWin32Error()}";
                    return result;
                }

                IntPtr dataPtr = IntPtr.Add(buffer, cmdSize + cdbSize);
                result.VendorId = ReadHex16(dataPtr, 0);
                result.SubsystemVendorId = ReadHex16(dataPtr, 2);
                result.SerialNumber = ReadAscii(dataPtr, 4, 20);
                result.ModelNumber = ReadAscii(dataPtr, 24, 40);
                result.FirmwareRevision = ReadAscii(dataPtr, 64, 8);
                result.Success = true;
                result.Summary = $"{result.ModelNumber.Trim()} / FW {result.FirmwareRevision.Trim()} / VID {result.VendorId}";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    private static string ReadAscii(IntPtr baseAddr, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(IntPtr.Add(baseAddr, offset), bytes, 0, length);
        // Trim trailing spaces / nulls per NVMe spec padding rules.
        int end = bytes.Length;
        while (end > 0 && (bytes[end - 1] == 0 || bytes[end - 1] == 0x20)) end--;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end);
    }

    private static string ReadHex16(IntPtr baseAddr, int offset)
    {
        ushort v = (ushort)(Marshal.ReadByte(baseAddr, offset) | (Marshal.ReadByte(baseAddr, offset + 1) << 8));
        return "0x" + v.ToString("X4");
    }
}
