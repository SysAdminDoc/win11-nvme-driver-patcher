using System.Windows.Controls;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using NVMeDriverPatcher.Models;
using SkiaSharp;

namespace NVMeDriverPatcher.Views;

public partial class BenchmarkComparisonView : UserControl
{
    public BenchmarkComparisonView()
    {
        InitializeComponent();
    }

    public void UpdateChart(List<BenchmarkResult> history)
    {
        if (history.Count == 0)
        {
            ReadIopsValue.Text = "-";
            WriteIopsValue.Text = "-";
            ReadDelta.Text = "";
            WriteDelta.Text = "";
            BenchChart.Series = [];
            return;
        }

        var latest = history[^1];
        ReadIopsValue.Text = latest.Read.IOPS.ToString("N0");
        WriteIopsValue.Text = latest.Write.IOPS.ToString("N0");

        if (history.Count >= 2)
        {
            var prev = history[^2];
            ReadDelta.Text = FormatDelta(prev.Read.IOPS, latest.Read.IOPS);
            ReadDelta.Foreground = DeltaBrush(prev.Read.IOPS, latest.Read.IOPS);
            WriteDelta.Text = FormatDelta(prev.Write.IOPS, latest.Write.IOPS);
            WriteDelta.Foreground = DeltaBrush(prev.Write.IOPS, latest.Write.IOPS);
        }

        var labels = history.Select(h => h.Label).ToArray();
        var readValues = history.Select(h => h.Read.IOPS).ToArray();
        var writeValues = history.Select(h => h.Write.IOPS).ToArray();

        BenchChart.Series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = readValues,
                Name = "Read IOPS",
                Fill = new SolidColorPaint(new SKColor(59, 130, 246)),
                MaxBarWidth = 24,
                Padding = 4
            },
            new ColumnSeries<double>
            {
                Values = writeValues,
                Name = "Write IOPS",
                Fill = new SolidColorPaint(new SKColor(245, 158, 11)),
                MaxBarWidth = 24,
                Padding = 4
            }
        };

        BenchChart.XAxes = new[]
        {
            new Axis
            {
                Labels = labels,
                LabelsPaint = new SolidColorPaint(new SKColor(113, 113, 122)),
                TextSize = 10,
                SeparatorsPaint = new SolidColorPaint(new SKColor(39, 39, 42))
            }
        };

        BenchChart.YAxes = new[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(new SKColor(113, 113, 122)),
                TextSize = 10,
                SeparatorsPaint = new SolidColorPaint(new SKColor(26, 26, 30)),
                MinLimit = 0
            }
        };
    }

    private static string FormatDelta(double prev, double current)
    {
        if (prev <= 0) return "";
        var pct = Math.Round((current - prev) / prev * 100, 1);
        return $"vs prev: {(pct >= 0 ? "+" : "")}{pct}%";
    }

    private static System.Windows.Media.SolidColorBrush DeltaBrush(double prev, double current)
    {
        var bc = new System.Windows.Media.BrushConverter();
        return current >= prev
            ? (System.Windows.Media.SolidColorBrush)bc.ConvertFromString("#FF22c55e")!
            : (System.Windows.Media.SolidColorBrush)bc.ConvertFromString("#FFef4444")!;
    }
}
