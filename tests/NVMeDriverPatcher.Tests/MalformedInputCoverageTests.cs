using System.Text.Json;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class MalformedInputCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"NVMePatcher_MalformedInputs_{Guid.NewGuid():N}");

    public MalformedInputCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ArtifactManifest_MalformedJsonFailsClosed()
    {
        var kit = Path.Combine(_root, "kit");
        Directory.CreateDirectory(kit);
        File.WriteAllText(Path.Combine(kit, GeneratedArtifactManifestService.ManifestFileName), "{");

        var result = GeneratedArtifactManifestService.VerifyDirectory(kit);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Kind == ArtifactIntegrityIssueKind.ManifestInvalid);
    }

    [Fact]
    public void WinPeControllerReport_MalformedJsonIsRejected()
    {
        var media = Path.Combine(_root, "media");
        var reportDirectory = Path.Combine(media, WinPEMediaFreshnessService.ControllerDirectoryName);
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(
            Path.Combine(reportDirectory, WinPEMediaFreshnessService.ReportFileName),
            "{");

        Assert.Throws<JsonException>(() => WinPEMediaFreshnessService.ReadBuildReport(media));
    }

    [Fact]
    public async Task WinPeSourceCapture_RejectsMalformedRecoveryKitManifest()
    {
        var kit = Path.Combine(_root, "recovery-kit");
        Directory.CreateDirectory(kit);
        File.WriteAllText(Path.Combine(kit, GeneratedArtifactManifestService.ManifestFileName), "{");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            WinPEMediaFreshnessService.CaptureSourcesAsync(
                kit,
                new BootStorageControllerInventory(),
                winReImagePath: null));

        Assert.Contains("Recovery Kit integrity failed", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { }
    }
}
