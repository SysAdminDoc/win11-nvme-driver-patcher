using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.ViewModels;
using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// A command bound only inside a container whose Visibility is hardcoded Collapsed is a feature
/// that cannot be reached from the GUI at all, and nothing else notices: the binding resolves, the
/// command exists, the build is clean, and every other test passes. The v5.x redesign left the old
/// overview grid collapsed rather than deleting it, which is how Cancel Benchmark, the ViVeTool
/// fallback badge, the Safe Boot upgrade button, and the mutation-blocked notice all became
/// unreachable while the app still told users to click them.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ConditionalAffordanceReachabilityTests
{
    [Fact]
    public void ConditionalAffordances_AreNotBuriedInAPermanentlyCollapsedContainer()
    {
        WpfTestHost.Run(() =>
        {
            ThemeService.ApplyMode(AppThemeMode.Dark);
            var window = new MainWindow();
            try
            {
                // Put the view model into the state each affordance exists for. This is the whole
                // point: the states were reachable in the model and unreachable in the window.
                var vm = Assert.IsType<MainViewModel>(window.DataContext);
                vm.ButtonsEnabled = true;
                vm.BenchmarkRunning = true;
                vm.ShowViVeToolFallbackBadge = true;
                vm.ShowSafeBootUpgradeBadge = true;
                vm.BuildPolicyBlocked = true;

                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1360, 980));
                root.Arrange(new Rect(0, 0, 1360, 980));
                root.UpdateLayout();

                // With those states on, every affordance must exist AND have no ancestor that
                // hardcodes Collapsed — the condition that made them permanently unreachable.
                foreach (var automationName in new[]
                         {
                             "Cancel running benchmark",
                             "Try the native FeatureStore fallback",
                             "Upgrade Safe Boot entries",
                             "Mutation actions disabled"
                         })
                {
                    var element = Descendants(window)
                        .FirstOrDefault(d => AutomationProperties.GetName(d) == automationName);
                    if (element is null)
                    {
                        var all = string.Join(", ", Descendants(window)
                            .Select(AutomationProperties.GetName)
                            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(60));
                        Assert.Fail($"'{automationName}' missing. Present names: {all}");
                    }

                    var collapsedAncestor = CollapsedAncestor(element!);
                    Assert.True(
                        collapsedAncestor is null,
                        $"'{automationName}' sits inside a hardcoded-Collapsed {collapsedAncestor?.GetType().Name} " +
                        "and can never be shown. Bind Visibility to view-model state instead of collapsing a container.");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The nearest ancestor whose Visibility is Collapsed with no binding driving it. A bound
    /// Visibility is fine — that is how a conditional affordance is supposed to work.
    /// </summary>
    private static DependencyObject? CollapsedAncestor(DependencyObject element)
    {
        for (var current = Parent(element); current is not null; current = Parent(current))
        {
            // A Window that was never shown reports Collapsed. That is the test host, not the
            // window's markup, and it is where this walk ends anyway.
            if (current is Window) return null;
            if (current is not UIElement ui || ui.Visibility != Visibility.Collapsed) continue;
            if (System.Windows.Data.BindingOperations.GetBindingBase(ui, UIElement.VisibilityProperty) is not null)
                continue;
            return current;
        }
        return null;
    }

    // Visual parent when the tree is realised, logical parent otherwise.
    private static DependencyObject? Parent(DependencyObject d) =>
        (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null)
        ?? LogicalTreeHelper.GetParent(d);

    /// <summary>
    /// Logical tree first, then visual. A window that was never shown has no realised visual tree
    /// below it, so a visual-only walk finds nothing at all and every assertion here would "fail"
    /// for the wrong reason. Same traversal the accessibility smoke test uses.
    /// </summary>
    private static IReadOnlyList<DependencyObject> Descendants(DependencyObject root)
    {
        var results = new List<DependencyObject>();
        var seen = new HashSet<DependencyObject>();
        Visit(root);
        return results;

        void Visit(DependencyObject current)
        {
            if (!seen.Add(current)) return;
            results.Add(current);

            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                Visit(child);

            if (current is not Visual and not System.Windows.Media.Media3D.Visual3D) return;

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++)
                Visit(VisualTreeHelper.GetChild(current, index));
        }
    }
}
