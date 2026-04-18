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
    public void ParseDiskSpdOutput_ReturnsEmptyMetricsWhenTotalLineIsMissing()
    {
        var metrics = BenchmarkService.ParseDiskSpdOutput("diskspd failed before printing totals");

        Assert.Equal(0, metrics.ThroughputMBs);
        Assert.Equal(0, metrics.IOPS);
        Assert.Equal(0, metrics.AvgLatencyMs);
    }
}
