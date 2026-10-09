using System.Runtime.CompilerServices;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The redesign dropped the views for several MainViewModel collections but left the code that
/// rebuilt them on every refresh (registry reads, WMI projections, BypassIO inspection) running
/// for nothing. The readiness list and the drive list are real safety information, so they are
/// shown on Overview and Drives. Everything else that nothing bound was removed.
/// </summary>
public sealed class ViewModelSurfaceTests
{
    [Theory]
    [InlineData("LeftChecks")]
    [InlineData("RightChecks")]
    [InlineData("RegistryFlags")]
    [InlineData("SafeBootFlags")]
    [InlineData("AttentionNotes")]
    [InlineData("ChangePlanSteps")]
    [InlineData("DirectStorageImpactText")]
    [InlineData("DirectStorageImpactSeverity")]
    [InlineData("DirectStoragePanelVisible")]
    [InlineData("RiskSummaryColor")]
    [InlineData("ActionReadinessText")]
    [InlineData("ActionReadinessColor")]
    [InlineData("SkipWarnings")]
    public void UnboundProjections_AreGone(string member)
    {
        Assert.Null(typeof(MainViewModel).GetProperty(member));
    }

    [Theory]
    [InlineData("ReadinessChecks")]
    [InlineData("Drives")]
    public void SurfacedCollections_AreBoundInTheMainWindow(string collection)
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Views", "MainWindow.xaml"));
        Assert.Contains($"ItemsSource=\"{{Binding {collection}}}\"", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CheckStatus.Pass, "Ready")]
    [InlineData(CheckStatus.Warning, "Review")]
    [InlineData(CheckStatus.Fail, "Blocked")]
    [InlineData(CheckStatus.Info, "Info")]
    public void ReadinessChip_LabelsTheStatusTheXamlTriggersOn(CheckStatus status, string label)
    {
        var chip = new PreflightCheckVM { Label = "Build", Status = status, Message = "msg" };
        Assert.Equal(label, chip.StatusLabel);

        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Views", "MainWindow.xaml"));
        Assert.Contains($"Binding=\"{{Binding StatusLabel}}\" Value=\"{label}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessChip_TooltipAppendsTheDetailAfterTheMessage()
    {
        Assert.Equal("msg", new PreflightCheckVM { Message = "msg" }.DetailTooltip);
        Assert.Equal("msg\n\ndetail", new PreflightCheckVM { Message = "msg", Tooltip = "detail" }.DetailTooltip);
    }

    [Fact]
    public void DriveRow_ShowsNativeOrLegacy()
    {
        Assert.Equal("NATIVE", new DriveRowVM { IsNativeDrive = true }.DriverBadgeText);
        Assert.Equal("LEGACY", new DriveRowVM { IsNativeDrive = false }.DriverBadgeText);
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
