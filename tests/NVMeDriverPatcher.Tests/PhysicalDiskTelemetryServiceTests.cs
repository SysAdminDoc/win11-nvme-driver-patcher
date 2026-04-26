using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// The live `Collect()` path requires WMI and a real `root\Microsoft\Windows\Storage`
// namespace — exercising it in unit tests would either be a no-op (no disks) or a
// non-deterministic live-machine test. Instead we test the small pure helpers that
// guard the WHERE-clause injection fix (v4.6).
public sealed class PhysicalDiskTelemetryServiceTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("0", "0")]
    [InlineData("42", "42")]
    [InlineData("it's", @"it\'s")]                 // single quote escaped
    [InlineData(@"C:\path\with\backslashes",
                @"C:\\path\\with\\backslashes")]  // backslash escaped first so the quote
                                                   // escape doesn't double-escape the \
    [InlineData(@"mix'ed\path\'s", @"mix\'ed\\path\\\'s")]
    public void EscapeWqlString_HandlesQuotesAndBackslashes(string input, string expected)
    {
        Assert.Equal(expected, PhysicalDiskTelemetryService.EscapeWqlString(input));
    }

    [Fact]
    public void HealthStatusName_HandlesKnownCodes()
    {
        Assert.Equal("Healthy", PhysicalDiskTelemetryService.HealthStatusName((ushort)0));
        Assert.Equal("Warning", PhysicalDiskTelemetryService.HealthStatusName((ushort)1));
        Assert.Equal("Unhealthy", PhysicalDiskTelemetryService.HealthStatusName((ushort)2));
        Assert.Equal("Unknown", PhysicalDiskTelemetryService.HealthStatusName((ushort)5));
    }

    [Fact]
    public void HealthStatusName_FallsBackForUnknownCode()
    {
        // An unknown ushort falls through to the default arm so support can see the raw
        // value instead of a misleading "Healthy" (the previous default was the first switch
        // arm, which would silently lie about a disk in a warning state).
        Assert.Equal("Health(99)", PhysicalDiskTelemetryService.HealthStatusName((ushort)99));
    }

    [Fact]
    public void HealthStatusName_HandlesNullAndWrongType()
    {
        Assert.Equal("Unknown", PhysicalDiskTelemetryService.HealthStatusName(null));
        Assert.Equal("Unknown", PhysicalDiskTelemetryService.HealthStatusName("not a ushort"));
    }

    [Fact]
    public void MediaTypeName_RecognizesCommonTypes()
    {
        Assert.Equal("SSD", PhysicalDiskTelemetryService.MediaTypeName((ushort)4));
        Assert.Equal("HDD", PhysicalDiskTelemetryService.MediaTypeName((ushort)3));
        Assert.Equal("SCM", PhysicalDiskTelemetryService.MediaTypeName((ushort)5));
    }

    [Fact]
    public void BusTypeName_RecognizesNvme()
    {
        Assert.Equal("NVMe", PhysicalDiskTelemetryService.BusTypeName((ushort)17));
        Assert.Equal("SATA", PhysicalDiskTelemetryService.BusTypeName((ushort)11));
        Assert.Equal("USB", PhysicalDiskTelemetryService.BusTypeName((ushort)7));
    }
}
