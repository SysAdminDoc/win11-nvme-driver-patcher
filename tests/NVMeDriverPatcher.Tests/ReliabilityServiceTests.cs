using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ReliabilityServiceTests
{
    private static readonly DateTime Now = new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildCorrelationReport_ComputesPrePostAveragesAndDelta()
    {
        var patch = Now.AddDays(-2);
        var points = new[]
        {
            new ReliabilityPoint { Timestamp = Now.AddDays(-4), Index = 8 },
            new ReliabilityPoint { Timestamp = Now.AddDays(-3), Index = 10 },
            new ReliabilityPoint { Timestamp = Now.AddDays(-1), Index = 7 },
            new ReliabilityPoint { Timestamp = Now, Index = 5 }
        };

        var report = ReliabilityService.BuildCorrelationReport(points, patch, Now);

        Assert.True(report.DataAvailable);
        Assert.Equal(9, report.PrePatchAverage);
        Assert.Equal(6, report.PostPatchAverage);
        Assert.Equal(-3, report.Delta);
        Assert.Contains("pre-patch", report.Summary);
        Assert.Contains("post-patch", report.Summary);
    }

    [Fact]
    public void BuildCorrelationReport_WithoutPatchReportsOverallAverage()
    {
        var report = ReliabilityService.BuildCorrelationReport(
            [
                new ReliabilityPoint { Timestamp = Now.AddDays(-1), Index = 8 },
                new ReliabilityPoint { Timestamp = Now, Index = 10 }
            ],
            patchAppliedAt: null,
            utcNow: Now);

        Assert.True(report.DataAvailable);
        Assert.Null(report.Delta);
        Assert.Contains("average: 9.0", report.Summary);
    }

    [Fact]
    public void BuildCorrelationReport_FiltersOldPoints()
    {
        var report = ReliabilityService.BuildCorrelationReport(
            [
                new ReliabilityPoint { Timestamp = Now.AddDays(-31), Index = 1 },
                new ReliabilityPoint { Timestamp = Now.AddDays(-1), Index = 10 }
            ],
            patchAppliedAt: null,
            utcNow: Now);

        Assert.Single(report.Series);
        Assert.Equal(10, report.Series[0].Index);
        Assert.Contains("1 days", report.Summary);
    }

    [Fact]
    public void BuildCorrelationReport_WithPostOnlySamplesExplainsMissingBaseline()
    {
        var patch = Now.AddDays(-5);

        var report = ReliabilityService.BuildCorrelationReport(
            [
                new ReliabilityPoint { Timestamp = Now.AddDays(-1), Index = 7 },
                new ReliabilityPoint { Timestamp = Now, Index = 9 }
            ],
            patch,
            Now);

        Assert.Null(report.PrePatchAverage);
        Assert.Equal(8, report.PostPatchAverage);
        Assert.Contains("No pre-patch baseline", report.Summary);
    }
}
