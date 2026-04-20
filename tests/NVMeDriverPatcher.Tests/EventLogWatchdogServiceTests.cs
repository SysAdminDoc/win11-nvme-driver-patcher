using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// BuildSummary is the safe surface to cover — it's pure, fed by the synthetic report +
// state structures. The real Evaluate path hits the Windows event log and is covered by
// manual testing. These tests pin the user-facing messaging ladder.
public sealed class EventLogWatchdogServiceTests
{
    private static WatchdogState StateWith(int warn, int revert, bool autoRevert = true) =>
        new() { WarnThreshold = warn, RevertThreshold = revert, AutoRevertEnabled = autoRevert };

    [Fact]
    public void Healthy_SummaryMentionsStable()
    {
        var report = new WatchdogReport { Verdict = WatchdogVerdict.Healthy, TotalEvents = 0 };
        var s = EventLogWatchdogService.BuildSummary(report, StateWith(3, 6));
        Assert.Contains("Stable", s);
    }

    [Fact]
    public void Warning_SummaryMentionsElevated()
    {
        var report = new WatchdogReport { Verdict = WatchdogVerdict.Warning, TotalEvents = 4 };
        var s = EventLogWatchdogService.BuildSummary(report, StateWith(3, 6));
        Assert.Contains("Elevated", s);
    }

    [Fact]
    public void Unstable_SummaryMentionsAutoRevertEligible()
    {
        var report = new WatchdogReport { Verdict = WatchdogVerdict.Unstable, TotalEvents = 7 };
        var s = EventLogWatchdogService.BuildSummary(report, StateWith(3, 6));
        Assert.Contains("auto-revert eligible", s);
    }

    [Fact]
    public void Completed_SummaryMentionsConsideredStable()
    {
        var report = new WatchdogReport { Verdict = WatchdogVerdict.Completed, TotalEvents = 1 };
        var s = EventLogWatchdogService.BuildSummary(report, StateWith(3, 6));
        Assert.Contains("considered stable", s);
    }

    [Fact]
    public void Idle_SummaryMentionsIdle()
    {
        var report = new WatchdogReport { Verdict = WatchdogVerdict.Idle };
        var s = EventLogWatchdogService.BuildSummary(report, StateWith(3, 6));
        Assert.Contains("idle", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detail_IncludesThresholdsAndWindow()
    {
        var report = new WatchdogReport
        {
            Verdict = WatchdogVerdict.Healthy,
            WindowStart = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            WindowEnd = new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc),
            Counts = new List<WatchdogEventCount>
            {
                new() { Source = "storport", Id = 129, Description = "Storport timeout", Count = 2 }
            }
        };
        var d = EventLogWatchdogService.BuildDetail(report, StateWith(3, 6));
        Assert.Contains("Warn threshold: 3", d);
        Assert.Contains("Revert threshold: 6", d);
        Assert.Contains("Storport timeout", d);
    }
}
