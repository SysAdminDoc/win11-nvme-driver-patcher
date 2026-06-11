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
