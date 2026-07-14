using NVMeDriverPatcher.Data;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BypassIoHistoryTests
{
    [Fact]
    public void BypassIoHistoryRecord_HasExpectedDefaults()
    {
        var record = new BypassIoHistoryRecord();
        Assert.Equal(string.Empty, record.VolumeLetter);
        Assert.Equal(string.Empty, record.Stack);
        Assert.Equal(string.Empty, record.Description);
        Assert.False(record.Enabled);
        Assert.False(record.IsPrePatch);
    }

    [Fact]
    public void BypassIoHistoryRecord_RoundTrips()
    {
        var record = new BypassIoHistoryRecord
        {
            VolumeLetter = @"C:\",
            Enabled = true,
            Stack = "stornvme.sys",
            Description = "Before patch install",
            IsPrePatch = true,
            Timestamp = DateTime.UtcNow
        };

        Assert.Equal(@"C:\", record.VolumeLetter);
        Assert.True(record.Enabled);
        Assert.Equal("stornvme.sys", record.Stack);
        Assert.True(record.IsPrePatch);
    }

    [Fact]
    public void BypassIoInspectorService_Inspect_ReturnsListWithoutCrashing()
    {
        var list = BypassIoInspectorService.Inspect();
        Assert.NotNull(list);
        foreach (var v in list)
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Letter));
            Assert.False(string.IsNullOrWhiteSpace(v.Status));
        }
    }

    [Fact]
    public void BuildBypassIoGamingImpact_NvmediskBlocked_NamesGamesAndGlobalScope()
    {
        var impact = DriveService.BuildBypassIoGamingImpact(new BypassIOResult
        {
            Supported = false,
            StorageType = "NVMe",
            BlockedBy = "nvmedisk.sys"
        });

        Assert.Contains("Ratchet & Clank: Rift Apart", impact);
        Assert.Contains("Forspoken", impact);
        Assert.Contains("Forza Motorsport", impact);
        Assert.Contains("Horizon Forbidden West", impact);
        Assert.Contains("nvmedisk.sys", impact);
        Assert.Contains("machine-wide", impact);
        Assert.Contains("cannot be excluded", impact);
    }

    [Fact]
    public void BuildBypassIoGamingImpact_EasyAntiCheatBlocked_NamesIndependentVeto()
    {
        var impact = DriveService.BuildBypassIoGamingImpact(new BypassIOResult
        {
            Supported = false,
            StorageType = "NVMe",
            BlockedBy = "EOSSys.sys"
        });

        Assert.Contains("EasyAntiCheat", impact);
        Assert.Contains("EOSSys.sys", impact);
        Assert.Contains("address that driver separately", impact);
    }

    [Fact]
    public void BuildBypassIoGamingImpact_CurrentlySupported_WarnsPatchCanRegressGames()
    {
        var impact = DriveService.BuildBypassIoGamingImpact(new BypassIOResult
        {
            Supported = true,
            StorageType = "NVMe",
            DriverCompat = "stornvme.sys"
        });

        Assert.Contains("currently available", impact);
        Assert.Contains("nvmedisk.sys", impact);
        Assert.Contains("legacy I/O", impact);
    }

    [Fact]
    public void BuildGamingImpactSummary_EnabledVolumes_NamesGamesAndVolumes()
    {
        var impact = BypassIoInspectorService.BuildGamingImpactSummary(
        [
            new BypassIoVolumeInfo { Letter = "D:", Enabled = true },
            new BypassIoVolumeInfo { Letter = "E:", Enabled = false },
            new BypassIoVolumeInfo { Letter = "F:", Enabled = true },
        ]);

        Assert.Contains("D:", impact);
        Assert.Contains("F:", impact);
        Assert.DoesNotContain("E:", impact);
        Assert.Contains("Ratchet & Clank: Rift Apart", impact);
        Assert.Contains("game-library drive", impact);
        Assert.Contains("machine-wide", impact);
    }

    [Fact]
    public void DataService_GetBypassIoHistory_ReturnsEmptyListBeforeAnyWrites()
    {
        DataService.Initialize();
        var history = DataService.GetBypassIoHistory();
        Assert.NotNull(history);
    }

    [Fact]
    public void DataService_GetBypassIoLatestPair_ReturnsEmptyBeforeAnyWrites()
    {
        DataService.Initialize();
        var (pre, post) = DataService.GetBypassIoLatestPair();
        Assert.NotNull(pre);
        Assert.NotNull(post);
    }
}
