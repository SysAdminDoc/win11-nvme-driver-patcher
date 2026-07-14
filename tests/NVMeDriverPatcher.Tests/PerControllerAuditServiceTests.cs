using System.Xml;
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
                    BoundDriverVersion = "10.0.26100.8521",
                    DeviceClass = "DiskDrive",
                    HardwareId = "SCSI\\DiskNVMe____",
                    CompatibleId = "GenNvmeDisk",
                },
            }
        };

        var text = report.RenderForcedDriverEvidence();

        Assert.Contains("nvmedisk.inf", text);
        Assert.Contains("Microsoft", text);
        Assert.Contains("10.0.26100.8521", text);
        Assert.Contains("DiskDrive", text);
        Assert.Contains("GenNvmeDisk", text);
        Assert.Contains("WD SN850X", text);
        // The note must steer a forced install to Device Manager, not registry cleanup.
        Assert.Contains("Device Manager", text);
        Assert.Contains("pnputil /enum-drivers /files", text);
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

    [Fact]
    public void FindCustomNativeWorkaroundEvidence_FlagsOemNativeInf()
    {
        var controller = new ControllerAudit
        {
            FriendlyName = "Custom Native",
            BoundDriver = "nvmedisk.sys",
            IsNative = true,
            InfName = "oem42.inf",
            DriverProvider = "Community Test"
        };

        var evidence = PerControllerAuditService.FindCustomNativeWorkaroundEvidence([controller]);

        Assert.Single(evidence);
        Assert.True(PerControllerAuditService.HasNonMicrosoftNativeInf(controller));
    }

    [Fact]
    public void FindCustomNativeWorkaroundEvidence_FlagsScsiDiskNvmeCustomMatch()
    {
        var controller = new ControllerAudit
        {
            FriendlyName = "Custom Match",
            BoundDriver = "nvmedisk.sys",
            IsNative = true,
            InfName = "nvmedisk.inf",
            DriverProvider = "Microsoft",
            HardwareIds = ["SCSI\\DiskNVMe____Custom_Model"]
        };

        var evidence = PerControllerAuditService.FindCustomNativeWorkaroundEvidence([controller]);

        Assert.Single(evidence);
        Assert.True(PerControllerAuditService.HasScsiDiskNvmeCustomMatch(controller));
    }

    [Fact]
    public void FindCustomNativeWorkaroundEvidence_IgnoresInboxNativeBinding()
    {
        var controller = new ControllerAudit
        {
            FriendlyName = "Inbox Native",
            BoundDriver = "nvmedisk.sys",
            IsNative = true,
            InfName = "nvmedisk.inf",
            DriverProvider = "Microsoft",
            CompatibleId = "GenNvmeDisk"
        };

        var evidence = PerControllerAuditService.FindCustomNativeWorkaroundEvidence([controller]);

        Assert.Empty(evidence);
    }

    [Fact]
    public void ParseDriverCandidatesXml_UsesExactDeviceAndSortsByHexRank()
    {
        const string instanceId = @"PCI\VEN_1234&DEV_5678\1";
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PnpUtil Version="10.0.26100">
              <Device InstanceId="PCI\VEN_1234&amp;DEV_5678\1">
                <DriverName>oem42.inf</DriverName>
                <MatchingDrivers>
                  <DriverName DriverName="oem42.inf">
                    <ProviderName>Vendor Corp</ProviderName>
                    <ClassName>SCSIAdapter</ClassName>
                    <ClassGuid>{4d36e97b-e325-11ce-bfc1-08002be10318}</ClassGuid>
                    <DriverVersion>07/01/2026 20.1.2.3</DriverVersion>
                    <SignerName>Vendor Publisher</SignerName>
                    <MatchingDeviceId>PCI\VEN_1234&amp;DEV_5678</MatchingDeviceId>
                    <Rank>00FF1001</Rank>
                    <Status>BestRanked/Installed</Status>
                  </DriverName>
                  <DriverName DriverName="stornvme.inf">
                    <ProviderName>Microsoft</ProviderName>
                    <ClassName>SCSIAdapter</ClassName>
                    <DriverVersion>06/21/2006 10.0.26100.8521</DriverVersion>
                    <MatchingDeviceId>PCI\CC_010802</MatchingDeviceId>
                    <Rank>00FF2006</Rank>
                    <Status></Status>
                  </DriverName>
                </MatchingDrivers>
              </Device>
            </PnpUtil>
            """;

        var candidates = PerControllerAuditService.ParseDriverCandidatesXml(xml, instanceId);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("oem42.inf", candidates[0].InfName);
        Assert.Equal("00FF1001", candidates[0].Rank);
        Assert.True(candidates[0].IsBestRanked);
        Assert.True(candidates[0].IsInstalled);
        Assert.Equal("Vendor Corp", candidates[0].Provider);
        Assert.Equal("stornvme.inf", candidates[1].InfName);
    }

    [Fact]
    public void ParseDriverCandidatesXml_RejectsNonMatchingDeviceAndDtd()
    {
        const string otherDevice = "<PnpUtil><Device InstanceId=\"PCI\\OTHER\"><MatchingDrivers /></Device></PnpUtil>";
        Assert.Throws<InvalidDataException>(() =>
            PerControllerAuditService.ParseDriverCandidatesXml(otherDevice, @"PCI\EXPECTED"));

        const string dtd = "<!DOCTYPE x [<!ENTITY test 'value'>]><PnpUtil />";
        Assert.ThrowsAny<XmlException>(() =>
            PerControllerAuditService.ParseDriverCandidatesXml(dtd, @"PCI\EXPECTED"));
    }

    [Fact]
    public void PopulateDriverCandidates_RecordsCommandFailureWithoutInventingCandidates()
    {
        var controller = new ControllerAudit { InstanceId = @"PCI\VEN_1234&DEV_5678\1" };
        var query = new PnpUtilDriverQueryResult(
            259,
            string.Empty,
            "Failed to enumerate matching drivers");

        PerControllerAuditService.PopulateDriverCandidates(controller, query);

        Assert.False(controller.DriverCandidateProbeSucceeded);
        Assert.Contains("exited 259", controller.DriverCandidateProbeError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/drivers /format xml", controller.DriverCandidateCommand, StringComparison.Ordinal);
        Assert.Empty(controller.DriverCandidates);
    }

    [Fact]
    public void RenderDriverCandidateEvidence_IncludesBoundSelectionRankAndProbeErrors()
    {
        var report = new PerControllerAuditReport
        {
            ObservedAtUtc = new DateTimeOffset(2026, 7, 14, 15, 0, 0, TimeSpan.Zero),
            Controllers =
            {
                new ControllerAudit
                {
                    FriendlyName = "VMD Controller",
                    InstanceId = @"PCI\VMD",
                    BoundDriver = "iaStorVD.sys",
                    InfName = "oem42.inf",
                    DriverProvider = "Intel",
                    BoundDriverVersion = "20.1.2.3",
                    DriverCandidateProbeSucceeded = true,
                    DriverCandidateCommand = "pnputil.exe /enum-devices ... /drivers /format xml",
                    DriverCandidates =
                    {
                        new ControllerDriverCandidate
                        {
                            InfName = "oem42.inf",
                            Provider = "Intel",
                            DriverVersion = "07/01/2026 20.1.2.3",
                            MatchingDeviceId = @"PCI\VEN_8086",
                            Rank = "00FF1001",
                            Status = "BestRanked/Installed"
                        }
                    }
                },
                new ControllerAudit
                {
                    FriendlyName = "Second Controller",
                    InstanceId = @"PCI\SECOND",
                    DriverCandidateProbeError = "PnPUtil timed out after 15 seconds."
                }
            }
        };

        var evidence = report.RenderDriverCandidateEvidence();

        Assert.Contains("2026-07-14T15:00:00", evidence, StringComparison.Ordinal);
        Assert.Contains("oem42.inf", evidence, StringComparison.Ordinal);
        Assert.Contains("rank=00FF1001", evidence, StringComparison.Ordinal);
        Assert.Contains("Candidate error: PnPUtil timed out", evidence, StringComparison.Ordinal);
    }
}
