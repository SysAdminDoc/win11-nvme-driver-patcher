using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using NVMeDriverPatcher.Converters;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Status chips, stage markers and badges bind a theme resource key through StrToBrush, which only
/// resolves the brush when the binding updates. Switching Dark to Light from the header left them in
/// the dark theme's pale colors, unreadable on the light surfaces, until their state next changed.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed partial class ThemeBrushRefreshTests
{
    [GeneratedRegex(@"\{Binding\s+(?:Path=)?(\w+)[^{}]*Converter=\{StaticResource StrToBrush\}")]
    private static partial Regex StrToBrushBinding();

    [Fact]
    public void KeyedBrushes_ResolveAgainAfterALiveThemeSwitch()
    {
        WpfTestHost.Run(() =>
        {
            ThemeService.ApplyMode(AppThemeMode.Dark);
            var vm = new MainViewModel();
            var chip = new Border { DataContext = vm };
            chip.SetBinding(Border.BorderBrushProperty,
                new Binding(nameof(MainViewModel.RecoveryTabBadgeColor)) { Converter = new StringToBrushConverter() });

            try
            {
                Pump();
                var key = vm.RecoveryTabBadgeColor;
                var darkBrush = Application.Current.FindResource(key);
                Assert.Same(darkBrush, chip.BorderBrush);

                ThemeService.ApplyMode(AppThemeMode.Light);
                Pump();
                var lightBrush = Application.Current.FindResource(key);
                Assert.NotSame(darkBrush, lightBrush);
                // Self-check: without the refresh the binding still holds the dark brush.
                Assert.Same(darkBrush, chip.BorderBrush);

                vm.RefreshThemeBrushes();
                Pump();
                Assert.Same(lightBrush, chip.BorderBrush);
            }
            finally
            {
                ThemeService.ApplyMode(AppThemeMode.Dark);
            }
        });
    }

    [Fact]
    public void EveryStrToBrushBinding_IsRefreshedOnThemeChange()
    {
        var sample = """<Border BorderBrush="{Binding StatusColor, Converter={StaticResource StrToBrush}}"/>""";
        Assert.Equal("StatusColor", StrToBrushBinding().Match(sample).Groups[1].Value);

        var bound = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Views"), "*.xaml")
            .SelectMany(file => StrToBrushBinding().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .ToList();

        Assert.True(bound.Count >= 20, $"Expected the view to bind its status colors through StrToBrush, found {bound.Count}.");
        var missed = bound.Distinct().Except(MainViewModel.ThemeBrushKeyPropertyNames).ToList();
        Assert.True(missed.Count == 0,
            "These StrToBrush bindings won't follow a theme switch. Name the property *Color or refresh it in RefreshThemeBrushes:\n" +
            string.Join("\n", missed));
    }

    [Fact]
    public void MainWindow_RefreshesKeyedBrushesWhenTheThemeChanges()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Views", "MainWindow.xaml.cs"));
        var handler = Regex.Match(source, @"void ThemeService_ThemeChanged\([^)]*\)\s*\{(?<body>[^}]*)\}");
        Assert.True(handler.Success, "MainWindow.ThemeService_ThemeChanged not found.");
        Assert.Contains("_vm.RefreshThemeBrushes();", handler.Groups["body"].Value, StringComparison.Ordinal);
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
