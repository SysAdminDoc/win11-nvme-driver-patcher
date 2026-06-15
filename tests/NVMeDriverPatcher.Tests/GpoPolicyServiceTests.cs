using System.IO;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class GpoPolicyServiceTests
{
    [Fact]
    public void AnyApplied_FalseForEmptyOverlay_TrueWhenAnyFieldSet()
    {
        Assert.False(new PolicyOverlay().AnyApplied);
        Assert.True(new PolicyOverlay { SkipWarnings = true }.AnyApplied);
        Assert.True(new PolicyOverlay { PatchProfile = PatchProfile.Full }.AnyApplied);
        Assert.True(new PolicyOverlay { WatchdogWindowHours = 24 }.AnyApplied);
    }

    [Fact]
    public void ApplyTo_PinnedValues_OverrideConfig()
    {
        var config = new AppConfig
        {
            PatchProfile = PatchProfile.Safe,
            IncludeServerKey = false,
            SkipWarnings = false,
            CompatTelemetryEnabled = true,
        };
        var overlay = new PolicyOverlay
        {
            PatchProfile = PatchProfile.Full,
            IncludeServerKey = true,
            SkipWarnings = true,
            CompatTelemetryEnabled = false,
        };

        GpoPolicyService.ApplyTo(config, overlay);

        Assert.Equal(PatchProfile.Full, config.PatchProfile);
        Assert.True(config.IncludeServerKey);
        Assert.True(config.SkipWarnings);
        Assert.False(config.CompatTelemetryEnabled);
    }

    [Fact]
    public void ApplyTo_EmptyOverlay_LeavesConfigUntouched()
    {
        var config = new AppConfig
        {
            PatchProfile = PatchProfile.Full,
            IncludeServerKey = true,
            SkipWarnings = true,
            CompatTelemetryEnabled = false,
        };

        GpoPolicyService.ApplyTo(config, new PolicyOverlay());

        Assert.Equal(PatchProfile.Full, config.PatchProfile);
        Assert.True(config.IncludeServerKey);
        Assert.True(config.SkipWarnings);
        Assert.False(config.CompatTelemetryEnabled);
    }

    [Fact]
    public void ApplyTo_WatchdogOverlay_PropagatesToWatchdogState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_Gpo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = new AppConfig { WorkingDir = dir };
            GpoPolicyService.ApplyTo(config, new PolicyOverlay { WatchdogAutoRevert = false, WatchdogWindowHours = 72 });

            var state = EventLogWatchdogService.LoadState(config);
            Assert.False(state.AutoRevertEnabled);
            Assert.Equal(72, state.WindowHours);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
