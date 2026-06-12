using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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

            ThemeService.ApplyMode(AppThemeMode.HighContrast);
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

                window.ApplyTemplate();
                window.UpdateLayout();

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
                AssertNamedControl(window, "Readiness refresh overlay");

                AssertFocusOrder(window, "Apply patch", "Remove patch");
                AssertFocusOrder(window, "Open recovery kit folder", "Create recovery kit copy");
                AssertFocusOrder(window, "Review verification script", "Generate verification script");
                AssertFocusOrder(window, "Review diagnostics report", "Export diagnostics report", "Export support bundle zip");
                AssertDialogChromeIsNamed();
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
