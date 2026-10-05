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

    private const uint StatusSuccess = 0x1;

    [Fact]
    public void Request_MatchesTheWinIoctlProtocolCommandContract()
    {
        // winioctl.h: CTL_CODE(IOCTL_STORAGE_BASE, 0x04F0, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
        uint shipped = NvmeIdentifyService.IOCTL_STORAGE_PROTOCOL_COMMAND;
        Assert.Equal(CtlCode(0x2D, 0x4F0, method: 0, access: 0x1 | 0x2), shipped);
        Assert.Equal(80, NvmeIdentifyService.HeaderSize);

        var buffer = Marshal.AllocHGlobal(NvmeIdentifyService.RequestSize);
        try
        {
            NvmeIdentifyService.WriteRequest(buffer);

            Assert.Equal(1, Marshal.ReadInt32(buffer, 0));        // Version
            Assert.Equal(80, Marshal.ReadInt32(buffer, 4));       // Length
            Assert.Equal(3, Marshal.ReadInt32(buffer, 8));        // ProtocolTypeNvme
            Assert.Equal(0, Marshal.ReadInt32(buffer, 16));       // ReturnStatus starts clear
            Assert.Equal(64, Marshal.ReadInt32(buffer, 24));      // CommandLength (NVMe commands are 64 bytes)
            Assert.Equal(4096, Marshal.ReadInt32(buffer, 36));    // DataFromDeviceTransferLength
            Assert.Equal(144, Marshal.ReadInt32(buffer, 52));     // DataFromDeviceBufferOffset
            Assert.Equal(1, Marshal.ReadInt32(buffer, 56));       // CommandSpecific = NVMe admin command
            Assert.Equal(0x06, Marshal.ReadByte(buffer, 80));     // Identify opcode
            Assert.Equal(1, Marshal.ReadInt32(buffer, 80 + 40));  // CDW10 CNS = controller
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

            NvmeIdentifyService.ParseResponse(buffer, result);

            Assert.False(result.Success, result.Summary);
            Assert.Contains("Pending", result.Summary, StringComparison.Ordinal);
            Assert.Equal(string.Empty, result.VendorId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(0x2u)]    // STORAGE_PROTOCOL_STATUS_ERROR
    [InlineData(0x3u)]    // STORAGE_PROTOCOL_STATUS_INVALID_REQUEST
    [InlineData(0xFFu)]   // STORAGE_PROTOCOL_STATUS_NOT_SUPPORTED
    public void ParseResponse_FailedProtocolStatus_IsAFailureEvenWithPlausibleData(uint returnStatus)
    {
        var buffer = Response(returnStatus, errorCode: 0x4002, withIdentity: true);
        try
        {
            var result = new NvmeIdentifyResult();

            NvmeIdentifyService.ParseResponse(buffer, result);

            Assert.False(result.Success, result.Summary);
            Assert.Contains("0x4002", result.Summary, StringComparison.Ordinal);
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
        var buffer = Response(StatusSuccess, errorCode: 0, withIdentity: false);
        try
        {
            var result = new NvmeIdentifyResult();

            NvmeIdentifyService.ParseResponse(buffer, result);

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
        var buffer = Response(StatusSuccess, errorCode: 0, withIdentity: true);
        try
        {
            var result = new NvmeIdentifyResult();

            NvmeIdentifyService.ParseResponse(buffer, result);

            Assert.True(result.Success, result.Summary);
            Assert.Equal("0x144D", result.VendorId);
            Assert.Equal("S6B0NL0W123456A", result.SerialNumber);
            Assert.Equal("Samsung SSD 990 PRO 2TB", result.ModelNumber);
            Assert.Equal("4B2QJXD7", result.FirmwareRevision);
            Assert.Equal(5, result.NumberOfPowerStates);
            Assert.Equal(5, result.PowerStates.Count);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    private static IntPtr Response(uint returnStatus, uint errorCode, bool withIdentity)
    {
        var buffer = Marshal.AllocHGlobal(NvmeIdentifyService.RequestSize);
        NvmeIdentifyService.WriteRequest(buffer);
        Marshal.WriteInt32(buffer, 16, unchecked((int)returnStatus));
        Marshal.WriteInt32(buffer, 20, unchecked((int)errorCode));
        if (withIdentity)
        {
            var data = NvmeIdentifyService.HeaderSize + NvmeIdentifyService.CommandBlockSize;
            Marshal.WriteInt16(buffer, data + 0, unchecked((short)0x144D));
            Marshal.WriteInt16(buffer, data + 2, unchecked((short)0x144D));
            WriteAscii(buffer, data + 4, "S6B0NL0W123456A", 20);
            WriteAscii(buffer, data + 24, "Samsung SSD 990 PRO 2TB", 40);
            WriteAscii(buffer, data + 64, "4B2QJXD7", 8);
            Marshal.WriteByte(buffer, data + 263, 4);   // NPSS is zero-based: five power states
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
