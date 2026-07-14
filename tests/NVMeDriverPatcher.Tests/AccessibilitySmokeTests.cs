using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.ViewModels;
using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher.Tests;

public sealed class AccessibilitySmokeTests
{
    [Fact]
    public void CriticalSafetyUi_LoadsThemesAndKeepsNamedFocusableActions()
    {
        RunSta(() =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();

            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light, AppThemeMode.HighContrast })
            {
                ThemeService.ApplyMode(mode);
                AssertThemeResources(app.Resources, mode);
            }

            ThemeService.ApplyMode(AppThemeMode.Dark);
            var window = new MainWindow();
            try
            {
                var vm = Assert.IsType<MainViewModel>(window.DataContext);
                vm.ButtonsEnabled = true;
                vm.ApplyEnabled = true;
                vm.RemoveEnabled = true;
                vm.HasRecoveryKit = true;
                vm.HasVerificationScript = true;
                vm.HasDiagnosticsReport = true;
                vm.IsLoading = false;

                var root = Assert.IsType<Grid>(window.Content);
                root.Measure(new Size(1360, 980));
                root.Arrange(new Rect(0, 0, 1360, 980));
                root.UpdateLayout();

                AssertEnabledFocusTarget(window, "Refresh readiness checks");
                AssertEnabledFocusTarget(window, "Apply patch");
                AssertEnabledFocusTarget(window, "Remove patch");
                AssertEnabledFocusTarget(window, "Create recovery kit");
                AssertEnabledFocusTarget(window, "Open recovery kit folder");
                AssertEnabledFocusTarget(window, "Create recovery kit copy");
                AssertEnabledFocusTarget(window, "Generate verification script");
                AssertEnabledFocusTarget(window, "Export support bundle zip");

                AssertNamedControl(window, "Details tabs");
                AssertNamedControl(window, "Workspace tabs");
                AssertNamedControl(window, "Primary navigation");
                AssertNamedControl(window, "Readiness refresh overlay");

                AssertFocusOrder(window, "Apply patch", "Remove patch");
                AssertFocusOrder(window, "Open recovery kit folder", "Create recovery kit copy");
                AssertFocusOrder(window, "Review verification script", "Generate verification script");
                AssertFocusOrder(window, "Review diagnostics report", "Export diagnostics report", "Export support bundle zip");
                AssertDialogChromeIsNamed();

                var navigation = Assert.IsType<ListBox>(window.FindName("PrimaryNavigation"));
                var hero = Assert.IsType<Border>(window.FindName("CommandDeck"));
                var workspace = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs"));
                Assert.Equal(0, workspace.SelectedIndex);
                Assert.True(navigation.ActualWidth >= 170, $"Navigation width was {navigation.ActualWidth}.");
                Assert.True(hero.ActualWidth >= 700, $"Hero width was {hero.ActualWidth}.");
                Assert.True(hero.ActualHeight >= 250, $"Hero height was {hero.ActualHeight}.");
                Assert.True(workspace.ActualWidth >= 500, $"Workspace width was {workspace.ActualWidth}.");

                vm.IsLoading = false;
                root.UpdateLayout();
                var snapshotPath = Environment.GetEnvironmentVariable("NVME_UI_SNAPSHOT_PATH");
                if (!string.IsNullOrWhiteSpace(snapshotPath))
                    SavePng(root, snapshotPath);

                var updateAdaptiveLayout = typeof(MainWindow).GetMethod(
                    "UpdateAdaptiveLayout",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateAdaptiveLayout);
                updateAdaptiveLayout.Invoke(window, null);
                var activityRail = Assert.IsType<Border>(window.FindName("ActivityRail"));
                Assert.Equal(1, Grid.GetRow(activityRail));
                Assert.Equal(0, Grid.GetColumn(activityRail));
                Assert.Equal(1, Grid.GetRowSpan(activityRail));
                Assert.Equal(3, Grid.GetColumnSpan(activityRail));
            }
            finally
            {
                try { window.Close(); } catch { }
                app.Shutdown();
            }
        });
    }

    private static void AssertThemeResources(ResourceDictionary resources, AppThemeMode mode)
    {
        foreach (var key in new[]
                 {
                     "WindowCanvasBrush", "TextPrimary", "TextSecondary", "Focus",
                     "Accent", "Green", "Yellow", "Red", "ActionButton",
                     "SecondaryButton", "DangerButton", "WindowChromeButton"
                 })
        {
            Assert.True(resources.Contains(key), $"{mode} theme is missing resource '{key}'.");
        }
    }

    private static void AssertEnabledFocusTarget(DependencyObject root, string automationName)
    {
        var matches = FindControls<Button>(root)
            .Where(button => AutomationProperties.GetName(button) == automationName)
            .ToArray();

        Assert.NotEmpty(matches);
        Assert.Contains(matches, button => button.Focusable && button.IsTabStop && button.IsEnabled);
    }

    private static void AssertNamedControl(DependencyObject root, string automationName)
    {
        Assert.Contains(Descendants(root), element => AutomationProperties.GetName(element) == automationName);
    }

    private static void AssertFocusOrder(DependencyObject root, params string[] expectedOrder)
    {
        var orderedNames = FindControls<Control>(root)
            .Where(control => control.Focusable && control.IsTabStop)
            .Select(AutomationProperties.GetName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        var lastIndex = -1;
        foreach (var expected in expectedOrder)
        {
            var index = orderedNames.FindIndex(lastIndex + 1, name => name == expected);
            Assert.True(index > lastIndex, $"Expected focus target '{expected}' after index {lastIndex}. Order: {string.Join(" | ", orderedNames)}");
            lastIndex = index;
        }
    }

    private static void AssertDialogChromeIsNamed()
    {
        var ctor = typeof(ThemedDialog).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);
        Assert.NotNull(ctor);

        var dialog = Assert.IsType<ThemedDialog>(ctor.Invoke(null));
        try
        {
            dialog.ApplyTemplate();
            dialog.UpdateLayout();

            AssertNamedControl(dialog, "Close dialog");
            AssertNamedControl(dialog, "Cancel dialog");
            AssertNamedControl(dialog, "Confirm dialog");
            AssertNamedControl(dialog, "Acknowledge dialog");
        }
        finally
        {
            try { dialog.Close(); } catch { }
        }
    }

    private static IReadOnlyList<T> FindControls<T>(DependencyObject root)
        where T : DependencyObject
    {
        return Descendants(root).OfType<T>().ToArray();
    }

    private static void SavePng(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(root.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(root.ActualHeight)),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IReadOnlyList<DependencyObject> Descendants(DependencyObject root)
    {
        var results = new List<DependencyObject>();
        var seen = new HashSet<DependencyObject>();
        Visit(root);
        return results;

        void Visit(DependencyObject current)
        {
            if (!seen.Add(current))
                return;

            results.Add(current);

            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                Visit(child);

            if (current is not Visual and not Visual3D)
                return;

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++)
                Visit(VisualTreeHelper.GetChild(current, index));
        }
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                completed = true;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Accessibility smoke timed out.");
        Assert.True(completed, "Accessibility smoke did not complete.");
        error?.Throw();
    }
}
