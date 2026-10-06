using NVMeDriverPatcher.Data;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BypassIoHistoryDiffTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private static BypassIoHistoryRecord Row(DateTime at, string volume, bool enabled, bool pre = false) =>
        new() { Timestamp = at, VolumeLetter = volume, Enabled = enabled, Stack = enabled ? "stornvme" : "nvmedisk", IsPrePatch = pre };

    [Fact]
    public void OnlyTheNewestSnapshotOnEachSideCounts()
    {
        // Newest first, the way DataService returns them: two pre snapshots, two post snapshots.
        List<BypassIoHistoryRecord> pre =
        [
            Row(T0.AddDays(2), "C:", true, pre: true), Row(T0.AddDays(2), "D:", true, pre: true),
            Row(T0, "C:", false, pre: true), Row(T0, "E:", true, pre: true),
        ];
        List<BypassIoHistoryRecord> post =
        [
            Row(T0.AddDays(3), "C:", true), Row(T0.AddDays(3), "D:", false),
            Row(T0.AddDays(1), "C:", false), Row(T0.AddDays(1), "D:", false),
        ];

        var diff = BypassIoInspectorService.DiffLatestPair(pre, post);

        Assert.Equal(T0.AddDays(2), diff.Pre!.TakenAt);
        Assert.Equal(["C:", "D:"], diff.Pre.Volumes.Select(v => v.VolumeLetter));
        Assert.Equal(T0.AddDays(3), diff.Post!.TakenAt);
        Assert.Equal(["D:"], diff.LostAfterPatch);   // C: kept it; E: isn't in the newest pre snapshot
        Assert.True(diff.Recorded);
    }

    [Fact]
    public void AVolumeMissingFromThePostSnapshot_IsNotCalledLost()
    {
        var diff = BypassIoInspectorService.DiffLatestPair(
            [Row(T0, "C:", true, pre: true), Row(T0, "D:", true, pre: true)],
            [Row(T0.AddHours(1), "C:", true)]);

        Assert.Empty(diff.LostAfterPatch);
    }

    [Fact]
    public void OneSidedHistory_IsRecordedWithoutALostList()
    {
        var preOnly = BypassIoInspectorService.DiffLatestPair([Row(T0, "C:", true, pre: true)], []);
        Assert.True(preOnly.Recorded);
        Assert.Null(preOnly.Post);
        Assert.Empty(preOnly.LostAfterPatch);

        var nothing = BypassIoInspectorService.DiffLatestPair([], []);
        Assert.False(nothing.Recorded);
        Assert.Null(nothing.Pre);
        Assert.Null(nothing.Post);
    }
}
