using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class AutoBenchmarkServiceTests
{
    [Fact]
    public void NoRegression_WhenBothArmsImprove()
    {
        var baseline = new BenchmarkBaseline { ReadIops = 100_000, WriteIops = 50_000 };
        var current = new BenchmarkBaseline { ReadIops = 120_000, WriteIops = 60_000 };
        var verdict = AutoBenchmarkService.Compare(baseline, current, thresholdPercent: 15);
        Assert.False(verdict.Regressed);
        Assert.Equal(20.0, verdict.ReadDeltaPercent, 1);
        Assert.Equal(20.0, verdict.WriteDeltaPercent, 1);
    }

    [Fact]
    public void Regression_WhenReadDropsBeyondThreshold()
    {
        var baseline = new BenchmarkBaseline { ReadIops = 100_000, WriteIops = 50_000 };
        var current = new BenchmarkBaseline { ReadIops = 80_000, WriteIops = 51_000 };
        var verdict = AutoBenchmarkService.Compare(baseline, current, thresholdPercent: 15);
        Assert.True(verdict.Regressed);
        Assert.Contains("REGRESSION", verdict.Summary);
    }

    [Fact]
    public void Regression_WhenWriteDropsBeyondThreshold()
    {
        var baseline = new BenchmarkBaseline { ReadIops = 100_000, WriteIops = 50_000 };
        var current = new BenchmarkBaseline { ReadIops = 100_500, WriteIops = 40_000 };
        var verdict = AutoBenchmarkService.Compare(baseline, current, thresholdPercent: 15);
        Assert.True(verdict.Regressed);
    }

    [Fact]
    public void ZeroBaseline_ProducesZeroDelta()
    {
        var baseline = new BenchmarkBaseline { ReadIops = 0, WriteIops = 0 };
        var current = new BenchmarkBaseline { ReadIops = 100, WriteIops = 100 };
        var verdict = AutoBenchmarkService.Compare(baseline, current, thresholdPercent: 5);
        Assert.Equal(0, verdict.ReadDeltaPercent);
        Assert.False(verdict.Regressed);
    }

    [Fact]
    public void SmallDrop_WithinThreshold_IsNotRegression()
    {
        var baseline = new BenchmarkBaseline { ReadIops = 100_000, WriteIops = 50_000 };
        var current = new BenchmarkBaseline { ReadIops = 95_000, WriteIops = 48_000 };
        var verdict = AutoBenchmarkService.Compare(baseline, current, thresholdPercent: 15);
        Assert.False(verdict.Regressed);
    }
}
