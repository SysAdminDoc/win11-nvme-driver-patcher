using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class WinPEMediaFreshnessServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), $"NVMeDriverPatcher.WinPEFreshness.Tests.{Guid.NewGuid():N}");

    public WinPEMediaFreshnessServiceTests() => Directory.CreateDirectory(_tempRoot);

    [Fact]
    public async Task CaptureSources_FingerprintsRecoveryManifestRollbackScriptWinReAndControllers()
    {
        var kit = RecoveryKitService.Export(_tempRoot);
        Assert.NotNull(kit);
        var winre = Path.Combine(_tempRoot, "winre.wim");
        File.WriteAllText(winre, "fake winre image");
        var inventory = Inventory(Controller("PCI\\VEN_1234\\1", "stornvme.inf", "1.0",
            WinPEControllerCoverage.Inbox));

        var snapshot = await WinPEMediaFreshnessService.CaptureSourcesAsync(kit!, inventory, winre);

        Assert.Equal(AppConfig.AppVersion, snapshot.ToolVersion);
        Assert.Matches("^[0-9a-f]{64}$", snapshot.RecoveryKitManifestSha256);
        Assert.Equal(WinPEMediaFreshnessService.ComputeCurrentRollbackScriptSha256(),
            snapshot.RollbackScriptSha256);
        Assert.True(snapshot.WinReImage.Available);
        Assert.Equal(new FileInfo(winre).Length, snapshot.WinReImage.ByteLength);
        Assert.Matches("^[0-9a-f]{64}$", snapshot.WinReImage.Sha256!);
        Assert.Single(snapshot.Controllers);
    }

    [Fact]
    public void Compare_IsFreshOnlyWhenEveryFingerprintAndControllerMatches()
    {
        var controller = Controller("PCI\\VEN_1234\\1", "stornvme.inf", "1.0",
            WinPEControllerCoverage.Inbox);
        var current = Snapshot(controller);
        var build = WinPEMediaFreshnessService.CreateBuildReport(current, [controller]);

        var result = WinPEMediaFreshnessService.Compare(build, current);

        Assert.Equal(WinPEMediaFreshness.Fresh, result.State);
        Assert.Empty(result.Reasons);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("kit")]
    [InlineData("rollback")]
    [InlineData("winre")]
    [InlineData("controller-version")]
    [InlineData("controller-added")]
    public void Compare_MarksEveryRequiredSourceChangeStale(string change)
    {
        var original = Controller("PCI\\VEN_1234\\1", "oem42.inf", "1.0",
            WinPEControllerCoverage.Injected);
        var buildSource = Snapshot(original);
        var build = WinPEMediaFreshnessService.CreateBuildReport(buildSource, [original]);
        var current = Snapshot(Controller(original.InstanceId, original.InfName, original.DriverVersion,
            WinPEControllerCoverage.PendingInjection));

        switch (change)
        {
            case "app": current.ToolVersion = "99.0.0"; break;
            case "kit": current.RecoveryKitManifestSha256 = Hash('b'); break;
            case "rollback": current.RollbackScriptSha256 = Hash('c'); break;
            case "winre": current.WinReImage.Sha256 = Hash('d'); break;
            case "controller-version": current.Controllers[0].DriverVersion = "2.0"; break;
            case "controller-added": current.Controllers.Add(
                Controller("PCI\\VEN_5678\\2", "stornvme.inf", "1.0", WinPEControllerCoverage.Inbox)); break;
        }

        var result = WinPEMediaFreshnessService.Compare(build, current);

        Assert.Equal(WinPEMediaFreshness.Stale, result.State);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void Compare_IsUnknownWhenWinReAndControllerEvidenceCannotBeObserved()
    {
        var source = Snapshot();
        source.WinReImage = new WinPEFileFingerprint { Available = false };
        source.ControllerProbeSucceeded = false;
        source.ControllerProbeError = "WMI access denied";
        var build = WinPEMediaFreshnessService.CreateBuildReport(source, []);

        var result = WinPEMediaFreshnessService.Compare(build, source);

        Assert.Equal(WinPEMediaFreshness.Unknown, result.State);
        Assert.Contains(result.Reasons, reason => reason.Contains("WinRE", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("WMI access denied", StringComparison.Ordinal));
    }

    [Fact]
    public void PublishBuildReport_IsAtomicReadableAndMediaRootResolverAcceptsTreeOrMedia()
    {
        var tree = Path.Combine(_tempRoot, "tree");
        var media = Path.Combine(tree, "media");
        Directory.CreateDirectory(media);
        File.WriteAllText(Path.Combine(media, "boot.bin"), "boot");
        var source = Snapshot();
        var build = WinPEMediaFreshnessService.CreateBuildReport(source, []);

        var reportPath = WinPEMediaFreshnessService.PublishBuildReport(media, build);
        GeneratedArtifactManifestService.PublishDirectoryManifest(media, "winpe-recovery-media");

        Assert.True(File.Exists(reportPath));
        Assert.Equal(build.ToolVersion, WinPEMediaFreshnessService.ReadBuildReport(media).ToolVersion);
        Assert.Equal(media, WinPEMediaFreshnessService.ResolveMediaRoot(tree));
        Assert.Equal(media, WinPEMediaFreshnessService.ResolveMediaRoot(media));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(reportPath)!, "*.tmp"));
    }

    [Fact]
    public void PublishBuildReport_RejectsDuplicateControllerInstanceIds()
    {
        var media = Path.Combine(_tempRoot, "duplicate-report", "media");
        Directory.CreateDirectory(media);
        var first = Controller("PCI\\VEN_1234\\1", "stornvme.inf", "1.0",
            WinPEControllerCoverage.Inbox);
        var duplicate = Controller("pci\\ven_1234\\1", "stornvme.inf", "1.0",
            WinPEControllerCoverage.Inbox);
        var build = WinPEMediaFreshnessService.CreateBuildReport(Snapshot(first), [first, duplicate]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            WinPEMediaFreshnessService.PublishBuildReport(media, build));

        Assert.Contains("duplicate instance ID", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(media, "*.tmp", SearchOption.AllDirectories));
    }

    private static WinPESourceSnapshot Snapshot(params BootStorageController[] controllers) => new()
    {
        ToolVersion = AppConfig.AppVersion,
        RecoveryKitManifestSha256 = Hash('a'),
        RollbackScriptSha256 = Hash('b'),
        WinReImage = new WinPEFileFingerprint
        {
            Available = true,
            ByteLength = 10,
            Sha256 = Hash('c')
        },
        ControllerProbeSucceeded = true,
        Controllers = controllers.ToList()
    };

    private static BootStorageControllerInventory Inventory(params BootStorageController[] controllers) => new()
    {
        ProbeSucceeded = true,
        Controllers = controllers.ToList()
    };

    private static BootStorageController Controller(
        string id, string inf, string version, WinPEControllerCoverage coverage) => new()
    {
        InstanceId = id,
        FriendlyName = id,
        DeviceClass = "SCSIAdapter",
        ServiceName = inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) ? "iaStorVD" : "stornvme",
        InfName = inf,
        DriverProvider = inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) ? "Vendor" : "Microsoft",
        DriverVersion = version,
        Coverage = coverage
    };

    private static string Hash(char value) => new(value, 64);

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }
}
