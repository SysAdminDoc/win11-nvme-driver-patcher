using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BypassIoInfReaderTests
{
    private const string DeclaringInf = """
        ; fixture shaped like a storport miniport INF
        [Version]
        Signature = "$Windows NT$"
        Class = SCSIAdapter

        [StorNVMe_Install.NT.Services]
        AddService = stornvme, %SPSVCINST_ASSOCSERVICE%, StorNVMe_ServiceInstall, StorNVMe_EventLog

        [StorNVMe_ServiceInstall]
        DisplayName = %StorNVMe_Svc%
        ServiceType = %SERVICE_KERNEL_DRIVER%
        AddReg = StorNVMe_Parameters_AddReg, StorNVMe_Other_AddReg

        [StorNVMe_Parameters_AddReg]
        HKR, Parameters, StorageSupportedFeatures, %REG_DWORD%, 1 ; opt in to BypassIO

        [StorNVMe_Other_AddReg]
        HKR, Parameters\Device, Unrelated, 0x00010001, 7

        [Strings]
        SPSVCINST_ASSOCSERVICE = 0x00000002
        REG_DWORD = 0x00010001
        StorNVMe_Svc = "Microsoft NVM Express"
        """;

    private const string SilentInf = """
        [StorAhci_Install.NT.Services]
        AddService = storahci, 0x00000002, StorAhci_ServiceInstall

        [StorAhci_ServiceInstall]
        ServiceType = 1
        AddReg = StorAhci_AddReg

        [StorAhci_AddReg]
        HKR, Parameters\Device, NumberOfRequests, 0x00010001, 0x20
        """;

    [Fact]
    public void Evaluate_ServiceSetsStorageSupportedFeatures_IsDeclared()
    {
        Assert.Equal(BypassIoInfDeclaration.Declared, BypassIoInfReader.Evaluate(DeclaringInf, "stornvme"));
    }

    [Fact]
    public void Evaluate_ServiceInstalledWithoutTheValue_IsNotDeclared()
    {
        Assert.Equal(BypassIoInfDeclaration.NotDeclared, BypassIoInfReader.Evaluate(SilentInf, "storahci"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0x2")]
    public void Evaluate_ValueWithoutTheBypassIoBit_IsNotDeclared(string data)
    {
        var inf = DeclaringInf.Replace("%REG_DWORD%, 1", "%REG_DWORD%, " + data);
        Assert.Equal(BypassIoInfDeclaration.NotDeclared, BypassIoInfReader.Evaluate(inf, "stornvme"));
    }

    [Fact]
    public void Evaluate_InfThatDoesNotInstallTheService_IsUnknown()
    {
        Assert.Equal(BypassIoInfDeclaration.Unknown, BypassIoInfReader.Evaluate(DeclaringInf, "nvmedisk"));
        Assert.Equal(BypassIoInfDeclaration.Unknown, BypassIoInfReader.Evaluate(null, "stornvme"));
        Assert.Equal(BypassIoInfDeclaration.Unknown, BypassIoInfReader.Evaluate("   ", "stornvme"));
    }

    [Fact]
    public void Evaluate_IsCaseInsensitiveAndIgnoresAValueUnderAnotherKey()
    {
        var inf = DeclaringInf.Replace("HKR, Parameters, StorageSupportedFeatures", "hkr, parameters, storagesupportedfeatures");
        Assert.Equal(BypassIoInfDeclaration.Declared, BypassIoInfReader.Evaluate(inf, "STORNVME"));

        var elsewhere = DeclaringInf.Replace("HKR, Parameters, StorageSupportedFeatures", "HKR, Parameters\\Device, StorageSupportedFeatures");
        Assert.Equal(BypassIoInfDeclaration.NotDeclared, BypassIoInfReader.Evaluate(elsewhere, "stornvme"));
    }

    [Fact]
    public void BuildVolumeDeviceEvidence_ReadsTheInfOfTheNodeTheServiceCameFrom()
    {
        var read = new List<string>();
        string? Reader(string name)
        {
            read.Add(name);
            return DeclaringInf;
        }

        var evidence = BypassIoInspectorService.BuildVolumeDeviceEvidence(
            "C:", 0, "disk", "stornvme", diskInf: "disk.inf", controllerInf: @"C:\Windows\INF\stornvme.inf", readInfText: Reader);

        Assert.Equal("stornvme.inf", evidence.InfName);
        Assert.Equal(BypassIoInfDeclaration.Declared, evidence.InfDeclaration);
        Assert.Equal(new[] { "stornvme.inf" }, read);
    }

    [Fact]
    public void BuildVolumeDeviceEvidence_UnreadableInfStaysUnknown()
    {
        var evidence = BypassIoInspectorService.BuildVolumeDeviceEvidence(
            "C:", 0, "disk", "stornvme", controllerInf: "oem9.inf", readInfText: _ => null);

        Assert.Equal(BypassIoInfDeclaration.Unknown, evidence.InfDeclaration);
        Assert.Equal(string.Empty, BypassIoInspectorService.DescribeInfDeclarations(
            [new BypassIoVolumeInfo { Letter = "C:", InfName = evidence.InfName, InfDeclaration = evidence.InfDeclaration }]));
    }

    [Fact]
    public void GamingImpactSummary_StatesWhatTheBoundDriversInfDeclares()
    {
        var declared = new BypassIoVolumeInfo
        {
            Letter = "C:", Enabled = true, InfName = "stornvme.inf", InfDeclaration = BypassIoInfDeclaration.Declared
        };
        var silent = new BypassIoVolumeInfo
        {
            Letter = "D:", Enabled = false, InfName = "storahci.inf", InfDeclaration = BypassIoInfDeclaration.NotDeclared
        };

        var summary = BypassIoInspectorService.BuildGamingImpactSummary([declared, silent]);

        Assert.Contains("stornvme.inf) declares BypassIO support", summary);
        Assert.Contains("storahci.inf) doesn't declare BypassIO support", summary);

        var single = DriveService.BuildBypassIoGamingImpact(new Models.BypassIOResult
        {
            Supported = false,
            StorageType = "NVMe",
            DriverCompat = "nvmedisk.sys",
            BlockedBy = "nvmedisk.sys",
            DriverInfName = "nvmedisk.inf",
            DriverInfDeclaration = "NotDeclared"
        });
        Assert.Contains("nvmedisk.inf) doesn't declare BypassIO support", single);
    }
}
