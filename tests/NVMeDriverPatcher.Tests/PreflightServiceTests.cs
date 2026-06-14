using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PreflightServiceTests
{
    // --- Pending-reboot classification ---

    [Fact]
    public void ClassifyPendingReboot_NonePending_ReturnsNull()
    {
        Assert.Null(PreflightService.ClassifyPendingReboot(false, false));
    }

    [Theory]
    [InlineData(true, false, "servicing")]
    [InlineData(false, true, "Windows Update")]
    public void ClassifyPendingReboot_SingleSource_WarnsAndNamesIt(bool cbs, bool wu, string expectedSource)
    {
        var check = PreflightService.ClassifyPendingReboot(cbs, wu);
        Assert.NotNull(check);
        Assert.Equal(CheckStatus.Warning, check!.Status);
        Assert.Contains(expectedSource, check.Message);
        Assert.Contains("restart Windows first", check.Message);
        Assert.False(check.Critical); // warning, never a blocker
    }

    [Fact]
    public void ClassifyPendingReboot_BothSources_NamesBoth()
    {
        var check = PreflightService.ClassifyPendingReboot(true, true);
        Assert.NotNull(check);
        Assert.Contains("servicing", check!.Message);
        Assert.Contains("Windows Update", check.Message);
    }

    // --- Working-directory free space classification ---

    [Fact]
    public void ClassifyWorkingDirSpace_HealthyOrUnknown_ReturnsNull()
    {
        Assert.Null(PreflightService.ClassifyWorkingDirSpace(null, PreflightService.MinWorkingDirFreeBytes));
        Assert.Null(PreflightService.ClassifyWorkingDirSpace(PreflightService.MinWorkingDirFreeBytes, PreflightService.MinWorkingDirFreeBytes));
        Assert.Null(PreflightService.ClassifyWorkingDirSpace(50L * 1024 * 1024 * 1024, PreflightService.MinWorkingDirFreeBytes));
    }

    [Fact]
    public void ClassifyWorkingDirSpace_BelowFloor_Warns()
    {
        var check = PreflightService.ClassifyWorkingDirSpace(20L * 1024 * 1024, PreflightService.MinWorkingDirFreeBytes);
        Assert.NotNull(check);
        Assert.Equal(CheckStatus.Warning, check!.Status);
        Assert.Contains("20 MB", check.Message);
        Assert.False(check.Critical);
    }
}
