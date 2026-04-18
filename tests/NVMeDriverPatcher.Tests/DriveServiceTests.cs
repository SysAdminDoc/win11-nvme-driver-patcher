using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class DriveServiceTests
{
    [Theory]
    [InlineData(0, "Healthy")]
    [InlineData(1, "Warning")]
    [InlineData(2, "Unhealthy")]
    [InlineData(5, "Unknown")]
    [InlineData(99, "Code 99")]
    public void DescribeHealthStatus_FormatsKnownAndUnknownCodes(int code, string expected)
    {
        Assert.Equal(expected, DriveService.DescribeHealthStatus(code));
    }

    [Fact]
    public void DescribeOperationalStatus_FormatsStatusArrays()
    {
        ushort[] raw = [2, 3, 0xD001];

        var text = DriveService.DescribeOperationalStatus(raw);

        Assert.Equal("OK, Degraded, Incomplete", text);
    }

    [Fact]
    public void DescribeOperationalStatus_ReturnsUnknownWhenNoCodesExist()
    {
        Assert.Equal("Unknown", DriveService.DescribeOperationalStatus(null));
    }
}
