using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The GUI's "vs. Previous" benchmark lines. HasMetrics is true when any single desktop value is
/// set, so a previous run can qualify with zero QD1 IOPS, and dividing by that logged NaN% or
/// Infinity% and skewed the high-QD versus QD1 verdict.
/// </summary>
public sealed class BenchmarkComparisonLogTests
{
    private static BenchmarkResult Run(
        string label,
        double read,
        double write,
        BenchmarkMetrics desktopRead,
        BenchmarkMetrics desktopWrite) => new()
        {
            Label = label,
            Read = new BenchmarkMetrics { IOPS = read },
            Write = new BenchmarkMetrics { IOPS = write },
            Desktop = new BenchmarkProfileResult { Read = desktopRead, Write = desktopWrite },
        };

    [Theory]
    [InlineData(0)]       // 0 / 0 was NaN%
    [InlineData(20000)]   // x / 0 was Infinity%
    public void ZeroPreviousDesktopIops_NeverLogsNaNOrInfinity(double currentDesktopRead)
    {
        // Only latency survived from the previous desktop profile, which is enough for HasMetrics.
        var prev = Run("Pre-Patch", 400000, 300000,
            new BenchmarkMetrics { IOPS = 0 },
            new BenchmarkMetrics { IOPS = 0, AvgLatencyMs = 0.05 });
        var result = Run("Post-Patch", 480000, 330000,
            new BenchmarkMetrics { IOPS = currentDesktopRead, AvgLatencyMs = 0.05 },
            new BenchmarkMetrics { IOPS = 18000 });
        Assert.True(prev.Desktop.HasMetrics);

        var lines = MainViewModel.BuildBenchmarkComparison(prev, result);

        Assert.All(lines, line =>
        {
            Assert.DoesNotContain("NaN", line.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Infinity", line.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("∞", line.Text, StringComparison.Ordinal);
        });
        Assert.Contains(lines, line =>
            line.Text.Contains("Desktop QD1 Read:", StringComparison.Ordinal) &&
            line.Text.Contains("no earlier value to compare", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Text.Contains("Summary:", StringComparison.Ordinal));
    }

    [Fact]
    public void RealPreviousValues_StillReportPercentagesAndTheQd1Verdict()
    {
        var prev = Run("Pre-Patch", 400000, 300000,
            new BenchmarkMetrics { IOPS = 20000 },
            new BenchmarkMetrics { IOPS = 18000 });
        var result = Run("Post-Patch", 480000, 330000,
            new BenchmarkMetrics { IOPS = 19800 },
            new BenchmarkMetrics { IOPS = 18000 });

        var lines = MainViewModel.BuildBenchmarkComparison(prev, result);

        Assert.Contains(lines, line => line.Text.Contains("Read IOPS:", StringComparison.Ordinal) &&
            line.Text.Contains("(+20%)", StringComparison.Ordinal) && line.Level == "SUCCESS");
        Assert.Contains(lines, line => line.Text.Contains("Desktop QD1 Read:", StringComparison.Ordinal) &&
            line.Text.Contains("(-1%)", StringComparison.Ordinal) && line.Level == "WARNING");
        Assert.Contains(lines, line => line.Text.Contains("Summary: High-QD improved while desktop QD1 did not.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 100, null)]
    [InlineData(-1, 100, null)]
    [InlineData(100, double.NaN, null)]
    [InlineData(100, 110, 10.0)]
    [InlineData(200, 150, -25.0)]
    public void IopsChangePercent_IsNullWithoutAPreviousValue(double previous, double current, double? expected)
    {
        Assert.Equal(expected, MainViewModel.IopsChangePercent(previous, current));
    }
}
