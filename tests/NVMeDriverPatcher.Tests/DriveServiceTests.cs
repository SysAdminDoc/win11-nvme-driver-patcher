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

    [Theory]
    [InlineData("C:", "C:\\")]
    [InlineData("c:", "C:\\")]
    [InlineData("D:\\", "D:\\")]
    [InlineData(" e: ", "E:\\")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("1:", null)]
    [InlineData("AA:", null)]
    public void NormalizeDriveRoot_AcceptsOnlyDriveRoots(string? raw, string? expected)
    {
        Assert.Equal(expected, DriveService.NormalizeDriveRoot(raw));
    }

    [Theory]
    [InlineData("DiskInfo64.exe")]
    [InlineData("DiskInfo32.exe")]
    [InlineData("DiskInfoA64.exe")]
    [InlineData("CrystalDiskInfo.exe")]
    [InlineData(@"C:\Tools\CrystalDiskInfo\DiskInfo64.exe")]
    [InlineData(@"C:\Program Files\CrystalDiskInfo")]
    public void IsCrystalDiskInfoName_MatchesKnownProcessAndInstallPaths(string candidate)
    {
        Assert.True(DriveService.IsCrystalDiskInfoName(candidate));
    }

    [Theory]
    [InlineData("CrystalDiskMark.exe")]
    [InlineData("DiskInfoCollector.exe")]
    [InlineData(@"C:\Tools\CrystalDiskMark\DiskMark64.exe")]
    [InlineData("")]
    public void IsCrystalDiskInfoName_DoesNotMatchAdjacentTools(string candidate)
    {
        Assert.False(DriveService.IsCrystalDiskInfoName(candidate));
    }

    [Fact]
    public void ServiceFixture_CrystalDiskInfo_GetsMediumSmartWarning()
    {
        var findings = DriveService.DetectServiceIncompatibilities(["DiskInfo64.exe"]);

        var crystal = Assert.Single(findings.Where(f => f.Name == "CrystalDiskInfo"));
        Assert.Equal("Medium", crystal.Severity);
        Assert.Contains("SCSI pass-through", crystal.Message);
        Assert.Contains("Get-StorageReliabilityCounter", crystal.Message);
    }

    [Fact]
    public void IsLaptopChassis_DetectsLaptop_RegardlessOfWmiArrayBoxing()
    {
        // WMI returns ChassisTypes boxed differently across SKUs/VMs/OEM images. Laptop(9) must
        // be detected no matter the element type — the old `is ushort[]` cast missed int[]/uint[].
        Assert.True(DriveService.IsLaptopChassis(new ushort[] { 9 }));
        Assert.True(DriveService.IsLaptopChassis(new int[] { 9 }));
        Assert.True(DriveService.IsLaptopChassis(new uint[] { 9 }));
        Assert.True(DriveService.IsLaptopChassis(new object[] { (ushort)10 }));   // Notebook
        Assert.True(DriveService.IsLaptopChassis(new int[] { 3, 31 }));           // Convertible among desktop codes
    }

    [Fact]
    public void IsLaptopChassis_FalseForDesktopNullOrEmpty()
    {
        Assert.False(DriveService.IsLaptopChassis(new int[] { 3 }));   // Desktop
        Assert.False(DriveService.IsLaptopChassis(new ushort[] { 7 })); // Tower
        Assert.False(DriveService.IsLaptopChassis(Array.Empty<int>()));
        Assert.False(DriveService.IsLaptopChassis(null));
        Assert.False(DriveService.IsLaptopChassis("not an array"));
    }
}
