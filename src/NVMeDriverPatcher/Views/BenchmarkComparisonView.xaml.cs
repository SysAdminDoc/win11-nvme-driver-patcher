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

    public void UpdateChart(List<BenchmarkResult>? history)
    {
        history ??= new List<BenchmarkResult>();
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
            ApplyBenchmarkState("Waiting", NeutralBrush, NeutralBgBrush, NeutralBorderBrush);
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
            ApplyBenchmarkState("Comparison Ready", PositiveBrush, SuccessBgBrush, SuccessBorderBrush);
        }
        else
        {
            ReadDelta.Text = "Baseline captured";
            ReadDelta.Foreground = NeutralBrush;
            WriteDelta.Text = "Run another benchmark after a driver change to compare.";
            WriteDelta.Foreground = NeutralBrush;
            BenchmarkSummaryText.Text = "This first run is your baseline. Capture another run after applying or removing the patch so the comparison view can show direction, not just raw numbers.";
            TrendHintText.Text = "The chart currently reflects one baseline run. Add a post-change run to reveal direction instead of a single point in time.";
            ApplyBenchmarkState("Baseline Only", AccentBrush, AccentBgBrush, AccentBrush);
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
                Fill = new SolidColorPaint(new SKColor(105, 174, 255)),
                MaxBarWidth = 24,
                Padding = 4
            },
            new ColumnSeries<double>
            {
                Values = writeValues,
                Name = "Write IOPS",
                Fill = new SolidColorPaint(new SKColor(255, 200, 108)),
                MaxBarWidth = 24,
                Padding = 4
            }
        };

        BenchChart.XAxes = new[]
        {
            new Axis
            {
                Labels = labels,
                LabelsPaint = new SolidColorPaint(new SKColor(131, 147, 173)),
                TextSize = 10,
                SeparatorsPaint = new SolidColorPaint(new SKColor(34, 49, 70))
            }
        };

        BenchChart.YAxes = new[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(new SKColor(131, 147, 173)),
                TextSize = 10,
                SeparatorsPaint = new SolidColorPaint(new SKColor(34, 49, 70)),
                MinLimit = 0
            }
        };
    }

    private void ApplyBenchmarkState(
        string label,
        System.Windows.Media.SolidColorBrush foreground,
        System.Windows.Media.SolidColorBrush background,
        System.Windows.Media.SolidColorBrush border)
    {
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

    // Frozen brushes: safe to share across threads, bypass BrushConverter (not thread-safe),
    // and let WPF skip change-tracking overhead.
    private static readonly System.Windows.Media.SolidColorBrush PositiveBrush = ToBrush(0xFF, 0x50, 0xDD, 0x9D);
    private static readonly System.Windows.Media.SolidColorBrush NegativeBrush = ToBrush(0xFF, 0xFF, 0x85, 0x85);
    private static readonly System.Windows.Media.SolidColorBrush NeutralBrush = ToBrush(0xFF, 0x83, 0x93, 0xAD);
    private static readonly System.Windows.Media.SolidColorBrush AccentBrush = ToBrush(0xFF, 0x69, 0xAE, 0xFF);
    private static readonly System.Windows.Media.SolidColorBrush AccentBgBrush = ToBrush(0xFF, 0x10, 0x28, 0x45);
    private static readonly System.Windows.Media.SolidColorBrush SuccessBgBrush = ToBrush(0xFF, 0x13, 0x39, 0x2C);
    private static readonly System.Windows.Media.SolidColorBrush SuccessBorderBrush = ToBrush(0xFF, 0x50, 0xDD, 0x9D);
    private static readonly System.Windows.Media.SolidColorBrush NeutralBgBrush = ToBrush(0xFF, 0x13, 0x1C, 0x29);
    private static readonly System.Windows.Media.SolidColorBrush NeutralBorderBrush = ToBrush(0xFF, 0x34, 0x4B, 0x69);

    private static System.Windows.Media.SolidColorBrush ToBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.SolidColorBrush DeltaBrush(double prev, double current)
    {
        return current >= prev ? PositiveBrush : NegativeBrush;
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
