using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// The full BuildAsync path shells out to ADK copype/MakeWinPEMedia + Dism (integration-only).
// What we pin here is the injection-target correctness: startnet.cmd must land INSIDE the boot
// image at \Windows\System32, not on the media's \sources folder where WinPE never reads it.
public sealed class WinPERecoveryBuilderServiceTests
{
    [Fact]
    public async Task BuildAsync_RequiresGeneratedRecoveryKitBeforeAdkWorkStarts()
    {
        var result = await WinPERecoveryBuilderService.BuildAsync(new WinPEBuildOptions
        {
            OutputDir = Path.GetTempPath(),
            RecoveryKitDir = Path.Combine(Path.GetTempPath(), $"missing-kit-{Guid.NewGuid():N}")
        });

        Assert.False(result.Success);
        Assert.Contains("self-verifying Recovery Kit is required", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void StartnetTargetPath_IsInsideImageWindowsSystem32_NotMediaSources()
    {
        var mount = Path.Combine(Path.GetTempPath(), "fakeMount");
        var target = WinPERecoveryBuilderService.StartnetTargetPath(mount);

        Assert.EndsWith(Path.Combine("Windows", "System32", "startnet.cmd"), target);
        // The old dead path — must NOT be where we write.
        Assert.DoesNotContain(Path.Combine("media", "sources"), target);
    }

    [Fact]
    public void BuildStartnetContent_AnnouncesRecoveryAndRunsWpeinit()
    {
        var content = WinPERecoveryBuilderService.BuildStartnetContent();
        Assert.Contains("wpeinit", content);
        Assert.Contains("Remove_NVMe_Patch.bat", content);
        Assert.Contains("NVMe_Recovery_Kit", content);
    }

    [Fact]
    public void WriteStartnetToMount_WritesIntoMountedImageTree()
    {
        var mount = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.WinPE.Tests.{Guid.NewGuid():N}");
        try
        {
            WinPERecoveryBuilderService.WriteStartnetToMount(mount);

            var expected = Path.Combine(mount, "Windows", "System32", "startnet.cmd");
            Assert.True(File.Exists(expected), $"startnet.cmd should be injected at {expected}");
            // It must NOT be written to the media\sources location.
            Assert.False(File.Exists(Path.Combine(mount, "media", "sources", "startnet.cmd")));

            var written = File.ReadAllText(expected);
            Assert.Contains("wpeinit", written);
        }
        finally
        {
            try { Directory.Delete(mount, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PublishMediaManifest_RecordsBootImageAndEmbeddedRecoveryKitThenVerifies()
    {
        var media = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.WinPE.Media.Tests.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(media, "sources"));
            Directory.CreateDirectory(Path.Combine(media, "NVMe_Recovery_Kit"));
            File.WriteAllText(Path.Combine(media, "sources", "boot.wim"), "fake-wim");
            File.WriteAllText(Path.Combine(media, "NVMe_Recovery_Kit", "Remove_NVMe_Patch.bat"), "exit /b 0");

            var result = WinPERecoveryBuilderService.PublishMediaManifest(media);

            Assert.True(result.Success, result.Summary);
            Assert.Equal("winpe-recovery-media", result.PayloadType);
            var json = File.ReadAllText(Path.Combine(media, GeneratedArtifactManifestService.ManifestFileName));
            Assert.Contains("\"role\": \"boot-image\"", json, StringComparison.Ordinal);
            Assert.Contains("\"role\": \"embedded-recovery-kit\"", json, StringComparison.Ordinal);

            File.AppendAllText(Path.Combine(media, "sources", "boot.wim"), "tampered");
            var tampered = GeneratedArtifactManifestService.VerifyDirectory(media);
            Assert.False(tampered.Success);
            Assert.Contains(tampered.Issues, i => i.RelativePath == "sources/boot.wim");
        }
        finally
        {
            try { Directory.Delete(media, recursive: true); } catch { }
        }
    }
}
