using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using SkiaSharp;

namespace NVMeDriverPatcher.Views;

public partial class BenchmarkComparisonView : UserControl
{
    private List<BenchmarkResult> _lastHistory = [];

    public BenchmarkComparisonView()
    {
        InitializeComponent();
        ThemeService.ThemeChanged += ThemeService_ThemeChanged;
        Unloaded += BenchmarkComparisonView_Unloaded;
    }

    public void UpdateChart(List<BenchmarkResult>? history)
    {
        _lastHistory = history?.ToList() ?? [];
        history = _lastHistory;

        if (history.Count == 0)
        {
            RunCountValue.Text = "0";
            BenchmarkContextText.Text = "No benchmark runs recorded yet.";
            BenchmarkSummaryText.Text = "Capture a baseline before you change drivers, then run the benchmark again afterward to validate the outcome on this exact machine.";
            TrendHintText.Text = "The chart becomes useful once at least one run is captured and far more trustworthy once a second run confirms direction.";
            ReadIopsValue.Text = "-";
            WriteIopsValue.Text = "-";
            ReadDelta.Text = "";
            WriteDelta.Text = "";
            ApplyBenchmarkState("Waiting", "TextDim", "SurfaceInset", "Border");
            BenchChart.Series = [];
            return;
        }

        var latest = history[^1];
        RunCountValue.Text = history.Count.ToString();
        BenchmarkContextText.Text = $"Latest run: {latest.Label} • {FormatTimestamp(latest.Timestamp)}";
        ReadIopsValue.Text = latest.Read.IOPS.ToString("N0");
        WriteIopsValue.Text = latest.Write.IOPS.ToString("N0");

        if (history.Count >= 2)
        {
            var prev = history[^2];
            ReadDelta.Text = FormatDelta(prev.Read.IOPS, latest.Read.IOPS);
            ReadDelta.Foreground = DeltaBrush(prev.Read.IOPS, latest.Read.IOPS);
            WriteDelta.Text = FormatDelta(prev.Write.IOPS, latest.Write.IOPS);
            WriteDelta.Foreground = DeltaBrush(prev.Write.IOPS, latest.Write.IOPS);
            BenchmarkSummaryText.Text = $"Comparing {latest.Label} against the previous run shows whether the most recent change helped sustained 4K random performance or simply shifted the tradeoff.";
            TrendHintText.Text = "Use the chart to confirm whether the latest change moved both read and write performance in the direction you expected, not just one headline metric.";
            ApplyBenchmarkState("Comparison Ready", "Green", "GreenBg", "Green");
        }
        else
        {
            ReadDelta.Text = "Baseline captured";
            ReadDelta.Foreground = ResolveBrush("TextDim");
            WriteDelta.Text = "Run another benchmark after a driver change to compare.";
            WriteDelta.Foreground = ResolveBrush("TextDim");
            BenchmarkSummaryText.Text = "This first run is your baseline. Capture another run after applying or removing the patch so the comparison view can show direction, not just raw numbers.";
            TrendHintText.Text = "The chart currently reflects one baseline run. Add a post-change run to reveal direction instead of a single point in time.";
            ApplyBenchmarkState("Baseline Only", "Accent", "AccentBg", "Accent");
        }

        var labels = history.Select((h, index) => $"{h.Label}\n{BuildAxisSubLabel(h.Timestamp, index)}").ToArray();
        var readValues = history.Select(h => h.Read.IOPS).ToArray();
        var writeValues = history.Select(h => h.Write.IOPS).ToArray();

        BenchChart.Series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = readValues,
                Name = "Read IOPS",
                Fill = new SolidColorPaint(ResolveSkColor("Accent")),
                MaxBarWidth = 24,
                Padding = 4
            },
            new ColumnSeries<double>
            {
                Values = writeValues,
                Name = "Write IOPS",
                Fill = new SolidColorPaint(ResolveSkColor("Yellow")),
                MaxBarWidth = 24,
                Padding = 4
            }
        };

        BenchChart.XAxes = new[]
        {
            new Axis
            {
                Labels = labels,
                LabelsPaint = new SolidColorPaint(ResolveSkColor("TextDim")),
                TextSize = 10,
                SeparatorsPaint = new SolidColorPaint(ResolveSkColor("Border"))
            }
        };

        BenchChart.YAxes = new[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(ResolveSkColor("TextDim")),
                TextSize = 10,
                SeparatorsPaint = new SolidColorPaint(ResolveSkColor("Border")),
                MinLimit = 0
            }
        };
    }

    private void ApplyBenchmarkState(
        string label,
        string foregroundKey,
        string backgroundKey,
        string borderKey)
    {
        var foreground = ResolveBrush(foregroundKey);
        var background = ResolveBrush(backgroundKey);
        var border = ResolveBrush(borderKey);

        BenchmarkStateText.Text = label;
        BenchmarkStateText.Foreground = foreground;
        BenchmarkStateBadge.Background = background;
        BenchmarkStateBadge.BorderBrush = border;
        InterpretationCard.Background = background;
        InterpretationCard.BorderBrush = border;
    }

    private static string FormatDelta(double prev, double current)
    {
        if (prev <= 0) return "";
        var pct = Math.Round((current - prev) / prev * 100, 1);
        return $"vs previous run: {(pct >= 0 ? "+" : "")}{pct}%";
    }

    private void ThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        UpdateChart(_lastHistory);
    }

    private void BenchmarkComparisonView_Unloaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
        Unloaded -= BenchmarkComparisonView_Unloaded;
    }

    private Brush DeltaBrush(double prev, double current)
    {
        return current >= prev
            ? ResolveBrush("Green")
            : ResolveBrush("Red");
    }

    private Brush ResolveBrush(string resourceKey)
    {
        return BrushResources.Resolve(this, resourceKey);
    }

    private SKColor ResolveSkColor(string resourceKey, byte? alpha = null)
    {
        var color = BrushResources.ResolveColor(this, resourceKey);
        return new SKColor(color.R, color.G, color.B, alpha ?? color.A);
    }

    private static string FormatTimestamp(string? rawTimestamp)
    {
        return DateTime.TryParse(rawTimestamp,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp.ToString("g")
            : "time unavailable";
    }

    private static string BuildAxisSubLabel(string? rawTimestamp, int index)
    {
        return DateTime.TryParse(rawTimestamp,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp.ToString("MM/dd HH:mm")
            : $"Run {index + 1}";
    }
}
