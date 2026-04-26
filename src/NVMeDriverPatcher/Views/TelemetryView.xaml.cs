using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using SkiaSharp;

namespace NVMeDriverPatcher.Views;

public partial class TelemetryView : UserControl
{
    private static readonly Regex RxDigitsOnly = new(@"[^0-9]", RegexOptions.Compiled);

    private bool _suppressDriveSelectionNotification;
    private List<(DateTime Time, int TempC)> _lastTempHistory = [];
    private List<(DateTime Time, int WearPct)> _lastWearHistory = [];

    public event Action<int>? DriveSelected;

    public TelemetryView()
    {
        InitializeComponent();
        ThemeService.ThemeChanged += ThemeService_ThemeChanged;
        Unloaded += TelemetryView_Unloaded;
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
                var driveItem = new ComboBoxItem
                {
                    Content = $"Disk {drive.Number}: {drive.Name}",
                    Tag = drive.Number
                };

                if (TryFindResource("DarkComboBoxItem") is Style itemStyle)
                    driveItem.Style = itemStyle;

                DriveSelector.Items.Add(driveItem);

                if (selectedDriveNumber == drive.Number)
                    selectedIndex = currentIndex;

                currentIndex++;
            }

            DriveSelector.IsEnabled = DriveSelector.Items.Count > 0;

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
        string backgroundKey;
        string borderKey;

        switch (tone)
        {
            case "success":
                foregroundKey = "Green";
                backgroundKey = "GreenBg";
                borderKey = "Green";
                break;
            case "warning":
                foregroundKey = "Yellow";
                backgroundKey = "YellowBg";
                borderKey = "Yellow";
                break;
            case "danger":
                foregroundKey = "Red";
                backgroundKey = "RedBg";
                borderKey = "Red";
                break;
            case "info":
                foregroundKey = "Accent";
                backgroundKey = "AccentBg";
                borderKey = "Accent";
                break;
            default:
                foregroundKey = "TextDim";
                backgroundKey = "SurfaceInset";
                borderKey = "Border";
                break;
        }

        TelemetryStatusText.Foreground = ResolveBrush(foregroundKey);
        TelemetryStatusCard.Background = ResolveBrush(backgroundKey);
        TelemetryStatusCard.BorderBrush = ResolveBrush(borderKey);
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
        PohValue.Foreground = ResolveBrush("Accent");

        var tooltip = string.IsNullOrWhiteSpace(health.SmartTooltip) ? null : health.SmartTooltip;
        TempValue.ToolTip = tooltip;
        WearValue.ToolTip = tooltip;
        PohValue.ToolTip = tooltip;
        ErrorsValue.ToolTip = tooltip;

        // Color temp gauge
        int temp = ExtractNumericValue(health.Temperature);
        TempValue.Foreground = ResolveBrush(
            temp >= 70 ? "Red" : temp >= 50 ? "Yellow" : "Green");

        int lifeRemaining = ExtractNumericValue(health.Wear);
        WearValue.Foreground = ResolveBrush(
            lifeRemaining <= 20 ? "Red" : lifeRemaining <= 50 ? "Yellow" : "Green");

        // Color errors gauge
        ErrorsValue.Foreground = health.MediaErrors > 0
            ? ResolveBrush("Red")
            : ResolveBrush("TextSecondary");
    }

    public void UpdateTempHistory(List<(DateTime Time, int TempC)>? data)
    {
        data ??= new List<(DateTime, int)>();
        _lastTempHistory = data.ToList();
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
                Stroke = new SolidColorPaint(ResolveSkColor("Red"), 2),
                Fill = new SolidColorPaint(ResolveSkColor("Red", 36)),
                GeometrySize = 0,
                LineSmoothness = 0.3
            }
        };
    }

    public void UpdateWearHistory(List<(DateTime Time, int WearPct)>? data)
    {
        data ??= new List<(DateTime, int)>();
        _lastWearHistory = data.ToList();
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
                Stroke = new SolidColorPaint(ResolveSkColor("Green"), 2),
                Fill = new SolidColorPaint(ResolveSkColor("Green", 36)),
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
            TelemetrySourceValue.Foreground = ResolveBrush("Green");
            TelemetrySourceDetail.Text = $"Disk {driveNumber} returned a direct controller snapshot during this refresh.";
        }
        else if (hasFallbackSnapshot)
        {
            TelemetrySourceValue.Text = "Windows fallback";
            TelemetrySourceValue.Foreground = ResolveBrush("Yellow");
            TelemetrySourceDetail.Text = $"Disk {driveNumber} is using Windows reliability counters because direct SMART polling was unavailable.";
        }
        else
        {
            TelemetrySourceValue.Text = "No current snapshot";
            TelemetrySourceValue.Foreground = ResolveBrush("TextDim");
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
        var labelPaint = new SolidColorPaint(ResolveSkColor("TextDim"));
        var separatorPaint = new SolidColorPaint(ResolveSkColor("Border"));
        var darkAxis = new Axis
        {
            LabelsPaint = labelPaint,
            TextSize = 10,
            SeparatorsPaint = separatorPaint
        };

        TempChart.XAxes = new[] { new Axis { IsVisible = false } };
        TempChart.YAxes = new[] { new Axis { LabelsPaint = darkAxis.LabelsPaint, TextSize = 10, SeparatorsPaint = darkAxis.SeparatorsPaint, Labeler = v => $"{v}C" } };
        WearChart.XAxes = new[] { new Axis { IsVisible = false } };
        WearChart.YAxes = new[] { new Axis { LabelsPaint = darkAxis.LabelsPaint, TextSize = 10, SeparatorsPaint = darkAxis.SeparatorsPaint, MinLimit = 0, MaxLimit = 100, Labeler = v => $"{v}%" } };
    }

    private void ThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        SetupChartDefaults();
        UpdateTempHistory(_lastTempHistory);
        UpdateWearHistory(_lastWearHistory);
    }

    private void TelemetryView_Unloaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
        Unloaded -= TelemetryView_Unloaded;
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
        TempValue.Foreground = ResolveBrush("TextSecondary");
        WearValue.Foreground = ResolveBrush("TextSecondary");
        PohValue.Foreground = ResolveBrush("Accent");
        ErrorsValue.Foreground = ResolveBrush("TextSecondary");
    }

    private void ResetTelemetryContext()
    {
        TelemetrySourceValue.Text = "Waiting for a drive";
        TelemetrySourceValue.Foreground = ResolveBrush("Accent");
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

    private Brush ResolveBrush(string resourceKey)
    {
        return BrushResources.Resolve(this, resourceKey);
    }

    private SKColor ResolveSkColor(string resourceKey, byte? alpha = null)
    {
        var color = BrushResources.ResolveColor(this, resourceKey);
        return new SKColor(color.R, color.G, color.B, alpha ?? color.A);
    }

    private static int ExtractNumericValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return int.TryParse(RxDigitsOnly.Replace(value, ""), out int numericValue)
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
