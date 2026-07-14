using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class AutoBenchmarkCompareTests
{
    private static BenchmarkBaseline Base(double read, double write) =>
        new() { ReadIops = read, WriteIops = write };

    [Fact]
    public void Compare_DetectsRegression_WhenCurrentIsSlower()
    {
        var baseline = Base(100_000, 50_000);
        var current = Base(70_000, 50_000); // -30% read
        var verdict = AutoBenchmarkService.Compare(baseline, current, thresholdPercent: 15);
        Assert.True(verdict.Regressed);
        Assert.Contains("REGRESSION", verdict.Summary);
    }

    [Fact]
    public void Compare_NoRegression_WhenCurrentMatchesBaseline()
    {
        var baseline = Base(100_000, 50_000);
        var verdict = AutoBenchmarkService.Compare(baseline, baseline, thresholdPercent: 15);
        Assert.False(verdict.Regressed);
    }

    [Fact]
    public void FromResult_ProjectsIopsAndLatency()
    {
        var result = new BenchmarkResult
        {
            Timestamp = "2026-07-14T00:00:00Z",
            Read = new BenchmarkMetrics { IOPS = 123, AvgLatencyMs = 0.5 },
            Write = new BenchmarkMetrics { IOPS = 456, AvgLatencyMs = 0.9 }
        };
        var baseline = AutoBenchmarkService.FromResult(result);
        Assert.Equal(123, baseline.ReadIops);
        Assert.Equal(456, baseline.WriteIops);
        Assert.Equal(0.5, baseline.ReadLatencyMs);
        Assert.Equal(0.9, baseline.WriteLatencyMs);
    }

    [Fact]
    public void FromResultThenCompare_FlagsRealRegression()
    {
        // A post-patch benchmark projected via FromResult must be able to trip the regression gate.
        var baseline = Base(200_000, 100_000);
        var postPatch = new BenchmarkResult
        {
            Read = new BenchmarkMetrics { IOPS = 150_000 },  // -25%
            Write = new BenchmarkMetrics { IOPS = 100_000 }
        };
        var verdict = AutoBenchmarkService.Compare(baseline, AutoBenchmarkService.FromResult(postPatch), thresholdPercent: 15);
        Assert.True(verdict.Regressed);
    }
}
