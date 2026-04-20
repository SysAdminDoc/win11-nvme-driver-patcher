using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class FirmwareUpdateNudgeServiceTests
{
    [Theory]
    [InlineData("Samsung SSD 990 PRO 2TB", "Samsung")]
    [InlineData("WD_BLACK SN850X 2000GB", "Western Digital")]
    [InlineData("CT2000P5PSSD8", "Crucial")]
    [InlineData("SK hynix Platinum P41 1TB", "SK hynix")]
    [InlineData("KINGSTON SKC3000D2048G", "Kingston")]
    [InlineData("Sabrent Rocket 4 Plus", "Sabrent")]
    [InlineData("Intel SSDPEKNU", "Intel / Solidigm")]
    [InlineData("Solidigm P44 Pro", "Intel / Solidigm")]
    [InlineData("Phison E18 Reference", "Phison / OEM")]
    public void KnownVendor_MapsToVendorLandingPage(string model, string expectedVendor)
    {
        var nudge = FirmwareUpdateNudgeService.Lookup(model, "FW01");
        Assert.Equal(expectedVendor, nudge.Vendor);
        Assert.StartsWith("http", nudge.UpdateToolUrl);
    }

    [Fact]
    public void UnknownVendor_ReportsUnknown()
    {
        var nudge = FirmwareUpdateNudgeService.Lookup("Some random 2026 drive", "FW");
        Assert.Equal("Unknown", nudge.Vendor);
        Assert.Equal(string.Empty, nudge.UpdateToolUrl);
    }

    [Fact]
    public void EmptyModel_ReportsUnknown()
    {
        var nudge = FirmwareUpdateNudgeService.Lookup(string.Empty, "FW");
        Assert.Equal("Unknown", nudge.Vendor);
    }
}
