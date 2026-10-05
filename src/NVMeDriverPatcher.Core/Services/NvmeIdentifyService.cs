using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NVMeDriverPatcher.Services;

public class NvmePowerStateDescriptor
{
    public int Index { get; set; }
    public double MaxPowerWatts { get; set; }
    public uint EntryLatencyUs { get; set; }
    public uint ExitLatencyUs { get; set; }
    public bool NonOperational { get; set; }
}

public class NvmeIdentifyResult
{
    public bool Success { get; set; }
    public string DrivePath { get; set; } = string.Empty;
    public string ModelNumber { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string FirmwareRevision { get; set; } = string.Empty;
    public string VendorId { get; set; } = string.Empty;
    public string SubsystemVendorId { get; set; } = string.Empty;
    public int NumberOfNamespaces { get; set; }
    public int MaxDataTransferSizePages { get; set; }
    public int NumberOfPowerStates { get; set; }
    public bool SupportsFormatNvm { get; set; }
    public bool SupportsFirmwareDownload { get; set; }
    public bool SupportsNamespaceMgmt { get; set; }
    public bool VolatileWriteCache { get; set; }
    public List<NvmePowerStateDescriptor> PowerStates { get; set; } = new();
    public string Summary { get; set; } = string.Empty;

    public string RedactedSerialNumber => SerialNumber.Length > 4
        ? new string('*', SerialNumber.Length - 4) + SerialNumber[^4..]
        : "****";
}

// NVMe Identify Controller through IOCTL_STORAGE_QUERY_PROPERTY, the route Microsoft documents for
// Identify. Pulls fields WMI doesn't expose (PCI vendor/subvendor, exact firmware, power states) and
// feeds FirmwareCompatService with the controller identity. The earlier IOCTL_STORAGE_PROTOCOL_COMMAND
// pass-through failed with ERROR_INVALID_PARAMETER on stornvme even as SYSTEM, and it needed a
// read/write handle, so it only ever ran elevated. A property query works on a handle opened with no
// access rights, which a standard user can open.
public static class NvmeIdentifyService
{
    // CTL_CODE(IOCTL_STORAGE_BASE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS) per winioctl.h.
    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    // STORAGE_PROPERTY_ID. Microsoft's sample asks the adapter; the device property is the fallback.
    internal const uint StorageAdapterProtocolSpecificProperty = 49;
    internal const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint ProtocolTypeNvme = 3;
    private const uint NVMeDataTypeIdentify = 1;
    private const uint NVME_IDENTIFY_CNS_CONTROLLER = 1;

    // STORAGE_PROPERTY_QUERY is PropertyId + QueryType, then AdditionalParameters, which holds the
    // STORAGE_PROTOCOL_SPECIFIC_DATA. The reply reuses the buffer as STORAGE_PROTOCOL_DATA_DESCRIPTOR
    // (Version, Size, then the same specific data), so both sides put the specific data at offset 8.
    internal const int QueryHeaderSize = 8;
    internal const int ProtocolSpecificDataSize = 40;
    internal const int DescriptorSize = QueryHeaderSize + ProtocolSpecificDataSize;
    internal const int IdentifyDataSize = 4096;  // Identify Controller payload is 4KB
    internal const int RequestSize = DescriptorSize + IdentifyDataSize;

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
            0u /* no access rights: enough for a property query */,
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
            IntPtr buffer = Marshal.AllocHGlobal(RequestSize);
            try
            {
                var failures = new List<string>();
                foreach (var (name, propertyId) in new[]
                {
                    ("adapter", StorageAdapterProtocolSpecificProperty),
                    ("device", StorageDeviceProtocolSpecificProperty)
                })
                {
                    WriteRequest(buffer, propertyId);
                    if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                            buffer, RequestSize,
                            buffer, RequestSize,
                            out uint returned, IntPtr.Zero))
                    {
                        // A drive that isn't NVMe answers with ERROR_INVALID_FUNCTION (1) or 55.
                        failures.Add($"{name} query: Win32 error {Marshal.GetLastWin32Error()}");
                        continue;
                    }

                    var attempt = new NvmeIdentifyResult { DrivePath = result.DrivePath };
                    ParseResponse(buffer, (int)returned, attempt);
                    if (attempt.Success) return attempt;
                    failures.Add($"{name} query: {attempt.Summary}");
                }
                result.Summary = "NVMe Identify Controller failed: " + string.Join("; ", failures);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    /// <summary>Builds an Identify Controller property query in a <see cref="RequestSize"/>-byte buffer.</summary>
    internal static void WriteRequest(IntPtr buffer, uint propertyId)
    {
        // Zero the buffer first: stale bytes in the reply area must never read as Identify data.
        for (int i = 0; i < RequestSize; i++) Marshal.WriteByte(buffer, i, 0);

        Marshal.WriteInt32(buffer, 0, (int)propertyId);
        Marshal.WriteInt32(buffer, 4, 0);  // PropertyStandardQuery
        int specific = QueryHeaderSize;
        Marshal.WriteInt32(buffer, specific + 0, (int)ProtocolTypeNvme);
        Marshal.WriteInt32(buffer, specific + 4, (int)NVMeDataTypeIdentify);
        Marshal.WriteInt32(buffer, specific + 8, (int)NVME_IDENTIFY_CNS_CONTROLLER);  // ProtocolDataRequestValue
        Marshal.WriteInt32(buffer, specific + 12, 0);                                 // ProtocolDataRequestSubValue
        Marshal.WriteInt32(buffer, specific + 16, ProtocolSpecificDataSize);          // ProtocolDataOffset
        Marshal.WriteInt32(buffer, specific + 20, IdentifyDataSize);                  // ProtocolDataLength
    }

    /// <summary>
    /// Reads a completed property query into <paramref name="result"/>. The reply must be a
    /// STORAGE_PROTOCOL_DATA_DESCRIPTOR whose data range sits inside the buffer and covers the full
    /// 4KB Identify page. The offset comes from the driver, so it's checked before anything is read.
    /// </summary>
    internal static void ParseResponse(IntPtr buffer, int bytesReturned, NvmeIdentifyResult result)
    {
        int version = Marshal.ReadInt32(buffer, 0);
        int size = Marshal.ReadInt32(buffer, 4);
        if (version != DescriptorSize || size != DescriptorSize)
        {
            result.Summary = $"NVMe Identify Controller returned an unexpected descriptor (version {version}, size {size}).";
            return;
        }

        int offset = Marshal.ReadInt32(buffer, QueryHeaderSize + 16);
        int length = Marshal.ReadInt32(buffer, QueryHeaderSize + 20);
        long end = (long)QueryHeaderSize + offset + IdentifyDataSize;
        if (offset < ProtocolSpecificDataSize || length < IdentifyDataSize || end > RequestSize || end > bytesReturned)
        {
            result.Summary = $"NVMe Identify Controller returned {length} bytes at offset {offset} ({bytesReturned} bytes in all), not a full Identify page.";
            return;
        }

        IntPtr dataPtr = IntPtr.Add(buffer, QueryHeaderSize + offset);
        var serial = ReadAscii(dataPtr, 4, 20);
        var model = ReadAscii(dataPtr, 24, 40);
        // Serial and model are mandatory ASCII fields; both empty means no Identify data arrived.
        if (serial.Length == 0 && model.Length == 0)
        {
            result.Summary = "NVMe Identify Controller reported success but returned no controller identity (empty Identify data).";
            return;
        }

        result.VendorId = ReadHex16(dataPtr, 0);
        result.SubsystemVendorId = ReadHex16(dataPtr, 2);
        result.SerialNumber = serial;
        result.ModelNumber = model;
        result.FirmwareRevision = ReadAscii(dataPtr, 64, 8);

        result.MaxDataTransferSizePages = Marshal.ReadByte(dataPtr, 77);
        result.NumberOfNamespaces = Marshal.ReadInt32(dataPtr, 516);

        ushort oacs = ReadUInt16(dataPtr, 256);
        result.SupportsFormatNvm = (oacs & 0x02) != 0;
        result.SupportsFirmwareDownload = (oacs & 0x04) != 0;
        result.SupportsNamespaceMgmt = (oacs & 0x08) != 0;

        result.VolatileWriteCache = (Marshal.ReadByte(dataPtr, 525) & 0x01) != 0;

        int npss = Marshal.ReadByte(dataPtr, 263) + 1;
        result.NumberOfPowerStates = npss;
        for (int ps = 0; ps < Math.Min(npss, 32); ps++)
        {
            int psOffset = 2048 + (ps * 32);
            ushort mp = ReadUInt16(dataPtr, psOffset);
            // Power state descriptor byte 3: bit 0 MXPS (0.0001 W units), bit 1 NOPS (non-operational).
            byte flags = Marshal.ReadByte(dataPtr, psOffset + 3);
            bool mpsScale = (flags & 0x01) != 0;
            double maxPowerW = mp * (mpsScale ? 0.0001 : 0.01);
            uint entryLat = ReadUInt32(dataPtr, psOffset + 4);
            uint exitLat = ReadUInt32(dataPtr, psOffset + 8);
            bool nonOp = (flags & 0x02) != 0;

            result.PowerStates.Add(new NvmePowerStateDescriptor
            {
                Index = ps,
                MaxPowerWatts = maxPowerW,
                EntryLatencyUs = entryLat,
                ExitLatencyUs = exitLat,
                NonOperational = nonOp
            });
        }

        result.Success = true;
        result.Summary = $"{result.ModelNumber.Trim()} / FW {result.FirmwareRevision.Trim()} / VID {result.VendorId} / {npss} power states";
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
        ushort v = ReadUInt16(baseAddr, offset);
        return "0x" + v.ToString("X4");
    }

    private static ushort ReadUInt16(IntPtr baseAddr, int offset) =>
        (ushort)(Marshal.ReadByte(baseAddr, offset) | (Marshal.ReadByte(baseAddr, offset + 1) << 8));

    private static uint ReadUInt32(IntPtr baseAddr, int offset) =>
        (uint)(Marshal.ReadByte(baseAddr, offset)
            | (Marshal.ReadByte(baseAddr, offset + 1) << 8)
            | (Marshal.ReadByte(baseAddr, offset + 2) << 16)
            | (Marshal.ReadByte(baseAddr, offset + 3) << 24));
}
