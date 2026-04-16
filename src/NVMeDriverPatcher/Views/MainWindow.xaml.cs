using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using NVMeDriverPatcher.Data;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private int? _selectedTelemetryDriveNumber;
    private int _telemetryWorkspaceRefreshId;
    private int _telemetryDataRefreshId;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        _vm.ConfirmDialog = ShowConfirmDialog;
        _vm.InfoDialog = ShowInfoDialog;
        DataContext = _vm;
        _vm.PropertyChanged += ViewModel_PropertyChanged;
        TelemetryPanelControl.DriveSelected += TelemetryPanel_DriveSelected;
        TuningPanelControl.LogMessage += TuningPanel_LogMessage;

        // Set window icon from embedded resource (pack URI fails in single-file publish)
        try
        {
            var iconUri = new Uri("pack://application:,,,/nvme.ico", UriKind.Absolute);
            Icon = BitmapFrame.Create(iconUri);
        }
        catch { /* Icon load best-effort */ }

        var workArea = SystemParameters.WorkArea;
        if (Height > workArea.Height) Height = workArea.Height - 20;
        if (Width > workArea.Width) Width = workArea.Width - 20;

        ContentRendered += OnContentRendered;
        Closing += (_, _) => _vm.OnClosing();
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        await _vm.RunPreflightAsync();
        RefreshBenchmarkWorkspace();
        await RefreshTelemetryWorkspaceAsync();
    }

    private bool ShowConfirmDialog(string title, string message)
    {
        return ThemedDialog.Show(message, title, DialogButtons.YesNo, DialogIcon.Question, this) == "Yes";
    }

    private void ShowInfoDialog(string title, string message, DialogIcon icon)
    {
        ThemedDialog.Show(message, title, DialogButtons.OK, icon, this);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateBadge_Click(object sender, MouseButtonEventArgs e) => _vm.OpenUpdateUrlCommand.Execute(null);
    private void GitHub_Click(object sender, RoutedEventArgs e) => _vm.OpenGitHubCommand.Execute(null);
    private void Docs_Click(object sender, RoutedEventArgs e) => _vm.OpenDocsCommand.Execute(null);

    private void SettingsToggle_Click(object sender, MouseButtonEventArgs e) => _vm.ToggleSettingsCommand.Execute(null);

    private void LogOutput_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogScroller?.ScrollToBottom();
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(LogOutput.SelectedText))
            Clipboard.SetText(LogOutput.SelectedText);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        LogOutput.SelectAll();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearLog();
    }

    private void RecommendedPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        ExecuteRecommendedAction(_vm.NextStepPrimaryActionId);
    }

    private void RecommendedSecondaryAction_Click(object sender, RoutedEventArgs e)
    {
        ExecuteRecommendedAction(_vm.NextStepSecondaryActionId);
    }

    private async void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, WorkspaceTabs))
            return;

        switch (WorkspaceTabs.SelectedIndex)
        {
            case 1:
                RefreshBenchmarkWorkspace();
                break;
            case 2:
                await RefreshTelemetryWorkspaceAsync();
                break;
            case 3:
                _vm.RefreshRecoveryAssetsCommand.Execute(null);
                break;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.BenchLabelText) or nameof(MainViewModel.StatusText))
            RefreshBenchmarkWorkspace();

        if (e.PropertyName == nameof(MainViewModel.DriveInventorySummaryText))
            _ = RefreshTelemetryWorkspaceAsync();
    }

    private void ExecuteRecommendedAction(string? actionId)
    {
        switch (actionId)
        {
            case "apply_patch":
                if (_vm.ApplyPatchCommand.CanExecute(null))
                    _vm.ApplyPatchCommand.Execute(null);
                break;
            case "create_backup":
                if (_vm.RunBackupCommand.CanExecute(null))
                    _vm.RunBackupCommand.Execute(null);
                break;
            case "run_benchmark":
                if (_vm.RunBenchmarkCommand.CanExecute(null))
                    _vm.RunBenchmarkCommand.Execute(null);
                break;
            case "export_diagnostics":
                if (_vm.ExportDiagnosticsCommand.CanExecute(null))
                    _vm.ExportDiagnosticsCommand.Execute(null);
                break;
            case "create_recovery_kit":
                if (_vm.ExportRecoveryKitCommand.CanExecute(null))
                    _vm.ExportRecoveryKitCommand.Execute(null);
                break;
            case "refresh_checks":
                if (_vm.RefreshCommand.CanExecute(null))
                    _vm.RefreshCommand.Execute(null);
                break;
            case "open_activity":
                SelectWorkspaceTab(0);
                break;
            case "open_benchmarks":
                SelectWorkspaceTab(1);
                break;
            case "open_telemetry":
                SelectWorkspaceTab(2);
                break;
            case "open_recovery":
                SelectWorkspaceTab(3);
                break;
        }
    }

    private void SelectWorkspaceTab(int index)
    {
        if (WorkspaceTabs.SelectedIndex == index)
        {
            switch (index)
            {
                case 1:
                    RefreshBenchmarkWorkspace();
                    break;
                case 2:
                    _ = RefreshTelemetryWorkspaceAsync();
                    break;
                case 3:
                    _vm.RefreshRecoveryAssetsCommand.Execute(null);
                    break;
            }

            return;
        }

        WorkspaceTabs.SelectedIndex = index;
    }

    private void RefreshBenchmarkWorkspace()
    {
        var history = BenchmarkService.GetHistory(_vm.Config.WorkingDir);
        bool hasHistory = history.Count > 0;

        BenchmarkEmptyState.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
        BenchmarkWorkspacePanel.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;

        if (hasHistory)
            BenchmarkView.UpdateChart(history);
    }

    private async void RefreshTelemetry_Click(object sender, RoutedEventArgs e)
    {
        await RefreshTelemetryWorkspaceAsync();
    }

    private async void TelemetryPanel_DriveSelected(int driveNumber)
    {
        _selectedTelemetryDriveNumber = driveNumber;
        await RefreshTelemetryDataAsync(driveNumber);
    }

    private async Task RefreshTelemetryWorkspaceAsync()
    {
        int refreshId = Interlocked.Increment(ref _telemetryWorkspaceRefreshId);
        var nvmeDrives = (await Task.Run(DriveService.GetSystemDrives))
            .Where(d => d.IsNVMe)
            .ToList();

        if (refreshId != _telemetryWorkspaceRefreshId)
            return;

        bool hasNvmeDrives = nvmeDrives.Count > 0;
        TelemetryEmptyState.Visibility = hasNvmeDrives ? Visibility.Collapsed : Visibility.Visible;
        TelemetryWorkspacePanel.Visibility = hasNvmeDrives ? Visibility.Visible : Visibility.Collapsed;

        if (!hasNvmeDrives)
        {
            _selectedTelemetryDriveNumber = null;
            TelemetryPanelControl.Reset();
            return;
        }

        int? selectedDriveNumber = TelemetryPanelControl.SetDrives(nvmeDrives, _selectedTelemetryDriveNumber);
        if (selectedDriveNumber is int driveNumber)
        {
            _selectedTelemetryDriveNumber = driveNumber;
            await RefreshTelemetryDataAsync(driveNumber);
        }
    }

    private async Task RefreshTelemetryDataAsync(int driveNumber)
    {
        int requestId = Interlocked.Increment(ref _telemetryDataRefreshId);
        _selectedTelemetryDriveNumber = driveNumber;

        TelemetryPanelControl.SetTelemetryStatus(
            $"Polling Disk {driveNumber} for a fresh health snapshot and trend history.");

        var cachedHealthTask = Task.Run(DriveService.GetNVMeHealthData);
        var liveDataTask = NVMeTelemetryService.PollAsync(driveNumber);
        await Task.WhenAll(cachedHealthTask, liveDataTask);

        if (requestId != _telemetryDataRefreshId)
            return;

        var cachedHealth = cachedHealthTask.Result;
        cachedHealth.TryGetValue(driveNumber.ToString(), out NVMeHealthInfo? fallbackHealth);

        NVMeHealthData? liveData = liveDataTask.Result;
        NVMeHealthInfo? currentHealth = liveData is not null
            ? CreateHealthInfo(liveData)
            : fallbackHealth;

        if (liveData is not null)
        {
            TelemetryRecord? latestRecord = await Task.Run(() => DataService.GetLatestTelemetry(driveNumber));
            if (requestId != _telemetryDataRefreshId)
                return;

            if (ShouldPersistTelemetry(latestRecord, liveData))
            {
                await Task.Run(() => DataService.SaveTelemetry(
                    driveNumber,
                    liveData.TemperatureCelsius,
                    liveData.AvailableSpare,
                    liveData.PercentageUsed,
                    ClampToLong(liveData.DataUnitsRead),
                    ClampToLong(liveData.DataUnitsWritten),
                    ClampToLong(liveData.PowerOnHours),
                    ClampToInt(liveData.MediaErrors),
                    ClampToInt(liveData.UnsafeShutdowns)));

                if (requestId != _telemetryDataRefreshId)
                    return;
            }
        }

        var history = await Task.Run(() => DataService.GetTelemetryHistory(driveNumber, TimeSpan.FromDays(7)));
        if (requestId != _telemetryDataRefreshId)
            return;

        var tempHistory = history.Select(sample => (sample.Timestamp, sample.TemperatureCelsius)).ToList();
        var wearHistory = history.Select(sample => (sample.Timestamp, Math.Max(0, 100 - sample.PercentageUsed))).ToList();

        TelemetryPanelControl.UpdateCurrentHealth(currentHealth);
        TelemetryPanelControl.UpdateTelemetryContext(
            driveNumber,
            hasLiveSnapshot: liveData is not null,
            hasFallbackSnapshot: liveData is null && fallbackHealth is not null,
            tempHistory,
            wearHistory);
        TelemetryPanelControl.UpdateTempHistory(tempHistory);
        TelemetryPanelControl.UpdateWearHistory(wearHistory);

        if (liveData is not null)
        {
            TelemetryPanelControl.SetTelemetryStatus(
                $"Live SMART snapshot captured for Disk {driveNumber} at {DateTime.Now:t}. Showing the last 7 days of saved history.",
                "success");
        }
        else if (currentHealth is not null)
        {
            TelemetryPanelControl.SetTelemetryStatus(
                $"Direct SMART polling was unavailable for Disk {driveNumber}. Showing Windows storage reliability data and any saved history instead.",
                "warning");
        }
        else
        {
            TelemetryPanelControl.SetTelemetryStatus(
                $"No telemetry is available for Disk {driveNumber} yet. Try polling again after confirming Windows can access the drive normally.",
                "warning");
        }
    }

    private static NVMeHealthInfo CreateHealthInfo(NVMeHealthData data)
    {
        int lifeRemaining = Math.Max(0, 100 - data.PercentageUsed);
        int mediaErrors = ClampToInt(data.MediaErrors);
        long powerOnHours = ClampToLong(data.PowerOnHours);
        long unsafeShutdowns = ClampToLong(data.UnsafeShutdowns);

        var summary = new List<string>
        {
            $"Temperature: {data.TemperatureCelsius}C",
            $"Life remaining: {lifeRemaining}%",
            $"Available spare: {data.AvailableSpare}%",
            $"Power-on: {powerOnHours:N0}h"
        };

        if (unsafeShutdowns > 0)
            summary.Add($"Unsafe shutdowns: {unsafeShutdowns:N0}");
        if (mediaErrors > 0)
            summary.Add($"Media errors: {mediaErrors}");
        if (data.CriticalWarningRaw != 0)
            summary.Insert(0, $"Critical warning: 0x{data.CriticalWarningRaw:X2}");

        return new NVMeHealthInfo
        {
            Temperature = $"{data.TemperatureCelsius}C",
            Wear = $"{lifeRemaining}%",
            MediaErrors = mediaErrors,
            HealthStatus = data.CriticalWarningRaw == 0 ? "Healthy" : "Attention",
            OperationalStatus = data.CriticalWarningRaw == 0 ? "Live SMART snapshot" : "SMART warning present",
            PowerOnHours = $"{powerOnHours:N0}h",
            AvailableSpare = $"{data.AvailableSpare}%",
            SmartTooltip = string.Join(" | ", summary)
        };
    }

    private static bool ShouldPersistTelemetry(TelemetryRecord? latestRecord, NVMeHealthData data)
    {
        if (latestRecord is null)
            return true;

        if (DateTime.Now - latestRecord.Timestamp >= TimeSpan.FromMinutes(10))
            return true;

        return latestRecord.TemperatureCelsius != data.TemperatureCelsius
            || latestRecord.AvailableSparePercent != data.AvailableSpare
            || latestRecord.PercentageUsed != data.PercentageUsed
            || latestRecord.PowerOnHours != ClampToLong(data.PowerOnHours)
            || latestRecord.MediaErrors != ClampToInt(data.MediaErrors)
            || latestRecord.UnsafeShutdowns != ClampToInt(data.UnsafeShutdowns);
    }

    private static long ClampToLong(decimal value)
    {
        if (value <= 0)
            return 0;
        if (value >= long.MaxValue)
            return long.MaxValue;

        return decimal.ToInt64(decimal.Truncate(value));
    }

    private static int ClampToInt(decimal value)
    {
        if (value <= 0)
            return 0;
        if (value >= int.MaxValue)
            return int.MaxValue;

        return decimal.ToInt32(decimal.Truncate(value));
    }

    private void TuningPanel_LogMessage(string message)
    {
        string level = message.Contains("fail", StringComparison.OrdinalIgnoreCase)
            ? "ERROR"
            : message.Contains("applied", StringComparison.OrdinalIgnoreCase) || message.Contains("[OK]", StringComparison.OrdinalIgnoreCase)
                ? "SUCCESS"
                : "INFO";

        _vm.Log(message, level);
    }
}
