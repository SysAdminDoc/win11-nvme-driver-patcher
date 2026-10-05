using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class DriveServiceTests
{
    // Found in issue #18's support bundle: on 25H2 (26200.9457) nvmedisk.sys is loaded at boot on
    // every PC whether or not a drive binds to it. A running service with no Storage disks device
    // and no nvmedisk.inf binding is the legacy stack, not native NVMe.
    [Fact]
    public void ClassifyNativeNVMe_LoadedServiceWithNoBoundDrive_IsLegacy()
    {
        var status = DriveService.ClassifyNativeNVMe(serviceRunning: true, storageDisks: [], boundInfVersion: null);

        Assert.False(status.IsActive);
        Assert.Equal("Disk drives (legacy)", status.DeviceCategory);
        Assert.Contains("no drive is bound", status.Details);
    }

    [Fact]
    public void ClassifyNativeNVMe_RequiresDeviceBindingEvidence()
    {
        var byClass = DriveService.ClassifyNativeNVMe(true, ["SPCC M.2 PCIe SSD"], null);
        Assert.True(byClass.IsActive);
        Assert.Equal("Storage disks", byClass.DeviceCategory);
        Assert.Equal(["SPCC M.2 PCIe SSD"], byClass.StorageDisks);

        var byInf = DriveService.ClassifyNativeNVMe(false, [], "10.0.26100.9278");
        Assert.True(byInf.IsActive);
        Assert.Equal("nvmedisk.sys v10.0.26100.9278", byInf.ActiveDriver);

        var legacy = DriveService.ClassifyNativeNVMe(false, [], null);
        Assert.False(legacy.IsActive);
        Assert.Equal("Legacy NVMe stack active (pre-patch or reboot required)", legacy.Details);
    }

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

        var crystal = Assert.Single(findings, f => f.Name == "CrystalDiskInfo");
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
