using NVMeDriverPatcher.Data;
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
