using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BenchmarkServiceTests
{
    [Fact]
    public void ParseDiskSpdOutput_ParsesTotalMetricsLine()
    {
        const string rawOutput = """
            some heading
            total: |       0 |      0 |    324.55 |   81234.0 |      0.241 |
            trailing text
            """;

        var metrics = BenchmarkService.ParseDiskSpdOutput(rawOutput);

        Assert.Equal(324.55, metrics.ThroughputMBs);
        Assert.Equal(81234, metrics.IOPS);
        Assert.Equal(0.241, metrics.AvgLatencyMs);
    }

    [Fact]
    public void ParseDiskSpdOutput_HandlesThousandsSeparators()
    {
        const string rawOutput = """
            total: |       0 |      0 |    1,234.56 |   81,234.0 |      0.241 |
            """;

        var metrics = BenchmarkService.ParseDiskSpdOutput(rawOutput);

        Assert.Equal(1234.56, metrics.ThroughputMBs);
        Assert.Equal(81234, metrics.IOPS);
        Assert.Equal(0.241, metrics.AvgLatencyMs);
    }

    [Fact]
    public void ParseDiskSpdOutput_HandlesGroupedIntegerMetrics()
    {
        const string rawOutput = """
            total: |       0 |      0 |    1,234 |   81,234 |      0.241 |
            """;

        var metrics = BenchmarkService.ParseDiskSpdOutput(rawOutput);

        Assert.Equal(1234, metrics.ThroughputMBs);
        Assert.Equal(81234, metrics.IOPS);
        Assert.Equal(0.241, metrics.AvgLatencyMs);
    }

    [Fact]
    public void ParseDiskSpdOutput_HandlesCommaDecimalSeparators()
    {
        const string rawOutput = """
            total: |       0 |      0 |    324,55 |   81234,0 |      0,241 |
            """;

        var metrics = BenchmarkService.ParseDiskSpdOutput(rawOutput);

        Assert.Equal(324.55, metrics.ThroughputMBs);
        Assert.Equal(81234, metrics.IOPS);
        Assert.Equal(0.241, metrics.AvgLatencyMs);
    }

    [Fact]
    public void ParseDiskSpdOutput_ReturnsEmptyMetricsWhenTotalLineIsMissing()
    {
        var metrics = BenchmarkService.ParseDiskSpdOutput("diskspd failed before printing totals");

        Assert.Equal(0, metrics.ThroughputMBs);
        Assert.Equal(0, metrics.IOPS);
        Assert.Equal(0, metrics.AvgLatencyMs);
    }

    [Fact]
    public void CreateDiskSpdArguments_PassesTargetPathAsSingleArgument()
    {
        const string target = @"C:\Users\alice\NVMe Bench\diskspd_test.dat";

        var args = BenchmarkService.CreateDiskSpdArguments(writePercent: 100, target);

        Assert.Contains("-w100", args);
        Assert.Equal(target, args[^1]);
        Assert.DoesNotContain(args, arg => arg.Contains('"'));
    }

    [Fact]
    public void ReportProgress_SwallowsCallbackFailures()
    {
        BenchmarkService.ReportProgress((_, _) => throw new InvalidOperationException("UI closed"), 50, "halfway");
    }

    [Theory]
    [InlineData("https://github.com/microsoft/diskspd/releases/latest/download/DiskSpd.ZIP", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/test", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/test", true)]
    [InlineData("http://github.com/microsoft/diskspd/releases/latest/download/DiskSpd.ZIP", false)]
    [InlineData("https://example.com/DiskSpd.ZIP", false)]
    public void IsTrustedAssetUri_AllowsOnlyHttpsGitHubAssetHosts(string url, bool expected)
    {
        Assert.Equal(expected, BenchmarkService.IsTrustedAssetUri(new Uri(url)));
    }

    [Fact]
    public void SanitizeBenchmarkHistory_NormalizesNullEditableJsonFields()
    {
        var parsed = new List<BenchmarkResult?>
        {
            null,
            new()
            {
                Label = "",
                Timestamp = null!,
                Read = null!,
                Write = null!
            }
        };

        var sanitized = BenchmarkService.SanitizeBenchmarkHistory(parsed);

        var result = Assert.Single(sanitized);
        Assert.Equal("benchmark", result.Label);
        Assert.Equal(string.Empty, result.Timestamp);
        Assert.NotNull(result.Read);
        Assert.NotNull(result.Write);
    }
}
