using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using NVMeDriverPatcher.Data;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Views;

public partial class MainWindow : Window
{
    private const double CompactLayoutThreshold = 1100;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private readonly MainViewModel _vm;
    private int? _selectedTelemetryDriveNumber;
    private int _telemetryWorkspaceRefreshId;
    private int _telemetryDataRefreshId;

    private bool _initialized;
    private bool _compactLayoutApplied;
    private bool _syncingThemeModeSelector;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(MainWindow_PreviewMouseWheel), true);
        _vm = new MainViewModel();
        _vm.ConfirmDialog = ShowConfirmDialog;
        _vm.InfoDialog = ShowInfoDialog;
        DataContext = _vm;
        _vm.PropertyChanged += ViewModel_PropertyChanged;
        TelemetryPanelControl.DriveSelected += TelemetryPanel_DriveSelected;
        TuningPanelControl.LogMessage += TuningPanel_LogMessage;
        ThemeService.ThemeChanged += ThemeService_ThemeChanged;
        UpdateThemeToggleButton();
        SyncThemeModeSelector();

        // Set window icon from embedded resource (pack URI fails in single-file publish)
        try
        {
            var iconUri = new Uri("pack://application:,,,/nvme.ico", UriKind.Absolute);
            Icon = BitmapFrame.Create(iconUri);
        }
        catch { /* Icon load best-effort */ }

        var workArea = GetPrimaryWorkArea();
        if (Height > workArea.Height - 32) Height = Math.Max(MinHeight, workArea.Height - 32);
        if (Width > workArea.Width - 32) Width = Math.Max(MinWidth, workArea.Width - 32);

        SourceInitialized += MainWindow_SourceInitialized;
        ContentRendered += OnContentRendered;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        SizeChanged += MainWindow_SizeChanged;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyScreenAwareStartupBounds();
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        EnsureWindowWithinWorkArea();
        ApplyNativeTitleBarTheme();
        UpdateWindowPresentation();
        UpdateAdaptiveLayout();
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        // ContentRendered can fire more than once across the lifetime of a Window (any time the
        // content tree is rebuilt). Guard against running the entire preflight multiple times.
        if (_initialized) return;
        _initialized = true;
        ContentRendered -= OnContentRendered;

        try
        {
            await _vm.RunPreflightAsync();
            RefreshBenchmarkWorkspace();
            await RefreshTelemetryWorkspaceAsync();
        }
        catch (Exception ex)
        {
            // Preflight shouldn't throw — RunPreflightAsync has its own try/catch — but a
            // dispatcher or WMI teardown mid-boot can still bubble. Log to the activity rail
            // (so the user sees SOMETHING instead of a blank "Checking…" state) and event log.
            System.Diagnostics.Debug.WriteLine($"[OnContentRendered] {ex}");
            try { _vm.Log($"Startup preflight failed: {ex.Message}", "ERROR"); } catch { }
            try
            {
                EventLogService.Write(
                    $"Startup preflight failed: {ex}",
                    System.Diagnostics.EventLogEntryType.Error,
                    3105);
            }
            catch { }
            try
            {
                ThemedDialog.Show(
                    "The readiness scan could not finish. You can still use Refresh on the toolbar, but drive details may be incomplete until it succeeds.\n\n" +
                    $"Detail: {ex.Message}",
                    "Readiness scan failed",
                    DialogButtons.OK,
                    DialogIcon.Warning,
                    this);
            }
            catch { }
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try { _vm.OnClosing(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OnClosing] {ex}"); }
        try { _vm.PropertyChanged -= ViewModel_PropertyChanged; } catch { }
        try { TelemetryPanelControl.DriveSelected -= TelemetryPanel_DriveSelected; } catch { }
        try { TuningPanelControl.LogMessage -= TuningPanel_LogMessage; } catch { }
        try { ThemeService.ThemeChanged -= ThemeService_ThemeChanged; } catch { }
        try { SourceInitialized -= MainWindow_SourceInitialized; } catch { }
        try { Loaded -= MainWindow_Loaded; } catch { }
        try { StateChanged -= MainWindow_StateChanged; } catch { }
        try { SizeChanged -= MainWindow_SizeChanged; } catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyNativeTitleBarTheme()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            bool dark = ThemeService.CurrentTheme is AppTheme.Dark or AppTheme.HighContrast;
            int useDarkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));

            int captionColor = ThemeService.CurrentTheme switch
            {
                AppTheme.HighContrast => 0x00000000,
                AppTheme.Dark => 0x00191411,
                _ => 0x00FEFBF9
            };
            int textColor = dark ? 0x00FFFFFF : 0x0020120B;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));
        }
        catch
        {
            // Native title-bar tinting is best-effort and unavailable on older Windows builds.
        }
    }

    private bool ShowConfirmDialog(string title, string message)
    {
        return ThemedDialog.Show(message, title, DialogButtons.YesNo, ResolveConfirmationIcon(title), this) == "Yes";
    }

    private void ShowInfoDialog(string title, string message, DialogIcon icon)
    {
        ThemedDialog.Show(message, title, DialogButtons.OK, icon, this);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is not null)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        try
        {
            // DragMove throws InvalidOperationException if called when the window is in a state
            // that doesn't allow dragging (e.g. while WPF is mid-resize). Swallow that — the
            // user just gets no drag, not a crash dialog.
            DragMove();
        }
        catch (InvalidOperationException) { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var nextMode = ThemeService.CurrentTheme is AppTheme.Dark or AppTheme.HighContrast
            ? AppThemeMode.Light
            : AppThemeMode.Dark;
        _vm.SetThemeMode(nextMode);
        UpdateWindowPresentation();
    }

    private void ThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        _vm.RefreshThemeModeSummary();
        UpdateThemeToggleButton();
        SyncThemeModeSelector();
        ApplyNativeTitleBarTheme();
    }

    private void ThemeModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThemeModeSelector || ThemeModeSelector.SelectedItem is not ComboBoxItem item)
            return;

        if (!Enum.TryParse<AppThemeMode>(item.Tag?.ToString(), ignoreCase: true, out var mode))
            mode = AppThemeMode.System;

        _vm.SetThemeMode(mode);
        UpdateWindowPresentation();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateWindowPresentation();
        UpdateAdaptiveLayout();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAdaptiveLayout();
    }

    private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (FindAncestor<System.Windows.Controls.ComboBox>(source)?.IsDropDownOpen == true)
            return;

        var scroller = FindAncestor<ScrollViewer>(source);
        while (scroller is not null)
        {
            if (TryScrollViewer(scroller, e.Delta))
            {
                e.Handled = true;
                return;
            }

            scroller = FindAncestor<ScrollViewer>(GetVisualParent(scroller));
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F5)
        {
            if (_vm.ButtonsEnabled && _vm.RefreshCommand.CanExecute(null))
                _vm.RefreshCommand.Execute(null);

            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            return;

        int? tabIndex = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            _ => null
        };

        if (tabIndex is int index)
        {
            SelectWorkspaceTab(index);
            e.Handled = true;
        }
    }

    private void ToggleMaximizeRestore()
    {
        if (ResizeMode == ResizeMode.NoResize)
            return;

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateWindowPresentation()
    {
        bool maximized = WindowState == WindowState.Maximized;

        RootFrame.Margin = new Thickness(0);
        ShellBorder.CornerRadius = new CornerRadius(0);
        ShellAccentBar.CornerRadius = new CornerRadius(0);
        ShellBorder.Effect = null;

        MaximizeRestoreButton.Content = maximized ? "❐" : "□";
        MaximizeRestoreButton.ToolTip = maximized ? "Restore down" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(
            MaximizeRestoreButton,
            maximized ? "Restore window" : "Maximize window");
    }

    private void EnsureWindowWithinWorkArea()
    {
        if (WindowState != WindowState.Normal)
            return;

        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        var center = new System.Windows.Point(Left + width / 2, Top + height / 2);
        var virtualBounds = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (virtualBounds.Contains(center))
            return;

        var workArea = GetCurrentWorkArea();
        Left = workArea.Left + Math.Max(16, (workArea.Width - width) / 2);
        Top = workArea.Top + Math.Max(16, (workArea.Height - height) / 2);

        if (Left + width > workArea.Right - 16)
            Left = Math.Max(workArea.Left + 16, workArea.Right - width - 16);

        if (Top + height > workArea.Bottom - 16)
            Top = Math.Max(workArea.Top + 16, workArea.Bottom - height - 16);
    }

    private void ApplyScreenAwareStartupBounds()
    {
        if (WindowState != WindowState.Normal)
            return;

        var workArea = GetCurrentWorkArea();
        const double horizontalMargin = 16;
        double maxWidth = Math.Max(MinWidth, workArea.Width - horizontalMargin * 2);
        double requestedWidth = double.IsNaN(Width) || Width <= 0 ? MinWidth : Width;

        Width = Math.Min(Math.Max(MinWidth, requestedWidth), maxWidth);
        Height = Math.Max(MinHeight, workArea.Height);
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top;
    }

    private static Rect GetPrimaryWorkArea()
    {
        // WPF window sizing is in device-independent units; SystemParameters returns
        // the matching work area while WinForms reports physical pixels on high-DPI screens.
        return SystemParameters.WorkArea;
    }

    private Rect GetCurrentWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return GetPrimaryWorkArea();

        try
        {
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            return DevicePixelsToDips(screen.WorkingArea, hwnd);
        }
        catch
        {
            return GetPrimaryWorkArea();
        }
    }

    private static Rect DevicePixelsToDips(System.Drawing.Rectangle pixels, IntPtr hwnd)
    {
        var source = HwndSource.FromHwnd(hwnd);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(pixels.Left, pixels.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(pixels.Right, pixels.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void UpdateAdaptiveLayout()
    {
        bool compact = ActualWidth < CompactLayoutThreshold;
        _compactLayoutApplied = compact;

        TitleBarRegion.Padding = compact
            ? new Thickness(16, 8, 16, 0)
            : new Thickness(20, 8, 20, 0);
        OverviewSpacerColumn.Width = compact ? new GridLength(0) : new GridLength(16);
        MainContentSpacerColumn.Width = new GridLength(0);
        MainContentSplitter.Visibility = Visibility.Collapsed;
        OverviewTabs.Visibility = Visibility.Collapsed;
        MainContentSecondaryRow.Height = compact
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;

        ControlsCard.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(0);
        WorkspaceSurface.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(0);
        FooterActionsPanel.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        SettingsSpacerColumn.Width = compact ? new GridLength(0) : new GridLength(12);
        AuditAlertsCard.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(0);
        ActivityRailSpacerColumn.Width = compact ? new GridLength(8) : new GridLength(10);
        ActivityRailColumn.Width = compact ? new GridLength(340) : new GridLength(400);

        if (compact)
        {
            Grid.SetRow(ReadinessCard, 0);
            Grid.SetColumn(ReadinessCard, 0);
            Grid.SetColumnSpan(ReadinessCard, 3);

            Grid.SetRow(ControlsCard, 1);
            Grid.SetColumn(ControlsCard, 0);
            Grid.SetColumnSpan(ControlsCard, 3);

            Grid.SetRow(WorkspaceSurface, 0);
            Grid.SetColumn(WorkspaceSurface, 0);
            Grid.SetColumnSpan(WorkspaceSurface, 3);

            Grid.SetRow(PatchDefaultsCard, 0);
            Grid.SetColumn(PatchDefaultsCard, 0);
            Grid.SetColumnSpan(PatchDefaultsCard, 3);

            Grid.SetRow(AuditAlertsCard, 1);
            Grid.SetColumn(AuditAlertsCard, 0);
            Grid.SetColumnSpan(AuditAlertsCard, 3);
        }
        else
        {
            Grid.SetRow(ReadinessCard, 0);
            Grid.SetColumn(ReadinessCard, 0);
            Grid.SetColumnSpan(ReadinessCard, 1);

            Grid.SetRow(ControlsCard, 0);
            Grid.SetColumn(ControlsCard, 2);
            Grid.SetColumnSpan(ControlsCard, 1);

            Grid.SetRow(WorkspaceSurface, 0);
            Grid.SetColumn(WorkspaceSurface, 0);
            Grid.SetColumnSpan(WorkspaceSurface, 3);

            Grid.SetRow(PatchDefaultsCard, 0);
            Grid.SetColumn(PatchDefaultsCard, 0);
            Grid.SetColumnSpan(PatchDefaultsCard, 1);

            Grid.SetRow(AuditAlertsCard, 0);
            Grid.SetColumn(AuditAlertsCard, 2);
            Grid.SetColumnSpan(AuditAlertsCard, 1);
        }
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) => _vm.OpenGitHubCommand.Execute(null);
    private void Docs_Click(object sender, RoutedEventArgs e) => _vm.OpenDocsCommand.Execute(null);

    private void LogOutput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ActivityRailLogScroller?.ScrollToBottom();
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        string selectedText = ActivityRailLogOutput.SelectedText;

        if (!string.IsNullOrEmpty(selectedText))
            Clipboard.SetText(selectedText);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        ActivityRailLogOutput.Focus();
        ActivityRailLogOutput.SelectAll();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.LogEntryCount > 0)
        {
            bool confirmed = ThemedDialog.Show(
                $"This clears {FormatCount(_vm.LogEntryCount, "visible activity entry")} from the current session.\n\nExport the log first if you need a saved audit trail.",
                "Clear Activity Log",
                DialogButtons.YesNo,
                DialogIcon.Warning,
                this) == "Yes";
            if (!confirmed)
                return;
        }

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

    private void UpdateThemeToggleButton()
    {
        bool dark = ThemeService.CurrentTheme is AppTheme.Dark or AppTheme.HighContrast;
        ThemeToggleButton.Content = "☀";
        ThemeToggleButton.ToolTip = dark ? "Switch to light theme" : "Switch to dark theme";
        System.Windows.Automation.AutomationProperties.SetName(
            ThemeToggleButton,
            dark ? "Switch to light theme" : "Switch to dark theme");
    }

    private void SyncThemeModeSelector()
    {
        if (ThemeModeSelector is null)
            return;

        _syncingThemeModeSelector = true;
        try
        {
            string target = ThemeService.CurrentMode.ToString();
            foreach (var item in ThemeModeSelector.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase))
                {
                    ThemeModeSelector.SelectedItem = item;
                    return;
                }
            }

            ThemeModeSelector.SelectedIndex = 0;
        }
        finally
        {
            _syncingThemeModeSelector = false;
        }
    }

    private async void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (!ReferenceEquals(e.Source, WorkspaceTabs))
                return;

            switch (WorkspaceTabs.SelectedIndex)
            {
                case 0:
                    RefreshBenchmarkWorkspace();
                    break;
                case 1:
                    await RefreshTelemetryWorkspaceAsync();
                    break;
                case 2:
                    _vm.RefreshRecoveryAssetsCommand.Execute(null);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WorkspaceTabs_SelectionChanged] {ex}");
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // BenchLabelText and StatusText fire often during preflight; only refresh the
        // benchmark chart panel when it's the visible tab. Saves the file read + chart relayout.
        if ((e.PropertyName == nameof(MainViewModel.BenchLabelText) ||
             e.PropertyName == nameof(MainViewModel.StatusText)) &&
            WorkspaceTabs.SelectedIndex == 0)
        {
            RefreshBenchmarkWorkspace();
        }

        if (e.PropertyName == nameof(MainViewModel.DriveInventorySummaryText) &&
            WorkspaceTabs.SelectedIndex == 1)
        {
            _ = RefreshTelemetryWorkspaceAsync();
        }
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
                FocusActivityRail();
                break;
            case "open_benchmarks":
                SelectWorkspaceTab(0);
                break;
            case "open_telemetry":
                SelectWorkspaceTab(1);
                break;
            case "open_recovery":
                SelectWorkspaceTab(2);
                break;
        }
    }

    private void SelectWorkspaceTab(int index)
    {
        if (WorkspaceTabs.SelectedIndex == index)
        {
            switch (index)
            {
                case 0:
                    RefreshBenchmarkWorkspace();
                    break;
                case 1:
                    _ = RefreshTelemetryWorkspaceAsync();
                    break;
                case 2:
                    _vm.RefreshRecoveryAssetsCommand.Execute(null);
                    break;
            }

            return;
        }

        WorkspaceTabs.SelectedIndex = index;
    }

    private void FocusActivityRail()
    {
        ActivityRailLogOutput.Focus();
        ActivityRailLogScroller.ScrollToBottom();
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
        try { await RefreshTelemetryWorkspaceAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RefreshTelemetry_Click] {ex}"); }
    }

    private async void TelemetryPanel_DriveSelected(int driveNumber)
    {
        try
        {
            _selectedTelemetryDriveNumber = driveNumber;
            await RefreshTelemetryDataAsync(driveNumber);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TelemetryPanel_DriveSelected] {ex}");
        }
    }

    private async Task RefreshTelemetryWorkspaceAsync()
    {
        int refreshId = Interlocked.Increment(ref _telemetryWorkspaceRefreshId);
        try
        {
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
        catch (Exception ex)
        {
            if (refreshId != _telemetryWorkspaceRefreshId)
                return;

            System.Diagnostics.Debug.WriteLine($"[RefreshTelemetryWorkspaceAsync] {ex}");
            _selectedTelemetryDriveNumber = null;
            TelemetryWorkspacePanel.Visibility = Visibility.Collapsed;
            TelemetryEmptyState.Visibility = Visibility.Visible;
            TelemetryPanelControl.Reset();
            TelemetryPanelControl.SetTelemetryStatus(
                "Telemetry refresh failed. Check the activity log or try again after Windows finishes enumerating storage devices.",
                "warning");
        }
    }

    private async Task RefreshTelemetryDataAsync(int driveNumber)
    {
        int requestId = Interlocked.Increment(ref _telemetryDataRefreshId);
        _selectedTelemetryDriveNumber = driveNumber;

        try
        {
            TelemetryPanelControl.SetTelemetryStatus(
                $"Polling Disk {driveNumber} for a fresh health snapshot and trend history.");

            var cachedHealthTask = Task.Run(() =>
            {
                try { return DriveService.GetNVMeHealthData(); }
                catch { return new Dictionary<string, NVMeHealthInfo>(); }
            });
            var liveDataTask = Task.Run(async () =>
            {
                try { return await NVMeTelemetryService.PollAsync(driveNumber); }
                catch { return null; }
            });

            // Use individual awaits so a faulted task in one stream doesn't surface as an
            // AggregateException via WhenAll; either side failing is non-fatal here.
            var cachedHealth = await cachedHealthTask;
            var liveData = await liveDataTask;

            if (requestId != _telemetryDataRefreshId)
                return;

            cachedHealth.TryGetValue(driveNumber.ToString(), out NVMeHealthInfo? fallbackHealth);
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
        catch (Exception ex)
        {
            if (requestId != _telemetryDataRefreshId)
                return;

            System.Diagnostics.Debug.WriteLine($"[RefreshTelemetryDataAsync] {ex}");
            TelemetryPanelControl.SetTelemetryStatus(
                $"Telemetry refresh failed for Disk {driveNumber}. Try again after Windows finishes enumerating the drive.",
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

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;

            current = GetVisualParent(current);
        }

        return null;
    }

    private static DependencyObject? GetVisualParent(DependencyObject current)
    {
        try
        {
            return System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryScrollViewer(ScrollViewer scroller, int wheelDelta)
    {
        if (wheelDelta == 0 || scroller.ScrollableHeight <= 0)
            return false;

        bool scrollingUp = wheelDelta > 0;
        if (scrollingUp && scroller.VerticalOffset <= 0)
            return false;

        if (!scrollingUp && scroller.VerticalOffset >= scroller.ScrollableHeight)
            return false;

        double lines = Math.Max(1, Math.Abs(wheelDelta) / 120.0);
        double offsetDelta = lines * 48 * (scrollingUp ? -1 : 1);
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset + offsetDelta);
        return true;
    }

    private static DialogIcon ResolveConfirmationIcon(string title)
    {
        return title.Contains("apply", StringComparison.OrdinalIgnoreCase)
            || title.Contains("remove", StringComparison.OrdinalIgnoreCase)
            || title.Contains("restart", StringComparison.OrdinalIgnoreCase)
            || title.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || title.Contains("fallback", StringComparison.OrdinalIgnoreCase)
            || title.Contains("inactive", StringComparison.OrdinalIgnoreCase)
                ? DialogIcon.Warning
                : DialogIcon.Question;
    }

    private static string FormatCount(int count, string singular)
    {
        return count == 1 ? $"1 {singular}" : $"{count} {singular}s";
    }
}
