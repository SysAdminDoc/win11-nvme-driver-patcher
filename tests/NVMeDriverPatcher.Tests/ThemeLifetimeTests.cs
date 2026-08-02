using System.Windows;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.Views;
using SkiaSharp;

namespace NVMeDriverPatcher.Tests;

[Collection(WpfCollection.Name)]
public sealed class ThemeLifetimeTests
{
    [Fact]
    public void ChartViews_ResubscribeThemeChangesAfterTabReload()
    {
        WpfTestHost.Run(() =>
        {
            ThemeService.ApplyMode(AppThemeMode.Dark);
            var benchmark = new BenchmarkComparisonView();
            var telemetry = new TelemetryView();

            try
            {
                benchmark.UpdateChart(new List<BenchmarkResult>
                {
                    new()
                    {
                        Label = "baseline",
                        Timestamp = DateTime.UtcNow.ToString("o"),
                        Read = new BenchmarkMetrics { IOPS = 42_000 },
                        Write = new BenchmarkMetrics { IOPS = 51_000 }
                    }
                });
                telemetry.UpdateTempHistory([(DateTime.UtcNow, 42)]);
                telemetry.UpdateWearHistory([(DateTime.UtcNow, 88)]);

                var darkBenchmarkColor = BenchmarkFill(benchmark);
                var darkTelemetryColor = TelemetryStroke(telemetry);

                RaiseLifecycleEvent(benchmark, FrameworkElement.LoadedEvent);
                RaiseLifecycleEvent(telemetry, FrameworkElement.LoadedEvent);
                RaiseLifecycleEvent(benchmark, FrameworkElement.UnloadedEvent);
                RaiseLifecycleEvent(telemetry, FrameworkElement.UnloadedEvent);

                ThemeService.ApplyMode(AppThemeMode.Light);
                Assert.Equal(darkBenchmarkColor, BenchmarkFill(benchmark));
                Assert.Equal(darkTelemetryColor, TelemetryStroke(telemetry));

                RaiseLifecycleEvent(benchmark, FrameworkElement.LoadedEvent);
                RaiseLifecycleEvent(telemetry, FrameworkElement.LoadedEvent);
                ThemeService.ApplyMode(AppThemeMode.Light);

                Assert.NotEqual(darkBenchmarkColor, BenchmarkFill(benchmark));
                Assert.NotEqual(darkTelemetryColor, TelemetryStroke(telemetry));
            }
            finally
            {
                RaiseLifecycleEvent(benchmark, FrameworkElement.UnloadedEvent);
                RaiseLifecycleEvent(telemetry, FrameworkElement.UnloadedEvent);
                ThemeService.ApplyMode(AppThemeMode.Dark);
            }
        });
    }

    private static SKColor BenchmarkFill(BenchmarkComparisonView view)
    {
        var chart = Assert.IsType<CartesianChart>(view.FindName("BenchChart"));
        var series = Assert.IsType<ColumnSeries<double>>(chart.Series.OfType<ColumnSeries<double>>().First());
        return Assert.IsType<SolidColorPaint>(series.Fill).Color;
    }

    private static SKColor TelemetryStroke(TelemetryView view)
    {
        var chart = Assert.IsType<CartesianChart>(view.FindName("TempChart"));
        var series = Assert.IsType<LineSeries<ObservablePoint>>(Assert.Single(chart.Series));
        return Assert.IsType<SolidColorPaint>(series.Stroke).Color;
    }

    private static void RaiseLifecycleEvent(FrameworkElement element, RoutedEvent routedEvent)
    {
        element.RaiseEvent(new RoutedEventArgs(routedEvent, element));
    }
}
