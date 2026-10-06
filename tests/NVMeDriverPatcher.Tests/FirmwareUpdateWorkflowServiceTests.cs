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
    public void Marker_RecordsTheOptionalKeys_AndReEnablePutsThemBack()
    {
        // Disable ran with the Server key and 156965516 on. If the settings changed during the
        // firmware update, re-enable still writes the set that was disabled.
        _config.IncludeServerKey = true;
        _config.IncludeStandaloneFuture = true;
        FirmwareUpdateWorkflowService.WriteMarker(_config, PatchProfile.Full, "2026-10-05T00:00:00Z");
        _config.IncludeServerKey = false;
        _config.IncludeStandaloneFuture = false;

        var marker = FirmwareUpdateWorkflowService.ReadMarker(_config);
        Assert.True(marker!.IncludeServerKey);
        Assert.True(marker.IncludeStandaloneFuture);

        FirmwareUpdateWorkflowService.RestoreMarkedOptions(marker, _config);
        Assert.True(_config.IncludeServerKey);
        Assert.True(_config.IncludeStandaloneFuture);
    }

    [Theory]
    [InlineData(new[] { "735209102" }, false, PatchProfile.Safe, false, false)]
    [InlineData(new[] { "735209102", "1853569164" }, false, PatchProfile.Full, false, false)]
    [InlineData(new[] { "735209102", "1853569164", "156965516" }, true, PatchProfile.Full, true, true)]
    // 156965516 without 1853569164 isn't something Full writes; re-enable shouldn't add Full for it.
    [InlineData(new[] { "735209102", "156965516" }, false, PatchProfile.Safe, false, false)]
    public void InstalledSelection_ComesFromWhatIsSetNotFromTheSettings(
        string[] setValues, bool serverSet, PatchProfile profile, bool server, bool standaloneFuture)
    {
        // Settings say the opposite of what's installed: the GUI saved a click without an apply.
        var config = new AppConfig { PatchProfile = PatchProfile.Safe, IncludeServerKey = !server, IncludeStandaloneFuture = !standaloneFuture };

        var installed = FirmwareUpdateWorkflowService.InstalledSelection(setValues, serverSet, config);

        Assert.Equal((profile, server, standaloneFuture), installed);
    }

    [Fact]
    public void InstalledSelection_NothingInstalled_FallsBackToTheSettings()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Full, IncludeServerKey = true, IncludeStandaloneFuture = true };

        Assert.Equal((PatchProfile.Full, true, true), FirmwareUpdateWorkflowService.InstalledSelection([], false, config));
    }

    [Fact]
    public void Marker_RecordsTheInstalledSelectionOverTheSettings()
    {
        _config.IncludeServerKey = true;
        _config.IncludeStandaloneFuture = true;
        FirmwareUpdateWorkflowService.WriteMarker(_config, PatchProfile.Full, "2026-10-05T00:00:00Z", includeServerKey: false, includeStandaloneFuture: false);

        var marker = FirmwareUpdateWorkflowService.ReadMarker(_config);
        Assert.False(marker!.IncludeServerKey);
        Assert.False(marker.IncludeStandaloneFuture);
    }

    [Fact]
    public void RestoreMarkedOptions_OlderMarkerWithoutThem_LeavesTheConfigAlone()
    {
        _config.IncludeServerKey = true;
        _config.IncludeStandaloneFuture = false;

        FirmwareUpdateWorkflowService.RestoreMarkedOptions(new FirmwareUpdatePendingState { Profile = "Full" }, _config);
        FirmwareUpdateWorkflowService.RestoreMarkedOptions(null, _config);

        Assert.True(_config.IncludeServerKey);
        Assert.False(_config.IncludeStandaloneFuture);
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
