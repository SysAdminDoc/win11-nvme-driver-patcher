using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class HotSwapServiceTests
{
    [Fact]
    public void CanHotSwap_AllowsNonBootNvmeWithDeviceId()
    {
        var drive = new SystemDrive
        {
            IsNVMe = true,
            IsBoot = false,
            PNPDeviceID = @"SCSI\DISK&VEN_NVME&PROD_TEST"
        };

        Assert.True(HotSwapService.CanHotSwap(drive));
    }

    [Theory]
    [InlineData(true, false, @"SCSI\DISK&VEN_NVME&PROD_TEST")]
    [InlineData(false, false, @"SCSI\DISK&VEN_NVME&PROD_TEST")]
    [InlineData(false, true, "")]
    [InlineData(false, true, "Unknown")]
    public void CanHotSwap_BlocksUnsafeOrUnknownTargets(bool isBoot, bool isNvme, string pnpDeviceId)
    {
        var drive = new SystemDrive
        {
            IsNVMe = isNvme,
            IsBoot = isBoot,
            PNPDeviceID = pnpDeviceId
        };

        Assert.False(HotSwapService.CanHotSwap(drive));
    }

    [Theory]
    [InlineData("C:")]
    [InlineData("z:")]
    public void IsSimpleDriveLetter_AllowsOnlyBareDriveLetters(string driveLetter)
    {
        Assert.True(HotSwapService.IsSimpleDriveLetter(driveLetter));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("C")]
    [InlineData("C:\\")]
    [InlineData("C: /P")]
    [InlineData("1:")]
    [InlineData("AA:")]
    public void IsSimpleDriveLetter_RejectsMalformedOrUnsafeValues(string? driveLetter)
    {
        Assert.False(HotSwapService.IsSimpleDriveLetter(driveLetter));
    }
}
