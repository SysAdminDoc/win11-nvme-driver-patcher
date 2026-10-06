using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The Registry flags list marks the two opt-in values "(optional)" unless the current selection
/// writes them. The label used to follow the last saved settings and only changed on the next
/// registry refresh, so ticking the 156965516 box left the row saying it was optional.
/// </summary>
public sealed class RegistryFlagRowTests
{
    [Theory]
    [InlineData(PatchProfile.Full, true, false, false)]   // Full with the opt-in writes it
    [InlineData(PatchProfile.Full, false, false, true)]
    [InlineData(PatchProfile.Safe, true, false, true)]    // Safe never writes it, whatever the box says
    [InlineData(PatchProfile.Safe, false, true, false)]   // already set: it's present, not optional
    public void StandaloneFutureRow_IsOptionalOnlyWhenNothingWritesOrHoldsIt(PatchProfile profile, bool optIn, bool isSet, bool optional)
    {
        var row = MainViewModel.BuildFlagRow(AppConfig.StandaloneFutureFeatureID, isSet, profile, optIn, includeServerKey: false);

        Assert.Equal(optional, row.IsOptional);
        Assert.Equal(optional, row.Name.EndsWith("(optional)", StringComparison.Ordinal));
        Assert.Equal(isSet ? "Present" : optional ? "Optional" : "Missing", row.StatusLabel);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void ServerRow_FollowsTheServerKeyBox(bool includeServerKey, bool isSet, bool optional)
    {
        var row = MainViewModel.BuildFlagRow(AppConfig.ServerFeatureID, isSet, PatchProfile.Safe, includeStandaloneFuture: false, includeServerKey);

        Assert.Equal(optional, row.IsOptional);
    }

    [Fact]
    public void PrimaryRow_IsNeverOptional()
    {
        var row = MainViewModel.BuildFlagRow(AppConfig.PrimaryFeatureID, isSet: false, PatchProfile.Safe, false, false);
        Assert.False(row.IsOptional);
        Assert.Equal("Missing", row.StatusLabel);
    }

    [Theory]
    [InlineData("MainViewModel.cs", "partial void OnIsSafeModeSelectedChanged")]
    [InlineData("MainViewModel.cs", "partial void OnIsFullModeSelectedChanged")]
    [InlineData("MainViewModel.Settings.cs", "partial void OnIncludeServerKeyChanged")]
    [InlineData("MainViewModel.Settings.cs", "partial void OnIncludeStandaloneFutureChanged")]
    public void ProfileAndOptInChanges_RelabelTheRows(string file, string handler)
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(RegistryFlagRowTests).Assembly.Location)!,
            "..", "..", "..", "..", "..", "src", "NVMeDriverPatcher", "ViewModels", file)));
        int start = source.IndexOf(handler, StringComparison.Ordinal);
        Assert.True(start >= 0, handler + " not found");
        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);

        Assert.Contains("RefreshOptionalFlagRows();", source[start..end], StringComparison.Ordinal);
    }
}
