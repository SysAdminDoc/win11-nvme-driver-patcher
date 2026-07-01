using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.SKCharts;
using SkiaSharp;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Native-graphics regression smoke for the SkiaSharp/LiveCharts stack. Renders the same chart
/// shapes the GUI builds (benchmark ColumnSeries, telemetry LineSeries) headlessly and encodes
/// to PNG — exercising the native libSkiaSharp + bundled libpng path without a WPF window. Run
/// before AND after any Skia/OpenTK/HarfBuzz native-package bump (per the dependency checklist):
/// a native ABI break or a broken libpng surfaces here as an exception or empty output instead
/// of a runtime crash in front of a user.
/// </summary>
public sealed class ChartingSmokeTests
{
    [Fact]
    public void BenchmarkColumnChart_RendersAndEncodesPng()
    {
        // Mirrors BenchmarkComparisonView: two ColumnSeries<double> (before/after IOPS).
        var series = new ISeries[]
        {
            new ColumnSeries<double> { Name = "Before", Values = new double[] { 42000, 51000 } },
            new ColumnSeries<double> { Name = "After",  Values = new double[] { 68000, 84000 } },
        };

        AssertRendersToPng(series);
    }

    [Fact]
    public void TelemetryLineChart_RendersAndEncodesPng()
    {
        // Mirrors TelemetryView: LineSeries<ObservablePoint> (temperature/wear trend).
        var series = new ISeries[]
        {
            new LineSeries<ObservablePoint>
            {
                Name = "Temp",
                Values = new[]
                {
                    new ObservablePoint(0, 38),
                    new ObservablePoint(1, 41),
                    new ObservablePoint(2, 40),
                },
            },
        };

        AssertRendersToPng(series);
    }

    private static void AssertRendersToPng(ISeries[] series)
    {
        Assert.NotEmpty(series);

        var chart = new SKCartesianChart
        {
            Width = 640,
            Height = 360,
            Series = series,
        };

        // GetImage() runs the full Skia draw pass; Encode(Png) drives the bundled libpng.
        using var image = chart.GetImage();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        Assert.NotNull(data);
        Assert.True(data.Size > 0, "Chart PNG encode produced no bytes — native Skia/libpng path failed.");

        // A real PNG starts with the 8-byte signature 89 50 4E 47 0D 0A 1A 0A.
        var header = data.ToArray().AsSpan(0, 8).ToArray();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
    }

    [Fact]
    public void OpenTK_IsTransitiveOnly_NotDirectlyReferenced()
    {
        var csprojPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(ChartingSmokeTests).Assembly.Location)!,
            "..", "..", "..", "..", "..",
            "src", "NVMeDriverPatcher", "NVMeDriverPatcher.csproj"));
        var csproj = File.ReadAllText(csprojPath);
        Assert.DoesNotContain("\"OpenTK\"", csproj);
        Assert.DoesNotContain("\"OpenTK.Core\"", csproj);
        Assert.DoesNotContain("\"OpenTK.redist.glfw\"", csproj);
    }
}
