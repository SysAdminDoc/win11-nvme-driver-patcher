using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// The full BuildAsync path shells out to ADK copype/MakeWinPEMedia + Dism (integration-only).
// What we pin here is the injection-target correctness: startnet.cmd must land INSIDE the boot
// image at \Windows\System32, not on the media's \sources folder where WinPE never reads it.
public sealed class WinPERecoveryBuilderServiceTests
{
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
}
