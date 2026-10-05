using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The workspace TabControl's template named its content presenter nothing, and
/// TabItemAutomationPeer only reaches a selected tab's content through PART_SelectedContentHost.
/// UI Automation, and every screen reader on top of it, saw the navigation list and the activity
/// rail but nothing inside Overview, Drives, Recovery, Tuning, Diagnostics or Settings.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class WorkspaceAutomationTreeTests
{
    [Theory]
    [InlineData(0, "Run first benchmark")]
    [InlineData(1, "Capture telemetry snapshot")]
    [InlineData(2, "Open recovery kit folder")]
    [InlineData(3, "Apply StorNVMe tuning")]
    [InlineData(4, "Preview dry-run plan")]
    [InlineData(5, "Theme mode")]
    public void SelectedWorkspaceTab_ExposesItsControlsToUiAutomation(int tabIndex, string automationName)
    {
        WpfTestHost.Run(() =>
        {
            ThemeService.ApplyMode(AppThemeMode.Dark);
            var window = new MainWindow();
            try
            {
                var tabs = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs"));
                tabs.SelectedIndex = tabIndex;
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1360, 980));
                root.Arrange(new Rect(0, 0, 1360, 980));
                root.UpdateLayout();

                var peer = UIElementAutomationPeer.CreatePeerForElement(tabs);
                var names = Names(peer).ToList();

                Assert.True(names.Contains(automationName),
                    $"'{automationName}' is not in the automation tree under tab {tabIndex}. Found: {string.Join(", ", names.Distinct())}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static IEnumerable<string> Names(AutomationPeer peer)
    {
        foreach (var child in peer.GetChildren() ?? [])
        {
            yield return child.GetName();
            foreach (var name in Names(child))
                yield return name;
        }
    }
}
