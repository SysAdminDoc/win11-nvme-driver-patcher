using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BootStorageControllerServiceTests
{
    [Fact]
    public void BuildInventory_ClassifiesInboxOemAndMissingBoundPackages()
    {
        var devices = new[]
        {
            Device("PCI\\VEN_8086&DEV_A77F\\1", "Intel VMD", "SCSIAdapter", "iaStorVD"),
            Device("PCI\\VEN_144D&DEV_A80D\\2", "Standard NVMe", "SCSIAdapter", "stornvme"),
            Device("ACPI\\VEN_TEST\\3", "Unknown RAID", "SCSIAdapter", "mystor")
        };
        var drivers = new[]
        {
            Driver(devices[0].InstanceId, "oem42.inf", "Intel", "20.2.6.1025"),
            Driver(devices[1].InstanceId, "stornvme.inf", "Microsoft", "10.0.26100.1")
        };

        var inventory = BootStorageControllerService.BuildInventory(devices, drivers, probeSucceeded: true);

        Assert.True(inventory.ProbeSucceeded);
        Assert.Equal(3, inventory.Controllers.Count);
        Assert.Equal(WinPEControllerCoverage.PendingInjection,
            inventory.Controllers.Single(c => c.ServiceName == "iaStorVD").Coverage);
        Assert.Equal(WinPEControllerCoverage.Inbox,
            inventory.Controllers.Single(c => c.ServiceName == "stornvme").Coverage);
        Assert.Equal(WinPEControllerCoverage.Missing,
            inventory.Controllers.Single(c => c.ServiceName == "mystor").Coverage);
        Assert.False(inventory.Complete);
    }

    [Fact]
    public void BuildInventory_ExcludesVirtualAndNonPresentControllers()
    {
        var devices = new[]
        {
            Device("SWD\\XVDDENUM\\ROOT", "Xvdd", "SCSIAdapter", "xvdd"),
            Device("ROOT\\SPACEPORT\\0000", "Storage Spaces", "SCSIAdapter", "spaceport"),
            Device("PCI\\VEN_1234&DEV_5678\\4", "Disabled RAID", "SCSIAdapter", "raid", error: 22),
            Device("VMBUS\\{GUID}\\5", "Hyper-V SCSI", "SCSIAdapter", "storvsc")
        };
        var drivers = new[]
        {
            Driver(devices[0].InstanceId, "oem39.inf", "Xbox", "1.0"),
            Driver(devices[1].InstanceId, "spaceport.inf", "Microsoft", "1.0"),
            Driver(devices[2].InstanceId, "oem7.inf", "Vendor", "1.0"),
            Driver(devices[3].InstanceId, "storvsc.inf", "Microsoft", "1.0")
        };

        var inventory = BootStorageControllerService.BuildInventory(devices, drivers, probeSucceeded: true);

        var controller = Assert.Single(inventory.Controllers);
        Assert.Equal("Hyper-V SCSI", controller.FriendlyName);
        Assert.Equal(WinPEControllerCoverage.Inbox, controller.Coverage);
    }

    [Fact]
    public void BuildInventory_DeduplicatesInstanceIdsAndProducesStableSourceFingerprint()
    {
        var instance = "PCI\\VEN_8086&DEV_A77F\\1";
        var inventory = BootStorageControllerService.BuildInventory(
            [Device(instance, "Intel VMD", "SCSIAdapter", "iaStorVD"),
             Device(instance.ToLowerInvariant(), "Duplicate", "SCSIAdapter", "iaStorVD")],
            [Driver(instance, "OEM42.INF", "Intel", "20.2.6.1025")],
            probeSucceeded: true);

        var controller = Assert.Single(inventory.Controllers);
        Assert.Equal("PCI\\VEN_8086&DEV_A77F\\1|oem42.inf|20.2.6.1025|iastorvd", controller.SourceFingerprint);
        Assert.True(BootStorageControllerService.IsOemInf("oem42.inf"));
        Assert.False(BootStorageControllerService.IsOemInf("iaStorVD.inf"));
    }

    [Fact]
    public void BuildInventory_PreservesTypedProbeFailure()
    {
        var inventory = BootStorageControllerService.BuildInventory(
            [], [], probeSucceeded: false, probeError: "Access denied");

        Assert.False(inventory.ProbeSucceeded);
        Assert.False(inventory.Complete);
        Assert.Contains("Access denied", inventory.Summary, StringComparison.Ordinal);
    }

    private static ControllerDeviceEvidence Device(
        string id, string name, string deviceClass, string service, uint error = 0) =>
        new(id, name, deviceClass, service, error);

    private static ControllerDriverEvidence Driver(
        string id, string inf, string provider, string version) =>
        new(id, inf, provider, version);
}
