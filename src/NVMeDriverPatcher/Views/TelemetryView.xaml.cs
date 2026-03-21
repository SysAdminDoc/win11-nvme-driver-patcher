using System.Windows.Controls;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using NVMeDriverPatcher.Models;
using SkiaSharp;

namespace NVMeDriverPatcher.Views;

public partial class TelemetryView : UserControl
{
    public event Action<int>? DriveSelected;

    public TelemetryView()
    {
        InitializeComponent();
        SetupChartDefaults();
    }

    public void SetDrives(List<SystemDrive> drives)
    {
        DriveSelector.Items.Clear();
        foreach (var d in drives.Where(d => d.IsNVMe))
            DriveSelector.Items.Add(new ComboBoxItem { Content = $"Disk {d.Number}: {d.Name}", Tag = d.Number });
        if (DriveSelector.Items.Count > 0)
            DriveSelector.SelectedIndex = 0;
    }

    public void UpdateCurrentHealth(NVMeHealthInfo? health)
    {
        if (health is null)
        {
            TempValue.Text = "-";
            WearValue.Text = "-";
            PohValue.Text = "-";
            ErrorsValue.Text = "-";
            return;
        }

        TempValue.Text = health.Temperature;
        WearValue.Text = health.Wear;
        PohValue.Text = health.PowerOnHours;
        ErrorsValue.Text = health.MediaErrors.ToString();

        // Color temp gauge
        var bc = new System.Windows.Media.BrushConverter();
        if (int.TryParse(System.Text.RegularExpressions.Regex.Replace(health.Temperature, @"[^0-9]", ""), out int temp))
            TempValue.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString(temp >= 70 ? "#FFef4444" : temp >= 50 ? "#FFf59e0b" : "#FF22c55e")!;

        // Color errors gauge
        ErrorsValue.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString(health.MediaErrors > 0 ? "#FFef4444" : "#FFd4d4d8")!;
    }

    public void UpdateTempHistory(List<(DateTime Time, int TempC)> data)
    {
        if (data.Count == 0) { TempChart.Series = []; return; }

        TempChart.Series = new ISeries[]
        {
            new LineSeries<ObservablePoint>
            {
                Values = data.Select(d => new ObservablePoint(d.Time.Ticks, d.TempC)).ToArray(),
                Stroke = new SolidColorPaint(new SKColor(239, 68, 68), 2),
                Fill = new SolidColorPaint(new SKColor(239, 68, 68, 30)),
                GeometrySize = 0,
                LineSmoothness = 0.3
            }
        };
    }

    public void UpdateWearHistory(List<(DateTime Time, int WearPct)> data)
    {
        if (data.Count == 0) { WearChart.Series = []; return; }

        WearChart.Series = new ISeries[]
        {
            new LineSeries<ObservablePoint>
            {
                Values = data.Select(d => new ObservablePoint(d.Time.Ticks, d.WearPct)).ToArray(),
                Stroke = new SolidColorPaint(new SKColor(34, 197, 94), 2),
                Fill = new SolidColorPaint(new SKColor(34, 197, 94, 30)),
                GeometrySize = 0,
                LineSmoothness = 0.3
            }
        };
    }

    private void SetupChartDefaults()
    {
        var darkAxis = new Axis
        {
            LabelsPaint = new SolidColorPaint(new SKColor(113, 113, 122)),
            TextSize = 9,
            SeparatorsPaint = new SolidColorPaint(new SKColor(26, 26, 30))
        };

        TempChart.XAxes = new[] { new Axis { IsVisible = false } };
        TempChart.YAxes = new[] { new Axis { LabelsPaint = darkAxis.LabelsPaint, TextSize = 9, SeparatorsPaint = darkAxis.SeparatorsPaint, Labeler = v => $"{v}C" } };
        WearChart.XAxes = new[] { new Axis { IsVisible = false } };
        WearChart.YAxes = new[] { new Axis { LabelsPaint = darkAxis.LabelsPaint, TextSize = 9, SeparatorsPaint = darkAxis.SeparatorsPaint, MinLimit = 0, MaxLimit = 100, Labeler = v => $"{v}%" } };
    }

    private void DriveSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DriveSelector.SelectedItem is ComboBoxItem item && item.Tag is int driveNum)
            DriveSelected?.Invoke(driveNum);
    }
}
