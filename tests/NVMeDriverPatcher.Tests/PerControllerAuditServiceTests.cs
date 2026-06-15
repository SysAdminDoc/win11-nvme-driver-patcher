using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PerControllerAuditServiceTests
{
    [Fact]
    public void RenderForcedDriverEvidence_NoNativeControllers_SaysNothingToCapture()
    {
        var report = new PerControllerAuditReport
        {
            Controllers =
            {
                new ControllerAudit { FriendlyName = "Samsung SSD", BoundDriver = "stornvme.sys", IsNative = false },
            }
        };

        var text = report.RenderForcedDriverEvidence();

        Assert.Contains("No nvmedisk.sys-bound controllers", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderForcedDriverEvidence_NativeController_IncludesAllPnPEvidenceFields()
    {
        var report = new PerControllerAuditReport
        {
            Controllers =
            {
                new ControllerAudit
                {
                    FriendlyName = "WD SN850X",
                    InstanceId = "SCSI\\DISK&VEN_NVME&PROD_WD",
                    BoundDriver = "nvmedisk.sys",
                    IsNative = true,
                    InfName = "nvmedisk.inf",
                    DriverProvider = "Microsoft",
                    DeviceClass = "DiskDrive",
                    HardwareId = "SCSI\\DiskNVMe____",
                    CompatibleId = "GenNvmeDisk",
                },
            }
        };

        var text = report.RenderForcedDriverEvidence();

        Assert.Contains("nvmedisk.inf", text);
        Assert.Contains("Microsoft", text);
        Assert.Contains("DiskDrive", text);
        Assert.Contains("GenNvmeDisk", text);
        Assert.Contains("WD SN850X", text);
        // The note must steer a forced install to Device Manager, not registry cleanup.
        Assert.Contains("Device Manager", text);
    }

    [Fact]
    public void RenderForcedDriverEvidence_MissingFields_RenderUnknownPlaceholder()
    {
        var report = new PerControllerAuditReport
        {
            Controllers =
            {
                new ControllerAudit { FriendlyName = "Generic NVMe", BoundDriver = "nvmedisk.sys", IsNative = true },
            }
        };

        var text = report.RenderForcedDriverEvidence();

        Assert.Contains("(unknown)", text);
    }

    [Fact]
    public void RenderForcedDriverEvidence_OnlyNativeControllersAppear()
    {
        var report = new PerControllerAuditReport
        {
            Controllers =
            {
                new ControllerAudit { FriendlyName = "Legacy Drive", BoundDriver = "stornvme.sys", IsNative = false },
                new ControllerAudit { FriendlyName = "Native Drive", BoundDriver = "nvmedisk.sys", IsNative = true },
            }
        };

        var text = report.RenderForcedDriverEvidence();

        Assert.Contains("Native Drive", text);
        Assert.DoesNotContain("Legacy Drive", text);
    }
}
