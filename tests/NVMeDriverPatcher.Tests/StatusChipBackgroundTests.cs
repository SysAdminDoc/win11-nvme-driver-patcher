using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.ViewModels;
using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The "Rollback readiness" chip hardcoded a yellow warning background while its text bound to
/// the live status color, so a Ready recovery state rendered green text on a yellow chip. Every
/// other recovery chip swaps its background with a DataTrigger.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class StatusChipBackgroundTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly Regex StatusBackground = new(@"^\{DynamicResource (?:Green|Yellow|Red|Blue|Orange|Accent)Bg\}$");

    [Fact]
    public void StatusChips_NeverPinAStatusBackgroundUnderABoundStatusColor()
    {
        // Self-check against the original chip, or a pass means nothing.
        var original = XElement.Parse($$$"""
            <Grid xmlns="{{{Wpf.NamespaceName}}}">
              <Border Grid.Column="1" Padding="8,4" Background="{DynamicResource YellowBg}" CornerRadius="9">
                <TextBlock Text="{Binding RecoveryTabBadgeText}" Foreground="{Binding RecoveryTabBadgeColor, Converter={StaticResource StrToBrush}}"/>
              </Border>
            </Grid>
            """);
        Assert.Single(PinnedChips(original));

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Views"), "*.xaml"))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            offenders.AddRange(PinnedChips(doc.Root!).Select(b =>
                $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)b).LineNumber} pins {b.Attribute("Background")!.Value}"));
        }

        Assert.True(offenders.Count == 0,
            "A chip whose text color follows a status binding must take its background from the same " +
            "state (DataTrigger or binding), not a hardcoded status brush:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void RollbackReadinessChip_TurnsGreenWhenReady_InEveryTheme()
    {
        WpfTestHost.Run(() =>
        {
            ThemeService.ApplyMode(AppThemeMode.Dark);
            var window = new MainWindow();
            try
            {
                var vm = Assert.IsType<MainViewModel>(window.DataContext);
                var title = LogicalDescendants(window).OfType<TextBlock>().Single(t => t.Text == "Rollback readiness");
                var header = Assert.IsType<Grid>(LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent(title)));
                var chip = Assert.Single(header.Children.OfType<Border>());

                foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light, AppThemeMode.HighContrast })
                {
                    ThemeService.ApplyMode(mode);

                    vm.RecoveryTabBadgeText = "Ready";
                    vm.RecoveryTabBadgeColor = "Green";
                    Pump();
                    Assert.Equal(BrushColor("GreenBg"), Assert.IsType<SolidColorBrush>(chip.Background).Color);

                    vm.RecoveryTabBadgeText = "2 missing";
                    vm.RecoveryTabBadgeColor = "Yellow";
                    Pump();
                    Assert.Equal(BrushColor("YellowBg"), Assert.IsType<SolidColorBrush>(chip.Background).Color);
                }
            }
            finally
            {
                window.Close();
                ThemeService.ApplyMode(AppThemeMode.Dark);
            }
        });
    }

    // Borders with a hardcoded status background whose direct content binds its color.
    private static IEnumerable<XElement> PinnedChips(XElement root) =>
        root.DescendantsAndSelf(Wpf + "Border").Where(border =>
            StatusBackground.IsMatch(border.Attribute("Background")?.Value ?? string.Empty) &&
            border.Elements()
                .Where(child => !child.Name.LocalName.Contains('.'))
                .Any(child => IsBinding(child.Attribute("Foreground")) || IsBinding(child.Attribute("Fill"))));

    private static bool IsBinding(XAttribute? attribute) =>
        attribute?.Value.StartsWith("{Binding", StringComparison.Ordinal) == true;

    private static Color BrushColor(string key) =>
        Assert.IsType<SolidColorBrush>(Application.Current.FindResource(key)).Color;

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child))
                yield return descendant;
        }
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
