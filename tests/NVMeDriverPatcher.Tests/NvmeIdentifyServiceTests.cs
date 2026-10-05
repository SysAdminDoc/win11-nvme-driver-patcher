using System.Runtime.InteropServices;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class NvmeIdentifyServiceTests
{
    [Fact]
    public void NvmeIdentifyResult_RedactsSerialNumber()
    {
        var r = new NvmeIdentifyResult { SerialNumber = "ABCDEFGH12345678" };
        Assert.EndsWith("5678", r.RedactedSerialNumber);
        Assert.StartsWith("*", r.RedactedSerialNumber);
        Assert.DoesNotContain("ABCDE", r.RedactedSerialNumber);
    }

    [Fact]
    public void NvmeIdentifyResult_ShortSerial_FullyRedacted()
    {
        var r = new NvmeIdentifyResult { SerialNumber = "AB" };
        Assert.Equal("****", r.RedactedSerialNumber);
    }

    [Fact]
    public void NvmeIdentifyResult_DefaultsAreEmpty()
    {
        var r = new NvmeIdentifyResult();
        Assert.False(r.Success);
        Assert.Equal(string.Empty, r.ModelNumber);
        Assert.Equal(string.Empty, r.FirmwareRevision);
        Assert.Empty(r.PowerStates);
    }

    [Fact]
    public void NvmePowerStateDescriptor_DefaultsAreZero()
    {
        var ps = new NvmePowerStateDescriptor();
        Assert.Equal(0, ps.Index);
        Assert.Equal(0.0, ps.MaxPowerWatts);
        Assert.Equal(0u, ps.EntryLatencyUs);
        Assert.Equal(0u, ps.ExitLatencyUs);
        Assert.False(ps.NonOperational);
    }

    // --- Request shape and response parsing, against fixture buffers ---

    // What stornvme returned for Identify Controller on real drives: a 48-byte descriptor, the
    // 4KB page at offset 40 from the protocol data, 4144 bytes in all.
    private const int RealBytesReturned = 4144;

    [Theory]
    [InlineData(49u)]   // StorageAdapterProtocolSpecificProperty
    [InlineData(50u)]   // StorageDeviceProtocolSpecificProperty
    public void Request_MatchesTheWinIoctlPropertyQueryContract(uint propertyId)
    {
        // winioctl.h: CTL_CODE(IOCTL_STORAGE_BASE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS). Any
        // access is what lets the query run on a handle opened with no rights.
        Assert.Equal(CtlCode(0x2D, 0x500, method: 0, access: 0), NvmeIdentifyService.IOCTL_STORAGE_QUERY_PROPERTY);
        Assert.Equal(48 + 4096, NvmeIdentifyService.RequestSize);

        var buffer = Marshal.AllocHGlobal(NvmeIdentifyService.RequestSize);
        try
        {
            for (var i = 0; i < NvmeIdentifyService.RequestSize; i++) Marshal.WriteByte(buffer, i, 0xCC);
            NvmeIdentifyService.WriteRequest(buffer, propertyId);

            Assert.Equal((int)propertyId, Marshal.ReadInt32(buffer, 0));  // PropertyId
            Assert.Equal(0, Marshal.ReadInt32(buffer, 4));                // PropertyStandardQuery
            Assert.Equal(3, Marshal.ReadInt32(buffer, 8));                // ProtocolTypeNvme
            Assert.Equal(1, Marshal.ReadInt32(buffer, 12));               // NVMeDataTypeIdentify
            Assert.Equal(1, Marshal.ReadInt32(buffer, 16));               // CNS = controller
            Assert.Equal(0, Marshal.ReadInt32(buffer, 20));               // SubValue (no namespace)
            Assert.Equal(40, Marshal.ReadInt32(buffer, 24));              // ProtocolDataOffset
            Assert.Equal(4096, Marshal.ReadInt32(buffer, 28));            // ProtocolDataLength
            // The data area starts zeroed, so leftover bytes can't pass as an identity.
            Assert.Equal(0, Marshal.ReadInt32(buffer, 48 + 24));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ParseResponse_ZeroedBuffer_IsNotASuccessfulIdentify()
    {
        var buffer = Marshal.AllocHGlobal(NvmeIdentifyService.RequestSize);
        try
        {
            for (var i = 0; i < NvmeIdentifyService.RequestSize; i++) Marshal.WriteByte(buffer, i, 0);
            var result = new NvmeIdentifyResult();

            NvmeIdentifyService.ParseResponse(buffer, RealBytesReturned, result);

            Assert.False(result.Success, result.Summary);
            Assert.Contains("unexpected descriptor", result.Summary, StringComparison.Ordinal);
            Assert.Equal(string.Empty, result.VendorId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(40, 4096, 1000)]        // reply shorter than the page it claims
    [InlineData(8, 4096, RealBytesReturned)]    // offset inside the specific data itself
    [InlineData(40, 512, RealBytesReturned)]    // less than a full Identify page
    [InlineData(4000, 4096, RealBytesReturned)] // offset that would read past the buffer
    [InlineData(-64, 4096, RealBytesReturned)]  // negative offset from a broken driver
    public void ParseResponse_DataRangeOutsideTheReply_IsAFailureEvenWithPlausibleData(int offset, int length, int bytesReturned)
    {
        var buffer = Response(withIdentity: true, offset: offset, length: length);
        try
        {
            var result = new NvmeIdentifyResult();

            NvmeIdentifyService.ParseResponse(buffer, bytesReturned, result);

            Assert.False(result.Success, result.Summary);
            Assert.Contains("not a full Identify page", result.Summary, StringComparison.Ordinal);
            Assert.Equal(string.Empty, result.ModelNumber);
            Assert.Empty(result.PowerStates);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ParseResponse_SuccessWithEmptyIdentifyData_IsNotASuccessfulIdentify()
    {
        var buffer = Response(withIdentity: false);
        try
        {
            var result = new NvmeIdentifyResult();

            NvmeIdentifyService.ParseResponse(buffer, RealBytesReturned, result);

            Assert.False(result.Success, result.Summary);
            Assert.Equal(string.Empty, result.VendorId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ParseResponse_Success_ReadsTheControllerIdentity()
    {
        var buffer = Response(withIdentity: true);
        try
        {
            var result = new NvmeIdentifyResult();

            NvmeIdentifyService.ParseResponse(buffer, RealBytesReturned, result);

            Assert.True(result.Success, result.Summary);
            Assert.Equal("0x144D", result.VendorId);
            Assert.Equal("S6B0NL0W123456A", result.SerialNumber);
            Assert.Equal("Samsung SSD 990 PRO 2TB", result.ModelNumber);
            Assert.Equal("4B2QJXD7", result.FirmwareRevision);
            Assert.Equal(5, result.NumberOfPowerStates);
            Assert.Equal(5, result.PowerStates.Count);
            Assert.Equal(3.25, result.PowerStates[0].MaxPowerWatts, precision: 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ParseResponse_ReadsPowerStateFlagsFromDescriptorByteThree()
    {
        // NVMe power state descriptor: MP at bytes 0-1, byte 3 holds MXPS (bit 0) and NOPS (bit 1),
        // ENLAT at 4-7, EXLAT at 8-11. NOPS used to be read from byte 25, a reserved byte, so every
        // state on a real Samsung PM9C1b came back operational.
        var buffer = Response(withIdentity: true);
        try
        {
            var data = 8 + 40;
            int ps3 = data + 2048 + 3 * 32, ps4 = data + 2048 + 4 * 32;
            Marshal.WriteInt16(buffer, ps3, unchecked((short)5));
            Marshal.WriteByte(buffer, ps3 + 3, 0x02);                     // NOPS
            Marshal.WriteInt32(buffer, ps3 + 4, 5000);
            Marshal.WriteInt32(buffer, ps3 + 8, 10000);
            Marshal.WriteInt16(buffer, ps4, unchecked((short)50));
            Marshal.WriteByte(buffer, ps4 + 3, 0x03);                     // NOPS + MXPS
            Marshal.WriteByte(buffer, ps4 + 25, 0x00);
            Marshal.WriteByte(buffer, data + 2048 + 25, 0x02);            // reserved byte set on PS0
            var result = new NvmeIdentifyResult();

            NvmeIdentifyService.ParseResponse(buffer, RealBytesReturned, result);

            Assert.True(result.Success, result.Summary);
            Assert.False(result.PowerStates[0].NonOperational);
            Assert.True(result.PowerStates[3].NonOperational);
            Assert.Equal(0.05, result.PowerStates[3].MaxPowerWatts, precision: 4);
            Assert.Equal(5000u, result.PowerStates[3].EntryLatencyUs);
            Assert.Equal(10000u, result.PowerStates[3].ExitLatencyUs);
            Assert.True(result.PowerStates[4].NonOperational);
            Assert.Equal(0.005, result.PowerStates[4].MaxPowerWatts, precision: 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    // A reply as stornvme writes it: STORAGE_PROTOCOL_DATA_DESCRIPTOR (Version = Size = 48), the
    // protocol data echoed at offset 8, and the Identify page at 8 + ProtocolDataOffset.
    private static IntPtr Response(bool withIdentity, int offset = 40, int length = 4096)
    {
        var buffer = Marshal.AllocHGlobal(NvmeIdentifyService.RequestSize);
        NvmeIdentifyService.WriteRequest(buffer, NvmeIdentifyService.StorageAdapterProtocolSpecificProperty);
        Marshal.WriteInt32(buffer, 0, 48);
        Marshal.WriteInt32(buffer, 4, 48);
        Marshal.WriteInt32(buffer, 8 + 16, offset);
        Marshal.WriteInt32(buffer, 8 + 20, length);
        if (withIdentity)
        {
            // Written at the standard place: a bad offset must keep the parser away from it.
            var data = 8 + 40;
            Marshal.WriteInt16(buffer, data + 0, unchecked((short)0x144D));
            Marshal.WriteInt16(buffer, data + 2, unchecked((short)0x144D));
            WriteAscii(buffer, data + 4, "S6B0NL0W123456A", 20);
            WriteAscii(buffer, data + 24, "Samsung SSD 990 PRO 2TB", 40);
            WriteAscii(buffer, data + 64, "4B2QJXD7", 8);
            Marshal.WriteByte(buffer, data + 263, 4);                       // NPSS is zero-based: five power states
            Marshal.WriteInt16(buffer, data + 2048, unchecked((short)325)); // PS0 max power, 0.01 W units
        }
        return buffer;
    }

    // NVMe identity strings are ASCII, space padded to the field width.
    private static void WriteAscii(IntPtr buffer, int offset, string value, int width)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value.PadRight(width));
        Marshal.Copy(bytes, 0, IntPtr.Add(buffer, offset), width);
    }

    [Fact]
    public void Query_InvalidDrive_ReturnsFailure()
    {
        var r = NvmeIdentifyService.Query(99);
        Assert.False(r.Success);
        Assert.Contains("99", r.DrivePath);
    }

    [Fact]
    public void Query_Drive0_DoesNotThrow()
    {
        var r = NvmeIdentifyService.Query(0);
        Assert.NotNull(r);
        Assert.False(string.IsNullOrWhiteSpace(r.DrivePath));
    }
}
