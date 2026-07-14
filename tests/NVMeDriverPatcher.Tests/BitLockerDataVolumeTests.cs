using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BitLockerDataVolumeTests
{
    private static DriveService.BitLockerVolume Vol(string letter, bool system, bool prot, bool autoUnlock) =>
        new(letter, system, prot, autoUnlock);

    [Fact]
    public void SystemOnly_NoDataVolumesFlagged()
    {
        var vols = new[] { Vol("C:", system: true, prot: true, autoUnlock: false) };
        Assert.Empty(DriveService.DataVolumesNeedingAttention(vols));
    }

    [Fact]
    public void AutoUnlockDataVolume_NotFlagged()
    {
        var vols = new[]
        {
            Vol("C:", system: true, prot: true, autoUnlock: false),
            Vol("D:", system: false, prot: true, autoUnlock: true),
        };
        Assert.Empty(DriveService.DataVolumesNeedingAttention(vols));
    }

    [Fact]
    public void NonAutoUnlockProtectedDataVolume_IsFlagged()
    {
        var vols = new[]
        {
            Vol("C:", system: true, prot: true, autoUnlock: false),
            Vol("D:", system: false, prot: true, autoUnlock: false),
            Vol("E:", system: false, prot: false, autoUnlock: false), // unprotected — ignored
        };
        var flagged = DriveService.DataVolumesNeedingAttention(vols);
        Assert.Single(flagged);
        Assert.Equal("D:", flagged[0].DriveLetter);
    }

    [Fact]
    public void UnprotectedDataVolume_NotFlagged()
    {
        var vols = new[] { Vol("D:", system: false, prot: false, autoUnlock: false) };
        Assert.Empty(DriveService.DataVolumesNeedingAttention(vols));
    }
}
