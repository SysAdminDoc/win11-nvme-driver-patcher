using NVMeDriverPatcher.Models;
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
    public void Summary_WithStorport129_NamesCommandTimeoutAndRevertGuidance()
    {
        var report = new WatchdogReport
        {
            Verdict = WatchdogVerdict.Warning,
            TotalEvents = 3,
            Counts =
            [
                new() { Source = "storport", Id = 129, Description = "Storport timeout", Count = 3 }
            ]
        };

        var s = EventLogWatchdogService.BuildSummary(report, StateWith(3, 6));

        Assert.Contains("command timeout (Storport 129)", s);
        Assert.Contains("Consider reverting", s);
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
        Assert.Contains("command timeout (Storport 129)", d);
        Assert.Contains("disk 51/153", d);
    }

    [Fact]
    public void CorruptState_IsUnavailableAndPreservedInsteadOfBecomingIdle()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var config = new AppConfig { WorkingDir = dir };
            var path = EventLogWatchdogService.StatePath(config);
            File.WriteAllText(path, "{ truncated");

            var loaded = EventLogWatchdogService.LoadStateWithStatus(config);
            var report = EventLogWatchdogService.Evaluate(config);

            Assert.False(loaded.Success);
            Assert.Equal(WatchdogStateAccessStatus.Unavailable, loaded.Status);
            Assert.Equal(WatchdogVerdict.Unavailable, report.Verdict);
            Assert.Equal("{ truncated", File.ReadAllText(path));
            Assert.NotEmpty(Directory.GetFiles(dir, "watchdog.json.corrupt*"));
            Assert.False(EventLogWatchdogService.Arm(config).Success);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Evaluate_LockTimeoutIsUnavailableWithoutReadingOrQuerying()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var config = new AppConfig { WorkingDir = dir };
        var mutexName = $@"Local\NVMeDriverPatcher.Watchdog.Tests.{Guid.NewGuid():N}";
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, mutexName);
            mutex.WaitOne();
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var queryCalls = 0;
            var report = EventLogWatchdogService.Evaluate(
                config,
                _ =>
                {
                    queryCalls++;
                    return new WatchdogEventQueryResult(true, [], "should not run");
                },
                DateTime.UtcNow,
                TimeSpan.FromMilliseconds(50),
                mutexName,
                (_, _) => new WatchdogStateSaveResult(WatchdogStateAccessStatus.Saved, "saved"));

            Assert.Equal(WatchdogVerdict.Unavailable, report.Verdict);
            Assert.Equal("StateBusy", report.FailureCode);
            Assert.Equal(0, queryCalls);
            Assert.False(File.Exists(EventLogWatchdogService.StatePath(config)));
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Evaluate_EventLogFailureIsUnavailableNotHealthy()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var config = ArmedConfig(dir);
            var report = EventLogWatchdogService.Evaluate(
                config,
                _ => new WatchdogEventQueryResult(false, [], "access denied", "EventLogAccessDenied"),
                new DateTime(2026, 7, 14, 1, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromSeconds(1),
                $@"Local\NVMeDriverPatcher.Watchdog.Tests.{Guid.NewGuid():N}",
                (_, _) => new WatchdogStateSaveResult(WatchdogStateAccessStatus.Saved, "saved"));

            Assert.Equal(WatchdogVerdict.Unavailable, report.Verdict);
            Assert.Equal("EventLogAccessDenied", report.FailureCode);
            Assert.False(report.DataAvailable);
            Assert.DoesNotContain("Stable", report.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Evaluate_PersistenceFailureIsUnavailableButRetainsProvedUnstableAction()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var config = ArmedConfig(dir);
            var counts = new List<WatchdogEventCount>
            {
                new() { Source = "disk", Id = 153, Count = 6, Description = "reset" }
            };
            var report = EventLogWatchdogService.Evaluate(
                config,
                _ => new WatchdogEventQueryResult(true, counts, "query succeeded"),
                new DateTime(2026, 7, 14, 1, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromSeconds(1),
                $@"Local\NVMeDriverPatcher.Watchdog.Tests.{Guid.NewGuid():N}",
                (_, _) => new WatchdogStateSaveResult(WatchdogStateAccessStatus.Unavailable, "disk full"));

            Assert.Equal(WatchdogVerdict.Unavailable, report.Verdict);
            Assert.Equal(WatchdogVerdict.Unstable, report.ObservedVerdict);
            Assert.Equal("StatePersistenceFailed", report.FailureCode);
            Assert.True(EventLogWatchdogService.ShouldAutoRevert(config, report));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StateWriters_SerializeUniqueAtomicPublicationWithValidatedBackup()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var config = new AppConfig { WorkingDir = dir };
            var writes = Enumerable.Range(1, 24).Select(index => Task.Run(() =>
                EventLogWatchdogService.SaveState(config, new WatchdogState
                {
                    PatchAppliedAt = "2026-07-14T00:00:00.0000000Z",
                    CumulativeEvents = index
                }))).ToArray();

            var results = await Task.WhenAll(writes);
            Assert.All(results, result => Assert.True(result.Success, result.Summary));
            Assert.True(EventLogWatchdogService.TryReadState(
                EventLogWatchdogService.StatePath(config), out var state, out var primaryError), primaryError);
            Assert.InRange(state!.CumulativeEvents, 1, 24);
            Assert.True(EventLogWatchdogService.TryReadState(
                EventLogWatchdogService.StatePath(config) + ".bak", out _, out var backupError), backupError);
            Assert.Empty(Directory.GetFiles(dir, "watchdog.json.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static AppConfig ArmedConfig(string dir)
    {
        var config = new AppConfig { WorkingDir = dir };
        var saved = EventLogWatchdogService.SaveState(config, new WatchdogState
        {
            PatchAppliedAt = "2026-07-14T00:00:00.0000000Z",
            AutoRevertEnabled = true
        });
        Assert.True(saved.Success, saved.Summary);
        return config;
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "NVMeDriverPatcher.Watchdog." + Guid.NewGuid().ToString("N"));
}
