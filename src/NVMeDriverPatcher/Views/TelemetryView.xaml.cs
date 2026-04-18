using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using NVMeDriverPatcher.Models;
using SkiaSharp;

namespace NVMeDriverPatcher.Views;

public partial class TelemetryView : UserControl
{
    private bool _suppressDriveSelectionNotification;

    // Cache frozen brushes for fallback paths. BrushConverter isn't thread-safe and
    // re-parsing the same hex string on every color update is wasteful.
    private static readonly Dictionary<string, Brush> _fallbackBrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _fallbackBrushLock = new();

    public event Action<int>? DriveSelected;

    public TelemetryView()
    {
        InitializeComponent();
        SetupChartDefaults();
        Reset();
    }

    public int? SetDrives(List<SystemDrive>? drives, int? selectedDriveNumber = null)
    {
        _suppressDriveSelectionNotification = true;
        try
        {
            selectedDriveNumber ??= GetSelectedDriveNumber();
            DriveSelector.Items.Clear();

            int selectedIndex = -1;
            int currentIndex = 0;

            foreach (var drive in (drives ?? new List<SystemDrive>()).Where(d => d.IsNVMe))
            {
                DriveSelector.Items.Add(new ComboBoxItem
                {
                    Content = $"Disk {drive.Number}: {drive.Name}",
                    Tag = drive.Number
                });

                if (selectedDriveNumber == drive.Number)
                    selectedIndex = currentIndex;

                currentIndex++;
            }

            DriveSelector.IsEnabled = DriveSelector.Items.Count > 1;

            if (DriveSelector.Items.Count == 0)
            {
                DriveSelector.SelectedIndex = -1;
                return null;
            }

            DriveSelector.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            return GetSelectedDriveNumber();
        }
        finally
        {
            _suppressDriveSelectionNotification = false;
        }
    }

    public void Reset()
    {
        _suppressDriveSelectionNotification = true;
        try
        {
            DriveSelector.Items.Clear();
            DriveSelector.SelectedIndex = -1;
            DriveSelector.IsEnabled = false;
        }
        finally
        {
            _suppressDriveSelectionNotification = false;
        }

        SetTelemetryStatus("Choose an NVMe drive to capture a fresh health snapshot and review recent history.");
        ResetTelemetryContext();
        UpdateCurrentHealth(null);
        UpdateTempHistory([]);
        UpdateWearHistory([]);
    }

    public void SetTelemetryStatus(string message, string tone = "muted")
    {
        TelemetryStatusText.Text = message;

        string foregroundKey;
        string foregroundFallback;
        string backgroundKey;
        string backgroundFallback;
        string borderKey;
        string borderFallback;

        switch (tone)
        {
            case "success":
                foregroundKey = "Green";
                foregroundFallback = "#FF50DD9D";
                backgroundKey = "GreenBg";
                backgroundFallback = "#FF13392C";
                borderKey = "Green";
                borderFallback = "#FF50DD9D";
                break;
            case "warning":
                foregroundKey = "Yellow";
                foregroundFallback = "#FFFFC86C";
                backgroundKey = "YellowBg";
                backgroundFallback = "#FF3A2A11";
                borderKey = "Yellow";
                borderFallback = "#FFFFC86C";
                break;
            case "danger":
                foregroundKey = "Red";
                foregroundFallback = "#FFFF8585";
                backgroundKey = "RedBg";
                backgroundFallback = "#FF3A1A1D";
                borderKey = "Red";
                borderFallback = "#FFFF8585";
                break;
            case "info":
                foregroundKey = "Accent";
                foregroundFallback = "#FF69AEFF";
                backgroundKey = "AccentBg";
                backgroundFallback = "#FF102845";
                borderKey = "Accent";
                borderFallback = "#FF69AEFF";
                break;
            default:
                foregroundKey = "TextDim";
                foregroundFallback = "#FF8393AD";
                backgroundKey = "SurfaceInset";
                backgroundFallback = "#FF09111B";
                borderKey = "Border";
                borderFallback = "#FF213146";
                break;
        }

        TelemetryStatusText.Foreground = ResolveBrush(foregroundKey, foregroundFallback);
        TelemetryStatusCard.Background = ResolveBrush(backgroundKey, backgroundFallback);
        TelemetryStatusCard.BorderBrush = ResolveBrush(borderKey, borderFallback);
    }

    public void UpdateCurrentHealth(NVMeHealthInfo? health)
    {
        if (health is null)
        {
            TempValue.Text = "-";
            WearValue.Text = "-";
            PohValue.Text = "-";
            ErrorsValue.Text = "-";
            TempValue.ToolTip = null;
            WearValue.ToolTip = null;
            PohValue.ToolTip = null;
            ErrorsValue.ToolTip = null;
            ApplyDefaultMetricBrushes();
            return;
        }

        TempValue.Text = health.Temperature;
        WearValue.Text = health.Wear;
        PohValue.Text = health.PowerOnHours;
        ErrorsValue.Text = health.MediaErrors.ToString();
        PohValue.Foreground = ResolveBrush("Accent", "#FF69AEFF");

        var tooltip = string.IsNullOrWhiteSpace(health.SmartTooltip) ? null : health.SmartTooltip;
        TempValue.ToolTip = tooltip;
        WearValue.ToolTip = tooltip;
        PohValue.ToolTip = tooltip;
        ErrorsValue.ToolTip = tooltip;

        // Color temp gauge
        int temp = ExtractNumericValue(health.Temperature);
        TempValue.Foreground = ResolveBrush(
            temp >= 70 ? "Red" : temp >= 50 ? "Yellow" : "Green",
            temp >= 70 ? "#FFFF8585" : temp >= 50 ? "#FFFFC86C" : "#FF50DD9D");

        int lifeRemaining = ExtractNumericValue(health.Wear);
        WearValue.Foreground = ResolveBrush(
            lifeRemaining <= 20 ? "Red" : lifeRemaining <= 50 ? "Yellow" : "Green",
            lifeRemaining <= 20 ? "#FFFF8585" : lifeRemaining <= 50 ? "#FFFFC86C" : "#FF50DD9D");

        // Color errors gauge
        ErrorsValue.Foreground = health.MediaErrors > 0
            ? ResolveBrush("Red", "#FFFF8585")
            : ResolveBrush("TextSecondary", "#FFD9E4F4");
    }

    public void UpdateTempHistory(List<(DateTime Time, int TempC)>? data)
    {
        data ??= new List<(DateTime, int)>();
        if (data.Count == 0)
        {
            TempChart.Series = [];
            TempHistoryPlaceholder.Visibility = System.Windows.Visibility.Visible;
            return;
        }

        TempHistoryPlaceholder.Visibility = System.Windows.Visibility.Collapsed;

        TempChart.Series = new ISeries[]
        {
            new LineSeries<ObservablePoint>
            {
                Values = data.Select(d => new ObservablePoint(d.Time.Ticks, d.TempC)).ToArray(),
                Stroke = new SolidColorPaint(new SKColor(255, 133, 133), 2),
                Fill = new SolidColorPaint(new SKColor(255, 133, 133, 36)),
                GeometrySize = 0,
                LineSmoothness = 0.3
            }
        };
    }

    public void UpdateWearHistory(List<(DateTime Time, int WearPct)>? data)
    {
        data ??= new List<(DateTime, int)>();
        if (data.Count == 0)
        {
            WearChart.Series = [];
            WearHistoryPlaceholder.Visibility = System.Windows.Visibility.Visible;
            return;
        }

        WearHistoryPlaceholder.Visibility = System.Windows.Visibility.Collapsed;

        WearChart.Series = new ISeries[]
        {
            new LineSeries<ObservablePoint>
            {
                Values = data.Select(d => new ObservablePoint(d.Time.Ticks, d.WearPct)).ToArray(),
                Stroke = new SolidColorPaint(new SKColor(80, 221, 157), 2),
                Fill = new SolidColorPaint(new SKColor(80, 221, 157, 36)),
                GeometrySize = 0,
                LineSmoothness = 0.3
            }
        };
    }

    public void UpdateTelemetryContext(
        int driveNumber,
        bool hasLiveSnapshot,
        bool hasFallbackSnapshot,
        List<(DateTime Time, int TempC)> tempHistory,
        List<(DateTime Time, int WearPct)> wearHistory)
    {
        if (hasLiveSnapshot)
        {
            TelemetrySourceValue.Text = "Live SMART";
            TelemetrySourceValue.Foreground = ResolveBrush("Green", "#FF50DD9D");
            TelemetrySourceDetail.Text = $"Disk {driveNumber} returned a direct controller snapshot during this refresh.";
        }
        else if (hasFallbackSnapshot)
        {
            TelemetrySourceValue.Text = "Windows fallback";
            TelemetrySourceValue.Foreground = ResolveBrush("Yellow", "#FFFFC86C");
            TelemetrySourceDetail.Text = $"Disk {driveNumber} is using Windows reliability counters because direct SMART polling was unavailable.";
        }
        else
        {
            TelemetrySourceValue.Text = "No current snapshot";
            TelemetrySourceValue.Foreground = ResolveBrush("TextDim", "#FF8393AD");
            TelemetrySourceDetail.Text = $"Disk {driveNumber} has no live or fallback health snapshot yet.";
        }

        int sampleCount = Math.Max(tempHistory.Count, wearHistory.Count);
        if (sampleCount == 0)
        {
            TelemetryHistoryValue.Text = "No saved history";
            TelemetryHistoryDetail.Text = "Poll this drive over time to build a rolling seven-day local record.";
        }
        else
        {
            var timestamps = tempHistory.Select(point => point.Time)
                .Concat(wearHistory.Select(point => point.Time))
                .OrderBy(time => time)
                .ToList();

            var firstTimestamp = timestamps.First();
            var lastTimestamp = timestamps.Last();
            TelemetryHistoryValue.Text = sampleCount == 1
                ? "1 saved sample"
                : $"{sampleCount} saved samples";
            TelemetryHistoryDetail.Text = sampleCount == 1
                ? $"Latest sample captured {lastTimestamp:g}."
                : $"Coverage runs from {firstTimestamp:g} through {lastTimestamp:g}.";
        }

        TelemetryTrendValue.Text = BuildTrendHeadline(tempHistory, wearHistory);
        TelemetryTrendDetail.Text = BuildTrendDetail(tempHistory, wearHistory);
    }

    private void SetupChartDefaults()
    {
        var darkAxis = new Axis
        {
            LabelsPaint = new SolidColorPaint(new SKColor(131, 147, 173)),
            TextSize = 10,
            SeparatorsPaint = new SolidColorPaint(new SKColor(34, 49, 70))
        };

        TempChart.XAxes = new[] { new Axis { IsVisible = false } };
        TempChart.YAxes = new[] { new Axis { LabelsPaint = darkAxis.LabelsPaint, TextSize = 10, SeparatorsPaint = darkAxis.SeparatorsPaint, Labeler = v => $"{v}C" } };
        WearChart.XAxes = new[] { new Axis { IsVisible = false } };
        WearChart.YAxes = new[] { new Axis { LabelsPaint = darkAxis.LabelsPaint, TextSize = 10, SeparatorsPaint = darkAxis.SeparatorsPaint, MinLimit = 0, MaxLimit = 100, Labeler = v => $"{v}%" } };
    }

    private void DriveSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDriveSelectionNotification)
            return;

        if (DriveSelector.SelectedItem is ComboBoxItem item && item.Tag is int driveNum)
            DriveSelected?.Invoke(driveNum);
    }

    private void ApplyDefaultMetricBrushes()
    {
        TempValue.Foreground = ResolveBrush("TextSecondary", "#FFD9E4F4");
        WearValue.Foreground = ResolveBrush("TextSecondary", "#FFD9E4F4");
        PohValue.Foreground = ResolveBrush("Accent", "#FF69AEFF");
        ErrorsValue.Foreground = ResolveBrush("TextSecondary", "#FFD9E4F4");
    }

    private void ResetTelemetryContext()
    {
        TelemetrySourceValue.Text = "Waiting for a drive";
        TelemetrySourceValue.Foreground = ResolveBrush("Accent", "#FF69AEFF");
        TelemetrySourceDetail.Text = "Choose an NVMe drive to see whether the current snapshot comes from live SMART or Windows fallback data.";
        TelemetryHistoryValue.Text = "No saved history";
        TelemetryHistoryDetail.Text = "Saved samples will appear here once this machine records local telemetry over time.";
        TelemetryTrendValue.Text = "Trend unavailable";
        TelemetryTrendDetail.Text = "A single snapshot is useful for health checks, but trends become meaningful only after more local samples are saved.";
    }

    private int? GetSelectedDriveNumber()
    {
        return DriveSelector.SelectedItem is ComboBoxItem item && item.Tag is int driveNumber
            ? driveNumber
            : null;
    }

    private Brush ResolveBrush(string resourceKey, string fallbackHex)
    {
        if (TryFindResource(resourceKey) is Brush b) return b;
        return GetFallbackBrush(fallbackHex);
    }

    private static Brush GetFallbackBrush(string fallbackHex)
    {
        lock (_fallbackBrushLock)
        {
            if (_fallbackBrushCache.TryGetValue(fallbackHex, out var cached))
                return cached;

            var brush = ParseHexBrush(fallbackHex) ?? System.Windows.Media.Brushes.Gray;
            _fallbackBrushCache[fallbackHex] = brush;
            return brush;
        }
    }

    private static Brush? ParseHexBrush(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return null;
        var digits = hex.AsSpan(1);
        if (digits.Length != 8 && digits.Length != 6) return null;

        byte a = 0xFF, r, g, b;
        int i = 0;
        if (digits.Length == 8)
        {
            if (!byte.TryParse(digits.Slice(i, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out a)) return null;
            i += 2;
        }
        if (!byte.TryParse(digits.Slice(i, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out r)) return null;
        if (!byte.TryParse(digits.Slice(i + 2, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out g)) return null;
        if (!byte.TryParse(digits.Slice(i + 4, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out b)) return null;

        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static int ExtractNumericValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return int.TryParse(System.Text.RegularExpressions.Regex.Replace(value, @"[^0-9]", ""), out int numericValue)
            ? numericValue
            : 0;
    }

    private static string BuildTrendHeadline(
        List<(DateTime Time, int TempC)> tempHistory,
        List<(DateTime Time, int WearPct)> wearHistory)
    {
        bool hasTempTrend = tempHistory.Count >= 2;
        bool hasWearTrend = wearHistory.Count >= 2;

        if (!hasTempTrend && !hasWearTrend)
            return "Waiting for more samples";

        if (hasTempTrend)
        {
            int tempDelta = tempHistory[^1].TempC - tempHistory[0].TempC;
            if (Math.Abs(tempDelta) <= 2)
                return hasWearTrend ? "Thermals stable so far" : "Temperature holding steady";

            return tempDelta > 0 ? $"Temperature up {tempDelta}C" : $"Temperature down {Math.Abs(tempDelta)}C";
        }

        int wearDelta = wearHistory[^1].WearPct - wearHistory[0].WearPct;
        return wearDelta switch
        {
            0 => "Life remaining unchanged",
            < 0 => $"Life remaining down {Math.Abs(wearDelta)} point{(Math.Abs(wearDelta) == 1 ? "" : "s")}",
            _ => $"Life remaining up {wearDelta} point{(wearDelta == 1 ? "" : "s")}"
        };
    }

    private static string BuildTrendDetail(
        List<(DateTime Time, int TempC)> tempHistory,
        List<(DateTime Time, int WearPct)> wearHistory)
    {
        string tempDetail = tempHistory.Count switch
        {
            0 => "Temperature history is empty.",
            1 => $"One temperature sample captured at {tempHistory[0].TempC}C.",
            _ => DescribeTemperatureTrend(tempHistory)
        };

        string wearDetail = wearHistory.Count switch
        {
            0 => "Wear history is empty.",
            1 => $"One life-remaining sample captured at {wearHistory[0].WearPct}%.",
            _ => DescribeWearTrend(wearHistory)
        };

        return $"{tempDetail} {wearDetail}";
    }

    private static string DescribeTemperatureTrend(List<(DateTime Time, int TempC)> tempHistory)
    {
        int earliest = tempHistory[0].TempC;
        int latest = tempHistory[^1].TempC;
        int delta = latest - earliest;

        return Math.Abs(delta) <= 2
            ? $"Temperature has stayed within {Math.Abs(delta)}C of the earliest saved sample."
            : delta > 0
                ? $"Temperature climbed from {earliest}C to {latest}C across the saved window."
                : $"Temperature dropped from {earliest}C to {latest}C across the saved window.";
    }

    private static string DescribeWearTrend(List<(DateTime Time, int WearPct)> wearHistory)
    {
        int earliest = wearHistory[0].WearPct;
        int latest = wearHistory[^1].WearPct;
        int delta = latest - earliest;

        return delta switch
        {
            0 => "Life remaining has not moved across the saved samples.",
            < 0 => $"Life remaining fell from {earliest}% to {latest}% across the saved samples.",
            _ => $"Life remaining rose from {earliest}% to {latest}%, which can happen when controller reporting recalibrates."
        };
    }
}
