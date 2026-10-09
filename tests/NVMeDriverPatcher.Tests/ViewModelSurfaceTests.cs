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
    // The Next step card shows a title and a description and never had buttons.
    [InlineData("HasNextStepPrimaryAction")]
    [InlineData("NextStepPrimaryActionText")]
    [InlineData("NextStepPrimaryActionId")]
    [InlineData("NextStepPrimaryActionEnabled")]
    [InlineData("HasNextStepSecondaryAction")]
    [InlineData("NextStepSecondaryActionText")]
    [InlineData("NextStepSecondaryActionId")]
    [InlineData("NextStepSecondaryActionEnabled")]
    [InlineData("BenchLabelText")]
    [InlineData("BenchLabelVisible")]
    [InlineData("LogEntries")]
    public void UnboundProjections_AreGone(string member)
    {
        Assert.Null(typeof(MainViewModel).GetProperty(member));
    }

    [Fact]
    public void NextStepCard_BindsOnlyWhatTheViewModelStillHas()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Views", "MainWindow.xaml"));
        foreach (var bound in new[] { "NextStepTitle", "NextStepDescription", "NextStepColor" })
        {
            Assert.Contains($"{{Binding {bound}", xaml, StringComparison.Ordinal);
            Assert.NotNull(typeof(MainViewModel).GetProperty(bound));
        }
        foreach (var gone in new[] { "NextStepPrimaryAction", "NextStepSecondaryAction", "BenchLabel", "LogEntries" })
            Assert.DoesNotContain(gone, xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchmarkHistoryChanged_IsTheWindowsSignalToDropItsHistoryCache()
    {
        // The bench label used to double as this signal; nothing showed the label itself.
        Assert.NotNull(typeof(MainViewModel).GetEvent("BenchmarkHistoryChanged"));

        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Views", "MainWindow.xaml.cs"));
        Assert.Contains("_vm.BenchmarkHistoryChanged += ViewModel_BenchmarkHistoryChanged;", code, StringComparison.Ordinal);
        Assert.Contains("_vm.BenchmarkHistoryChanged -= ViewModel_BenchmarkHistoryChanged;", code, StringComparison.Ordinal);
        foreach (var gone in new[] { "BenchLabel", "RecommendedPrimaryAction", "RecommendedSecondaryAction", "ExecuteRecommendedAction" })
            Assert.DoesNotContain(gone, code, StringComparison.Ordinal);
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
