using System.IO;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class FirmwareUpdateWorkflowServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppConfig _config;

    public FirmwareUpdateWorkflowServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_FwWorkflow_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new AppConfig { WorkingDir = _dir };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Marker_RoundTrips()
    {
        FirmwareUpdateWorkflowService.WriteMarker(_config, PatchProfile.Full, "2026-06-14T00:00:00Z");
        var marker = FirmwareUpdateWorkflowService.ReadMarker(_config);
        Assert.NotNull(marker);
        Assert.Equal("Full", marker!.Profile);
        Assert.Equal("2026-06-14T00:00:00Z", marker.DisabledAt);
    }

    [Fact]
    public void ClearMarker_RemovesFile()
    {
        FirmwareUpdateWorkflowService.WriteMarker(_config, PatchProfile.Safe, "x");
        Assert.True(File.Exists(FirmwareUpdateWorkflowService.MarkerPath(_config)));
        FirmwareUpdateWorkflowService.ClearMarker(_config);
        Assert.False(File.Exists(FirmwareUpdateWorkflowService.MarkerPath(_config)));
        Assert.Null(FirmwareUpdateWorkflowService.ReadMarker(_config));
    }

    [Fact]
    public void ResolveReEnableProfile_MarkerWins()
    {
        var marker = new FirmwareUpdatePendingState { Profile = "Full" };
        var cfg = new AppConfig { PatchProfile = PatchProfile.Safe };
        var (profile, hadMarker) = FirmwareUpdateWorkflowService.ResolveReEnableProfile(marker, cfg);
        Assert.Equal(PatchProfile.Full, profile);
        Assert.True(hadMarker);
    }

    [Fact]
    public void ResolveReEnableProfile_NoMarker_FallsBackToConfig()
    {
        var cfg = new AppConfig { PatchProfile = PatchProfile.Full };
        var (profile, hadMarker) = FirmwareUpdateWorkflowService.ResolveReEnableProfile(null, cfg);
        Assert.Equal(PatchProfile.Full, profile);
        Assert.False(hadMarker);
    }

    [Fact]
    public void ResolveReEnableProfile_InvalidMarker_FallsBackToConfig()
    {
        var marker = new FirmwareUpdatePendingState { Profile = "Frobnicate" };
        var cfg = new AppConfig { PatchProfile = PatchProfile.Safe };
        var (profile, hadMarker) = FirmwareUpdateWorkflowService.ResolveReEnableProfile(marker, cfg);
        Assert.Equal(PatchProfile.Safe, profile);
        Assert.False(hadMarker);
    }

    [Fact]
    public void BuildDisableInstructions_IncludesVendorGuideLink()
    {
        var nudge = FirmwareUpdateNudgeService.Lookup("Samsung SSD 990 PRO", "4B2QJXD7");
        var text = FirmwareUpdateWorkflowService.BuildDisableInstructions(new[] { nudge });
        Assert.Contains("Samsung SSD 990 PRO", text);
        Assert.Contains("4B2QJXD7", text);
        Assert.Contains(nudge.HowToUpdateUrl, text);
        Assert.Contains("re-enable-after-update", text);
    }

    [Fact]
    public void BuildDisableInstructions_NoDrives_StillExplains()
    {
        var text = FirmwareUpdateWorkflowService.BuildDisableInstructions(Array.Empty<FirmwareUpdateNudge>());
        Assert.Contains("No NVMe drives detected", text);
    }

    [Fact]
    public void Nudge_KnownVendor_HasHowToUpdateUrl()
    {
        var nudge = FirmwareUpdateNudgeService.Lookup("WD_BLACK SN850X", "620331WD");
        Assert.False(string.IsNullOrWhiteSpace(nudge.HowToUpdateUrl));
        Assert.Equal(nudge.UpdateToolUrl, nudge.HowToUpdateUrl);
    }
}
