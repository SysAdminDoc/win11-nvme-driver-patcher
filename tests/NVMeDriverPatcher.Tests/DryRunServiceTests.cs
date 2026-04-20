using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// DryRunService.PlanInstall is a pure(-ish) projection: the only impure read is a registry
// probe for current values, which gracefully returns null when denied (under xUnit we don't
// run elevated). These tests pin the profile→item mapping that drives the user-facing
// "what will change" summary — a regression here would silently under-/over-count writes.
public sealed class DryRunServiceTests
{
    [Fact]
    public void SafeProfile_Plans_OnePrimaryWrite_TwoSafeBootCreates()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe, IncludeServerKey = false };
        var report = DryRunService.PlanInstall(config, preflight: null);

        Assert.Equal(PatchProfile.Safe, report.Profile);
        Assert.False(report.IncludeServerKey);
        Assert.Equal(1, report.TotalWrites);
        Assert.Equal(2, report.TotalCreates);
        Assert.Contains(report.Items, i => i.Action == "WRITE" && i.ValueName == AppConfig.PrimaryFeatureID);
        Assert.Equal(2, report.Items.Count(i => i.Action == "CREATE"));
    }

    [Fact]
    public void FullProfile_Plans_ThreeWrites_TwoSafeBootCreates()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Full, IncludeServerKey = false };
        var report = DryRunService.PlanInstall(config, preflight: null);

        Assert.Equal(3, report.TotalWrites);
        Assert.Equal(2, report.TotalCreates);
        foreach (var id in AppConfig.FeatureIDs)
            Assert.Contains(report.Items, i => i.Action == "WRITE" && i.ValueName == id);
    }

    [Fact]
    public void ServerKey_AddsOneMoreWrite()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe, IncludeServerKey = true };
        var report = DryRunService.PlanInstall(config, preflight: null);
        Assert.Equal(2, report.TotalWrites);
        Assert.Contains(report.Items, i => i.ValueName == AppConfig.ServerFeatureID);
    }

    [Fact]
    public void VeraCryptBlocker_PropagatesAsPreflightBlocker()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe };
        var preflight = new PreflightResult { VeraCryptDetected = true };
        var report = DryRunService.PlanInstall(config, preflight);
        Assert.Single(report.PreflightBlockers);
        Assert.Contains("VeraCrypt", report.PreflightBlockers[0]);
    }

    [Fact]
    public void Markdown_IncludesAllRegistryItems()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe };
        var report = DryRunService.PlanInstall(config, preflight: null);
        var md = DryRunService.RenderMarkdown(report);
        Assert.Contains("| Action | Target | Value |", md);
        foreach (var item in report.Items)
            Assert.Contains(item.ValueName, md);
    }
}
