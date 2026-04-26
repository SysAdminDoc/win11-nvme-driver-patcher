using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const string NoActivityYetText = "No activity in this session yet.";
    private const string RemoveUnavailableText = "Remove stays unavailable until a patch or partial patch is present.";
    private const string NoBackupHistoryText = "No registry backups are saved in the working folder yet.";
    private const string NoSnapshotHistoryText = "No change snapshots saved yet.";
    private const string NoBenchmarkHistoryText = "No benchmark runs saved yet.";
    private const string NoRecoveryKitText = "No recovery kit is ready yet. Generate one before a risky reboot or remote handoff.";
    private const string NoVerificationScriptText = "No verification script is ready yet. Generate one so post-reboot checks stay predictable.";
    private const string NoDiagnosticsReportText = "No diagnostics report is saved yet. Export one when you need a support-ready snapshot.";

    public AppConfig Config { get; }

    // Dialog delegates (set by MainWindow.xaml.cs)
    public Func<string, string, bool>? ConfirmDialog { get; set; }
    public Action<string, string, DialogIcon>? InfoDialog { get; set; }

    // Preflight state
    [ObservableProperty] private string _statusText = "Checking…";
    [ObservableProperty] private string _statusColor = "TextDim";
    [ObservableProperty] private string _driverLabelText = "";
    [ObservableProperty] private string _benchLabelText = "";
    [ObservableProperty] private bool _benchLabelVisible;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateVersionText = "";
    [ObservableProperty] private string _updateUrl = "";
    [ObservableProperty] private string _updateTooltip = "";
    [ObservableProperty] private bool _buttonsEnabled;
    [ObservableProperty] private bool _applyEnabled;
    [ObservableProperty] private bool _removeEnabled;
    [ObservableProperty] private string _applyButtonText = "Apply Patch";
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _progressVisible;
    [ObservableProperty] private bool _settingsPanelVisible;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private int _logEntryCount;
    [ObservableProperty] private int _logSuccessCount;
    [ObservableProperty] private int _logWarningCount;
    [ObservableProperty] private int _logErrorCount;
    [ObservableProperty] private string _activitySummaryText = "Activity entries will appear here as checks and actions run.";
    [ObservableProperty] private string _logRetentionText = "Local log | Auto-save on close | Event Log on";
    [ObservableProperty] private string _latestActivityText = NoActivityYetText;
    [ObservableProperty] private string _activityTabBadgeText = "Idle";
    [ObservableProperty] private string _activityTabBadgeColor = "TextDim";
    [ObservableProperty] private string _benchmarkTabBadgeText = "New";
    [ObservableProperty] private string _benchmarkTabBadgeColor = "TextDim";
    [ObservableProperty] private string _telemetryTabBadgeText = "Waiting";
    [ObservableProperty] private string _telemetryTabBadgeColor = "TextDim";
    [ObservableProperty] private string _recoveryTabBadgeText = "3 missing";
    [ObservableProperty] private string _recoveryTabBadgeColor = "Yellow";
    [ObservableProperty] private int _benchmarkRunCount;
    [ObservableProperty] private int _recoveryMissingAssetCount = 3;
    [ObservableProperty] private string _statusSummaryText = "Checking build support, storage layout, and rollback safety.";
    [ObservableProperty] private string _buildSummaryText = "Windows build check pending";
    [ObservableProperty] private string _driveInventorySummaryText = "Scanning local drives";
    [ObservableProperty] private string _riskSummaryText = "Risk summary pending";
    [ObservableProperty] private string _riskSummaryColor = "Accent";
    [ObservableProperty] private string _optionsSummaryText = "Safe defaults keep confirmations, rollback, and audit helpers on.";
    [ObservableProperty] private string _preferenceSummaryText = "Theme, alerts, audit trail, and restart delay.";
    [ObservableProperty] private string _themeModeSummaryText = "Follows Windows. Current effective theme: dark.";
    [ObservableProperty] private string _attentionSummaryText = "Important compatibility notes will surface here after the readiness scan completes.";
    [ObservableProperty] private bool _hasAttentionNotes;
    [ObservableProperty] private string _changePlanSummaryText = "The machine-specific change plan will appear here after readiness checks finish.";
    [ObservableProperty] private bool _hasChangePlanSteps;
    [ObservableProperty] private string _actionReadinessText = "Readiness checks will explain when apply or remove becomes available.";
    [ObservableProperty] private string _actionReadinessColor = "TextMuted";
    [ObservableProperty] private string _applyButtonTooltipText = "Readiness checks are still running.";
    [ObservableProperty] private string _removeButtonTooltipText = RemoveUnavailableText;
    [ObservableProperty] private string _nextStepTitle = "Running readiness checks";
    [ObservableProperty] private string _nextStepDescription = "Driver changes stay locked until Windows build support, drive visibility, and rollback safety are confirmed.";
    [ObservableProperty] private string _nextStepColor = "Accent";
    [ObservableProperty] private bool _hasNextStepPrimaryAction;
    [ObservableProperty] private string _nextStepPrimaryActionText = "";
    [ObservableProperty] private string _nextStepPrimaryActionId = "";
    [ObservableProperty] private bool _nextStepPrimaryActionEnabled;
    [ObservableProperty] private bool _hasNextStepSecondaryAction;
    [ObservableProperty] private string _nextStepSecondaryActionText = "";
    [ObservableProperty] private string _nextStepSecondaryActionId = "";
    [ObservableProperty] private bool _nextStepSecondaryActionEnabled;
    [ObservableProperty] private string _backupHistoryText = NoBackupHistoryText;
    [ObservableProperty] private string _snapshotHistoryText = NoSnapshotHistoryText;
    [ObservableProperty] private string _benchmarkHistoryText = NoBenchmarkHistoryText;
    [ObservableProperty] private string _recoveryKitStatusText = NoRecoveryKitText;
    [ObservableProperty] private string _verificationScriptStatusText = NoVerificationScriptText;
    [ObservableProperty] private string _diagnosticsReportStatusText = NoDiagnosticsReportText;
    [ObservableProperty] private string _recoveryWorkspaceSummaryText = "Generate rollback, verification, and diagnostics assets so the system can be reversed or confirmed without guesswork.";
    [ObservableProperty] private bool _hasRecoveryKit;
    [ObservableProperty] private bool _hasVerificationScript;
    [ObservableProperty] private bool _hasDiagnosticsReport;
    [ObservableProperty] private bool _hasBackupFiles;
    [ObservableProperty] private bool _hasBenchmarkHistory;
    [ObservableProperty] private string _preparationStageStateText = "Review readiness and capture a baseline";
    [ObservableProperty] private string _preparationStageDetailText = "Readiness checks, backups, and optional benchmarks make the driver change easier to trust.";
    [ObservableProperty] private string _preparationStageColor = "Accent";
    [ObservableProperty] private string _restartStageStateText = "Patch not staged yet";
    [ObservableProperty] private string _restartStageDetailText = "Once the patch is applied, this phase will tell you when a reboot is actually required.";
    [ObservableProperty] private string _restartStageColor = "TextDim";
    [ObservableProperty] private string _validationStageStateText = "Validation comes after the driver changes";
    [ObservableProperty] private string _validationStageDetailText = "Use benchmarks, telemetry, and diagnostics after reboot to confirm the migration on this exact machine.";
    [ObservableProperty] private string _validationStageColor = "TextDim";
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _nvmeDriveCount;
    [ObservableProperty] private int _totalDriveCount;
    [ObservableProperty] private bool _hasDriveData;

    // Settings bindings
    [ObservableProperty] private bool _includeServerKey;
    [ObservableProperty] private bool _skipWarnings;
    [ObservableProperty] private bool _autoSaveLog;
    [ObservableProperty] private bool _enableToasts;
    [ObservableProperty] private bool _writeEventLog;
    [ObservableProperty] private string _restartDelayText = "30";
    [ObservableProperty] private bool _isSafeModeSelected = true;
    [ObservableProperty] private bool _isFullModeSelected;
    [ObservableProperty] private string _patchProfileHelpText =
        "Safe Mode writes only 735209102 — enough to swap the driver with no boot-crash reports tied to it.";

    // Lit when post-reboot verification detects that the override was blocked. Surfaces a
    // persistent "Try ViVeTool Fallback" affordance on the Overview card so the user can
    // revisit the choice without reopening the dialog.
    [ObservableProperty] private bool _showViVeToolFallbackBadge;

    // Set while a benchmark is running, used by the XAML Cancel button to show/hide and
    // to gate CancelBenchmarkCommand. Mirrors _benchmarkInFlight but as a bindable property.
    [ObservableProperty] private bool _benchmarkRunning;

    // Source used to cancel the active benchmark run. Non-null only while a benchmark is
    // in flight; disposed and nulled out in the finally block of RunBenchmark.
    private System.Threading.CancellationTokenSource? _benchmarkCts;

    // UI collections
    public ObservableCollection<PreflightCheckVM> ReadinessChecks { get; } = [];
    public ObservableCollection<PreflightCheckVM> LeftChecks { get; } = [];
    public ObservableCollection<PreflightCheckVM> RightChecks { get; } = [];
    public ObservableCollection<DriveRowVM> Drives { get; } = [];
    public ObservableCollection<RegistryFlagVM> RegistryFlags { get; } = [];
    public ObservableCollection<RegistryFlagVM> SafeBootFlags { get; } = [];
    public ObservableCollection<AttentionNoteVM> AttentionNotes { get; } = [];
    public ObservableCollection<ChangePlanStepVM> ChangePlanSteps { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];

    public string LogText => string.Join("\n", LogEntries);

    private PreflightResult? _preflight;
    private readonly List<string> _logHistory = [];
    // Guards _logHistory against torn writes when Log() is called from background threads
    // (PatchService.Install runs in Task.Run). Without this, concurrent Add() calls can
    // corrupt the underlying array and crash later reads with IndexOutOfRangeException.
    private readonly object _logHistoryLock = new();
    private bool _hasLoggedSessionStart;

    // Set while the ctor is priming view-bound properties from config, so the change partials
    // don't immediately save config back out (triggering a pointless write on every startup).
    private bool _suppressConfigWrites;

    public MainViewModel()
    {
        Config = ConfigService.Load();
        // GPO overlay wins over per-user config — a pinned fleet policy shouldn't be
        // quietly overridden by a stale local setting.
        try { GpoPolicyService.ApplyTo(Config, GpoPolicyService.Read()); } catch { }
        VersionText = $"v{AppConfig.AppVersion}";
        _suppressConfigWrites = true;
        try
        {
            IncludeServerKey = Config.IncludeServerKey;
            SkipWarnings = Config.SkipWarnings;
            AutoSaveLog = Config.AutoSaveLog;
            EnableToasts = Config.EnableToasts;
            WriteEventLog = Config.WriteEventLog;
            RestartDelayText = Config.RestartDelay.ToString();
            IsSafeModeSelected = Config.PatchProfile == PatchProfile.Safe;
            IsFullModeSelected = Config.PatchProfile == PatchProfile.Full;
            Config.ThemeMode = ThemeService.NormalizeMode(Config.ThemeMode);
            ThemeService.ApplyMode(Config.ThemeMode);
            ThemeModeSummaryText = ThemeService.GetModeDescription(Config.ThemeMode);
        }
        finally
        {
            _suppressConfigWrites = false;
        }
        RefreshPatchProfileHelpText();
        UpdateOptionsSummary();
        UpdatePreferenceSummary();
        UpdateOperationalHistory();
        UpdateActivitySummary();
        UpdateWorkspaceBadges();
    }

    partial void OnIsSafeModeSelectedChanged(bool value)
    {
        if (!value) return;
        IsFullModeSelected = false;
        Config.PatchProfile = PatchProfile.Safe;
        if (!_suppressConfigWrites) { try { ConfigService.Save(Config); } catch { } }
        RefreshPatchProfileHelpText();
        UpdateOptionsSummary();
    }

    partial void OnIsFullModeSelectedChanged(bool value)
    {
        if (!value) return;
        IsSafeModeSelected = false;
        Config.PatchProfile = PatchProfile.Full;
        if (!_suppressConfigWrites) { try { ConfigService.Save(Config); } catch { } }
        RefreshPatchProfileHelpText();
        UpdateOptionsSummary();
    }

    private void RefreshPatchProfileHelpText()
    {
        PatchProfileHelpText = Config.PatchProfile == PatchProfile.Safe
            ? "Safe Mode writes only 735209102 — enough to swap the driver with no boot-crash reports tied to it. This is what you want on a daily-driver machine."
            : "Full Mode adds 1853569164 (UxAccOptimization) and 156965516 (Standalone_Future). Higher peak performance on some drives; community boot-crash reports cluster on these two flags. Try Safe Mode first — you can always opt in later.";
    }

    public void Log(string message, string level = "INFO")
    {
        if (message is null) message = string.Empty;
        if (level is null) level = "INFO";

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"[{timestamp}] [{level}] {message}";

        lock (_logHistoryLock)
        {
            _logHistory.Add(entry);
        }

        try
        {
            var app = Application.Current;
            if (app is null) return;
            if (app.Dispatcher.CheckAccess())
            {
                AppendLogEntry(entry, message, level);
            }
            else
            {
                app.Dispatcher.BeginInvoke(() =>
                {
                    AppendLogEntry(entry, message, level);
                });
            }
        }
        catch { /* Dispatcher gone during shutdown */ }
    }

    public void ClearLog()
    {
        LogEntries.Clear();
        lock (_logHistoryLock) { _logHistory.Clear(); }
        LogEntryCount = 0;
        LogSuccessCount = 0;
        LogWarningCount = 0;
        LogErrorCount = 0;
        LatestActivityText = "Session activity cleared. New events will appear here.";
        UpdateActivitySummary();
        OnPropertyChanged(nameof(LogText));
    }

    // Thread-safe snapshot for callers that need to enumerate the full audit trail
    // (export, autosave-on-close, diagnostics report).
    private List<string> SnapshotLogHistory()
    {
        lock (_logHistoryLock) { return new List<string>(_logHistory); }
    }

    public async Task RunPreflightAsync()
    {
        ResetOverviewState();
        IsLoading = true;
        ButtonsEnabled = false;
        EventLogService.Initialize(Config.WriteEventLog);
        if (!_hasLoggedSessionStart)
        {
            Log($"{AppConfig.AppName} v{AppConfig.AppVersion} started");
            Log($"Working directory: {Config.WorkingDir}");
            Log("----------------------------------------");
            EventLogService.Write($"{AppConfig.AppName} v{AppConfig.AppVersion} started");
            _hasLoggedSessionStart = true;
        }
        else
        {
            Log("----------------------------------------");
        }
        Log("Running pre-flight checks...");

        // Post-reboot verification: evaluate now so the log captures the state at startup,
        // but defer any dialog until AFTER preflight has rendered. A modal dialog popping up
        // over a half-rendered UI was jarring AND it blocked the main window from showing
        // drive data / status cards until the user answered.
        VerificationReport? pendingVerification = null;
        try
        {
            pendingVerification = PatchVerificationService.Evaluate(Config);
            switch (pendingVerification.Outcome)
            {
                case VerificationOutcome.Confirmed:
                    Log($"[OK] Post-reboot verification: {pendingVerification.Detail}", "SUCCESS");
                    ToastService.Show("NVMe Driver Active",
                        "Native NVMe driver is bound after the reboot.",
                        ToastType.Success, Config.EnableToasts);
                    PatchVerificationService.Clear(Config, pendingVerification);
                    ConfigService.Save(Config);
                    pendingVerification = null;
                    break;
                case VerificationOutcome.OverrideBlocked:
                    Log("[WARNING] " + pendingVerification.Summary, "WARNING");
                    Log(pendingVerification.Detail, "WARNING");
                    EventLogService.Write(pendingVerification.Summary + " — " + pendingVerification.Detail,
                        System.Diagnostics.EventLogEntryType.Warning, 2101);
                    ToastService.Show("Patch Inactive",
                        "Registry keys are set but Windows is still on the legacy driver. See the Activity log.",
                        ToastType.Warning, Config.EnableToasts);
                    PatchVerificationService.Clear(Config, pendingVerification);
                    ShowViVeToolFallbackBadge = true;
                    ConfigService.Save(Config);
                    // Dialog itself is deferred — see HandlePendingVerificationDialogAsync below.
                    break;
                case VerificationOutcome.Reverted:
                    Log("Post-reboot verification: previous patch is no longer present.", "INFO");
                    PatchVerificationService.Clear(Config, pendingVerification);
                    ConfigService.Save(Config);
                    pendingVerification = null;
                    break;
                case VerificationOutcome.AwaitingRestart:
                    Log("Patch is applied — restart to complete activation.", "INFO");
                    pendingVerification = null;
                    break;
                case VerificationOutcome.StalePending:
                    Log($"Patch verification state is being cleared: {pendingVerification.Detail}", "DEBUG");
                    PatchVerificationService.Clear(Config, pendingVerification);
                    ConfigService.Save(Config);
                    pendingVerification = null;
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never let verification failures block the rest of preflight.
            Log($"Post-reboot verification skipped: {ex.Message}", "DEBUG");
            pendingVerification = null;
        }

        try
        {
            _preflight = await PreflightService.RunAllAsync(msg => Log(msg, "DEBUG"));
        }
        catch (Exception ex)
        {
            // RunAllAsync is supposed to never throw because all check exceptions are wrapped,
            // but a fatal WMI/COM teardown can still escape. Keep the UI usable instead of
            // sticking on "Loading..." forever.
            Log($"Preflight failed catastrophically: {ex.Message}", "ERROR");
            _preflight = new PreflightResult();
        }

        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
            // Map checks to UI
            ReadinessChecks.Clear();
            LeftChecks.Clear();
            RightChecks.Clear();

            var leftMap = new[] { "WindowsVersion", "NVMeDrives", "BitLocker", "VeraCrypt", "LaptopPower", "DriverStatus" };
            var leftLabels = new[] { "Build", "NVMe", "BitLocker", "VeraCrypt", "Power", "Driver" };
            var rightMap = new[] { "ThirdPartyDriver", "Compatibility", "SystemProtection", "BypassIO" };
            var rightLabels = new[] { "3rd Party", "Compat", "System", "BypassIO" };

            for (int i = 0; i < leftMap.Length; i++)
            {
                if (_preflight.Checks.TryGetValue(leftMap[i], out var check))
                {
                    var vm = new PreflightCheckVM { Label = leftLabels[i], Status = check.Status, Message = check.Message };
                    // Detail tooltips for specific checks
                    if (leftMap[i] == "VeraCrypt" && _preflight.VeraCryptDetected)
                        vm.Tooltip = "VeraCrypt system encryption breaks nvmedisk.sys boot entirely.\nThis is a hard block that cannot be overridden.";
                    else if (leftMap[i] == "BitLocker" && _preflight.BitLockerEnabled)
                        vm.Tooltip = "BitLocker will be automatically suspended for one reboot\nto prevent recovery key prompts.";
                    else if (leftMap[i] == "LaptopPower" && _preflight.IsLaptop)
                        vm.Tooltip = "Native NVMe breaks APST power management.\nExpect ~15% battery life reduction and higher idle SSD temps.";
                    else if (leftMap[i] == "WindowsVersion" && _preflight.BuildDetails is not null)
                        vm.Tooltip = $"{_preflight.BuildDetails.Caption}\nBuild {_preflight.BuildDetails.BuildNumber}.{_preflight.BuildDetails.UBR}";
                    ReadinessChecks.Add(vm);
                    LeftChecks.Add(vm);
                }
            }
            for (int i = 0; i < rightMap.Length; i++)
            {
                if (_preflight.Checks.TryGetValue(rightMap[i], out var check))
                {
                    var vm = new PreflightCheckVM { Label = rightLabels[i], Status = check.Status, Message = check.Message };
                    // Compat tooltip with full software details
                    if (rightMap[i] == "Compatibility" && _preflight.IncompatibleSoftware.Count > 0)
                        vm.Tooltip = string.Join("\n", _preflight.IncompatibleSoftware.Select(s => $"[{s.Severity}] {s.Name}: {s.Message}"));
                    ReadinessChecks.Add(vm);
                    RightChecks.Add(vm);
                }
            }

            // Log all results
            foreach (var (name, check) in _preflight.Checks)
            {
                var level = check.Status switch
                {
                    CheckStatus.Pass => "SUCCESS",
                    CheckStatus.Warning => "WARNING",
                    CheckStatus.Fail => "ERROR",
                    _ => "INFO"
                };
                Log($"  [{name}] {check.Message}", level);
            }

            // Log incompatible software details
            foreach (var sw in _preflight.IncompatibleSoftware)
            {
                var swLevel = sw.Severity == "Critical" ? "ERROR" : "WARNING";
                Log($"  [Compat] {sw.Name} ({sw.Severity}): {sw.Message}", swLevel);
            }

            // Log firmware
            if (_preflight.DriverInfo?.FirmwareVersions.Count > 0)
            {
                foreach (var (diskId, fw) in _preflight.DriverInfo.FirmwareVersions)
                    Log($"  [Firmware] Disk {diskId}: {fw}");
            }

            if (_preflight.BypassIOStatus?.Warning is { Length: > 0 } warning)
                Log($"  [BypassIO] {warning}", "WARNING");

            if (_preflight.BuildDetails is not null)
                Log($"  [Build] {_preflight.BuildDetails.DisplayVersion} (Build {_preflight.BuildDetails.BuildNumber}.{_preflight.BuildDetails.UBR})");

            // Drives, registry, status
            UpdateDrivesList();
            UpdateRegistryDisplay();
            UpdateStatusDisplay();
            UpdateAttentionNotes();
            UpdateOverviewSummary();
            UpdateOperationalHistory();

            // Update badge. PreflightService now runs the update check fire-and-forget so it
            // doesn't hold up the first render; pick up a synchronous result if it already
            // finished, otherwise schedule a late refresh.
            if (_preflight.UpdateAvailable is not null)
            {
                ApplyUpdateBadge(_preflight.UpdateAvailable);
            }
            else if (_preflight.UpdateCheckTask is { } updateTask)
            {
                ObserveLateUpdateCheck(updateTask);
            }

            // Benchmark label
            BenchLabelText = "";
            BenchLabelVisible = false;
            var history = BenchmarkService.GetHistory(Config.WorkingDir);
            if (history.Count > 0)
            {
                var last = history[^1];
                if (last.Read.IOPS > 0)
                {
                    BenchLabelText = $"Last bench: {last.Read.IOPS} IOPS read / {last.Write.IOPS} IOPS write ({last.Label})";
                    BenchLabelVisible = true;
                }
            }

            // Post-reboot check
            CheckPostReboot();

            Log("----------------------------------------");
            Log("Ready. Select an action above.");

            IsLoading = false;
            ButtonsEnabled = true;
            ApplyEnabled = PreflightService.AllCriticalPassed(_preflight.Checks) && !_preflight.VeraCryptDetected;
            });
        }
        catch (Exception ex)
        {
            // Recover the UI even if any of the post-preflight rendering blew up.
            Log($"Failed to render preflight results: {ex.Message}", "ERROR");
            try
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsLoading = false;
                    ButtonsEnabled = true;
                });
            }
            catch { }
        }

        // Dialog AFTER preflight render so the user sees the drive/status cards first rather
        // than a modal popping up over an empty-looking window. Scheduled via BeginInvoke at
        // Background priority so rendering has a frame to commit.
        if (pendingVerification is { Outcome: VerificationOutcome.OverrideBlocked })
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => _ = HandlePendingVerificationDialogAsync()));
            }
            catch { /* Dispatcher gone during shutdown */ }
        }
    }

    // Populates the "update available" badge + toast. Extracted so the initial preflight
    // render and the late-arriving background update check can both reuse it.
    private void ApplyUpdateBadge(UpdateInfo info)
    {
        UpdateAvailable = true;
        UpdateVersionText = $"v{info.Version}";
        UpdateUrl = info.URL;
        UpdateTooltip = $"Click to download v{info.Version}";
        Log($"UPDATE AVAILABLE: v{info.Version} -- {AppConfig.GitHubURL}/releases", "WARNING");
    }

    // Called when preflight kicked off the update check but it hadn't replied by the time
    // the first render happened. We wait up to 12 seconds on a background continuation — if
    // a result arrives, marshal back to the UI thread and raise the badge; otherwise stay
    // quiet (user is still free to click "Check for updates" from the menu).
    private void ObserveLateUpdateCheck(Task<UpdateInfo?> updateTask)
    {
        _ = updateTask.ContinueWith(async completed =>
        {
            try
            {
                // Task.Delay-based soft cap: the underlying HttpClient already has its own
                // timeout, but defense-in-depth keeps us from leaking a long-lived observer.
                var finished = await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(12)));
                if (finished is not Task<UpdateInfo?> t || !t.IsCompletedSuccessfully) return;
                var info = t.Result;
                if (info is null) return;

                var app = System.Windows.Application.Current;
                if (app is null) return;
                await app.Dispatcher.InvokeAsync(() =>
                {
                    if (_preflight is not null) _preflight.UpdateAvailable = info;
                    ApplyUpdateBadge(info);
                });
            }
            catch { /* best-effort — don't crash the UI over a missing update badge */ }
        }, TaskScheduler.Default);
    }

    // Split out from RunPreflightAsync so the async flow has a real Task to attach exception
    // handling to — previously the `new Action(async () => …)` lambda inside BeginInvoke was
    // an async-void in disguise and would silently swallow any failure in the fallback path.
    private async Task HandlePendingVerificationDialogAsync()
    {
        try
        {
            bool tryFallback = ConfirmDialog?.Invoke(
                "Patch Applied But Inactive",
                "The registry changes are in place, but Windows is still loading the legacy stornvme.sys driver.\n\n" +
                "Microsoft began blocking this override path on recent Insider builds in early 2026. " +
                "On those builds the FeatureManagement\\Overrides route is a no-op.\n\n" +
                "A community fallback exists: ViVeTool writes to a different feature store using IDs 60786016 and 48433719. " +
                "This works on post-block builds at the cost of an extra dependency.\n\n" +
                "Choose Apply Fallback to download ViVeTool from its official GitHub repository " +
                $"({ViVeToolService.ViVeToolProjectUrl}) and apply the fallback now.\n\n" +
                "If you choose Not Now, your registry backup, restore point, and recovery kit stay in place. " +
                "You can remove the patch from this app at any time."
            ) == true;
            if (tryFallback)
                await ApplyViVeToolFallback();
        }
        catch (Exception ex)
        {
            Log($"ViVeTool fallback dialog flow failed: {ex.Message}", "ERROR");
            try
            {
                EventLogService.Write(
                    $"ViVeTool fallback dialog flow failed: {ex}",
                    System.Diagnostics.EventLogEntryType.Error,
                    3101);
            }
            catch { }
        }
    }

    private void UpdateDrivesList()
    {
        if (_preflight is null) return;
        Drives.Clear();
        foreach (var drv in _preflight.CachedDrives)
        {
            var vm = new DriveRowVM
            {
                Name = drv.Name, Size = drv.Size, BusType = drv.BusType,
                IsNVMe = drv.IsNVMe, IsBoot = drv.IsBoot
            };

            if (drv.IsNVMe && _preflight.CachedHealth.TryGetValue(drv.Number.ToString(), out var health))
            {
                vm.Temperature = health.Temperature;
                vm.Wear = health.Wear;
                vm.SmartTooltip = health.SmartTooltip;
            }

            // Firmware version
            if (drv.IsNVMe && _preflight.DriverInfo?.FirmwareVersions.TryGetValue(drv.Number.ToString(), out var fw) == true)
                vm.Firmware = fw;

            if (drv.IsNVMe && _preflight.NativeNVMeStatus?.IsActive == true)
            {
                bool isNative = _preflight.NativeNVMeStatus.StorageDisks
                    .Any(sd => drv.Name.Split(' ').FirstOrDefault() is string first && sd.Contains(first, StringComparison.OrdinalIgnoreCase));
                if (!isNative && _preflight.CachedMigration is not null)
                    isNative = _preflight.CachedMigration.Migrated
                        .Any(md => drv.Name.Split(' ').FirstOrDefault() is string first && md.Contains(first, StringComparison.OrdinalIgnoreCase));
                vm.IsNativeDrive = isNative;
                vm.ShowDriverBadge = true;
            }

            Drives.Add(vm);
        }
    }

    private void UpdateRegistryDisplay()
    {
        RegistryFlags.Clear();
        SafeBootFlags.Clear();

        var status = RegistryService.GetPatchStatus();

        foreach (var id in AppConfig.FeatureIDs)
        {
            bool isSet = status.Keys.Contains(id);
            string name = AppConfig.FeatureNames.TryGetValue(id, out var fn) ? fn.Split('(')[0].Trim() : "Unknown";
            RegistryFlags.Add(new RegistryFlagVM { Id = id, Name = name, IsSet = isSet });
        }

        // Server key
        bool serverSet = RegistryService.IsServerKeyApplied();
        RegistryFlags.Add(new RegistryFlagVM
        {
            Id = AppConfig.ServerFeatureID, Name = "Server 2025 (optional)",
            IsSet = serverSet, IsOptional = !Config.IncludeServerKey && !serverSet
        });

        SafeBootFlags.Add(new RegistryFlagVM
        {
            Id = "SafeBoot", Name = "Minimal — boot protection",
            IsSet = status.Keys.Contains("SafeBootMinimal")
        });
        SafeBootFlags.Add(new RegistryFlagVM
        {
            Id = "SafeBoot/Net", Name = "Network — Safe Mode with Networking",
            IsSet = status.Keys.Contains("SafeBootNetwork")
        });
    }

    private void UpdateStatusDisplay()
    {
        var status = RegistryService.GetPatchStatus();

        if (status.Applied)
        {
            StatusText = "Patch applied";
            StatusColor = "Green";
            ApplyButtonText = "Reinstall Patch";
            RemoveEnabled = true;
        }
        else if (status.Partial)
        {
            StatusText = $"Patch incomplete ({status.Count}/{status.Total})";
            StatusColor = "Yellow";
            ApplyButtonText = "Repair Patch";
            RemoveEnabled = true;
        }
        else
        {
            StatusText = "Not applied";
            StatusColor = "TextDim";
            ApplyButtonText = "Apply Patch";
            RemoveEnabled = false;
        }

        if (_preflight?.NativeNVMeStatus?.IsActive == true)
            DriverLabelText = "Active: nvmedisk.sys (Native NVMe)";
        else if (_preflight?.DriverInfo is not null)
            DriverLabelText = $"Current driver: {_preflight.DriverInfo.CurrentDriver}";
    }

    private void UpdateOverviewSummary()
    {
        if (_preflight is null)
        {
            ResetOverviewState();
            return;
        }

        bool hasBenchmarkHistory = BenchmarkService.GetHistory(Config.WorkingDir).Count > 0;
        var status = RegistryService.GetPatchStatus();

        WarningCount = _preflight.Checks.Values.Count(c => c.Status == CheckStatus.Warning);
        CriticalCount = _preflight.Checks.Values.Count(c => c.Critical && c.Status == CheckStatus.Fail);
        TotalDriveCount = _preflight.CachedDrives.Count;
        NvmeDriveCount = _preflight.CachedDrives.Count(d => d.IsNVMe);
        HasDriveData = TotalDriveCount > 0;

        if (_preflight.BuildDetails is { } build)
            BuildSummaryText = $"Win 11 {build.DisplayVersion} | {build.BuildNumber}.{build.UBR}";
        else
            BuildSummaryText = "Windows build details unavailable";

        if (TotalDriveCount == 0)
            DriveInventorySummaryText = "No drives found";
        else if (NvmeDriveCount == 0)
            DriveInventorySummaryText = $"0 NVMe / {TotalDriveCount} {Pluralize(TotalDriveCount, "device")}";
        else
            DriveInventorySummaryText = $"{NvmeDriveCount} NVMe / {TotalDriveCount} {Pluralize(TotalDriveCount, "device")}";

        if (CriticalCount > 0)
        {
            RiskSummaryText = $"{CriticalCount} {Pluralize(CriticalCount, "blocker")}";
            RiskSummaryColor = "Red";
        }
        else if (WarningCount > 0)
        {
            RiskSummaryText = $"{WarningCount} advisory {Pluralize(WarningCount, "note")}";
            RiskSummaryColor = "Yellow";
        }
        else
        {
            RiskSummaryText = "Clear";
            RiskSummaryColor = "Green";
        }

        if (_preflight.NativeNVMeStatus?.IsActive == true)
        {
            StatusSummaryText = "Native NVMe active. Validate with benchmark, telemetry, or tuning.";
        }
        else
        {
            if (status.Applied)
                StatusSummaryText = "The patch is configured, but Windows is still on the legacy path until after the next reboot.";
            else if (CriticalCount > 0)
                StatusSummaryText = "A critical safeguard failed. Resolve the blocking item before applying any driver changes.";
            else if (WarningCount > 0)
                StatusSummaryText = "This system can proceed, but there are caveats worth reviewing before you commit.";
            else
                StatusSummaryText = "This system looks ready for a controlled apply with backup, notices, and recovery helpers in place.";
        }

        if (CriticalCount > 0)
        {
            NextStepTitle = "Resolve blockers";
            NextStepDescription = "Open readiness details, fix the critical items, then refresh checks.";
            NextStepColor = "Red";
        }
        else if (_preflight.NativeNVMeStatus?.IsActive == true)
        {
            if (hasBenchmarkHistory)
            {
                NextStepTitle = "Validate and document the outcome";
                NextStepDescription = "Review benchmark, thermal telemetry, then export diagnostics for a clean record.";
                NextStepColor = "Green";
            }
            else
            {
                NextStepTitle = "Capture a validation benchmark";
                NextStepDescription = "The native driver is already live. Run a benchmark now so this machine has before-and-after style evidence instead of relying on generic expectations.";
                NextStepColor = "Green";
            }
        }
        else if (status.Applied)
        {
            NextStepTitle = "Restart to finish the migration";
            NextStepDescription = "Windows has the patch staged, but the live driver path will not change until after the next reboot. Restart when ready, then use the Recovery workspace to review the verification script or rollback kit before you walk away.";
            NextStepColor = "Yellow";
        }
        else if (WarningCount > 0)
        {
            NextStepTitle = "Review the tradeoffs, then decide";
            NextStepDescription = hasBenchmarkHistory
                ? "You already have baseline data. Read the advisory notes, refresh the backup or recovery assets if needed, and apply only if the compatibility and power tradeoffs are acceptable."
                : "Read the advisory notes, capture a baseline benchmark if you want a comparison, and prepare backup or recovery assets before you apply.";
            NextStepColor = "Yellow";
        }
        else if (hasBenchmarkHistory)
        {
            NextStepTitle = "Apply when you are comfortable";
            NextStepDescription = "Your checks are clear and you already have baseline data. Refresh the backup or recovery kit, apply the patch, and plan for a restart to activate it.";
            NextStepColor = "Accent";
        }
        else
        {
            NextStepTitle = "Create a baseline before changing drivers";
            NextStepDescription = "Take a registry backup, optionally export the recovery kit and run a pre-patch benchmark, then apply once the current state is documented and easy to roll back.";
            NextStepColor = "Accent";
        }

        // Pass the already-fetched status to each sub-method so they don't each open their
        // own registry handle — avoids 4+ redundant reads and the TOCTOU window they create.
        UpdateActionGuidance(status);
        UpdateChangePlan(status);
        UpdateWorkflowGuide(status);
        UpdateRecommendedActions(status);
        UpdateWorkspaceBadges();
    }

    private void ResetOverviewState()
    {
        StatusText = "Checking…";
        StatusColor = "TextDim";
        StatusSummaryText = "Scanning your system build, storage layout, and rollback safety.";
        BuildSummaryText = "Windows build check pending";
        DriveInventorySummaryText = "Scanning local drives";
        RiskSummaryText = "Risk summary pending";
        RiskSummaryColor = "Accent";
        NextStepTitle = "Running readiness checks";
        NextStepDescription = "Driver changes stay locked until Windows build support, drive visibility, and rollback safety are confirmed.";
        NextStepColor = "Accent";
        WarningCount = 0;
        CriticalCount = 0;
        NvmeDriveCount = 0;
        TotalDriveCount = 0;
        HasDriveData = false;
        HasNextStepPrimaryAction = false;
        NextStepPrimaryActionText = "";
        NextStepPrimaryActionId = "";
        NextStepPrimaryActionEnabled = false;
        HasNextStepSecondaryAction = false;
        NextStepSecondaryActionText = "";
        NextStepSecondaryActionId = "";
        NextStepSecondaryActionEnabled = false;
        AttentionNotes.Clear();
        HasAttentionNotes = false;
        AttentionSummaryText = "Important compatibility notes will surface here after the readiness scan completes.";
        ChangePlanSteps.Clear();
        HasChangePlanSteps = false;
        ChangePlanSummaryText = "The machine-specific change plan will appear here after readiness checks finish.";
        ActionReadinessText = "Readiness checks will explain when apply or remove becomes available.";
        ActionReadinessColor = "TextMuted";
        ApplyButtonTooltipText = "Readiness checks are still running.";
        RemoveButtonTooltipText = RemoveUnavailableText;
        UpdateWorkspaceBadges();
    }

    private void UpdateAttentionNotes()
    {
        AttentionNotes.Clear();

        if (_preflight is null)
        {
            HasAttentionNotes = false;
            AttentionSummaryText = "Important compatibility notes will surface here after the readiness scan completes.";
            return;
        }

        if (_preflight.VeraCryptDetected)
        {
            AttentionNotes.Add(new AttentionNoteVM
            {
                Title = "VeraCrypt system encryption is a hard stop",
                Detail = "The native NVMe path breaks VeraCrypt system-encrypted boot. This machine should not be patched unless that configuration changes.",
                ToneColor = "Red"
            });
        }

        if (_preflight.BitLockerEnabled)
        {
            AttentionNotes.Add(new AttentionNoteVM
            {
                Title = "BitLocker will be suspended for one reboot",
                Detail = "That avoids an unexpected recovery-key prompt during the migration, then Windows resumes normal protection after restart.",
                ToneColor = "Yellow"
            });
        }

        if (_preflight.IsLaptop)
        {
            AttentionNotes.Add(new AttentionNoteVM
            {
                Title = "Laptop power behavior may change",
                Detail = "Native NVMe can reduce APST power savings on mobile systems, which usually means higher idle SSD temperature and shorter battery life.",
                ToneColor = "Yellow"
            });
        }

        if (_preflight.BypassIOStatus?.Warning is { Length: > 0 } bypassWarning)
        {
            AttentionNotes.Add(new AttentionNoteVM
            {
                Title = "DirectStorage path has caveats",
                Detail = bypassWarning,
                ToneColor = "Yellow"
            });
        }

        foreach (var software in _preflight.IncompatibleSoftware
                     .OrderByDescending(s => string.Equals(s.Severity, "Critical", StringComparison.OrdinalIgnoreCase))
                     .Take(4))
        {
            AttentionNotes.Add(new AttentionNoteVM
            {
                Title = $"{software.Name} compatibility note",
                Detail = software.Message,
                ToneColor = string.Equals(software.Severity, "Critical", StringComparison.OrdinalIgnoreCase)
                    ? "Red"
                    : "Yellow"
            });
        }

        HasAttentionNotes = AttentionNotes.Count > 0;
        if (!HasAttentionNotes)
        {
            AttentionSummaryText = "The latest readiness scan did not surface any special compatibility notes beyond the standard checks.";
            return;
        }

        int blockingNotes = AttentionNotes.Count(note => string.Equals(note.ToneColor, "Red", StringComparison.OrdinalIgnoreCase));
        int advisoryNotes = AttentionNotes.Count - blockingNotes;
        AttentionSummaryText = blockingNotes > 0
            ? $"{blockingNotes} blocking compatibility {Pluralize(blockingNotes, "note")} and {advisoryNotes} advisory {Pluralize(advisoryNotes, "note")} deserve review before you rely on the migration plan."
            : $"{advisoryNotes} advisory {Pluralize(advisoryNotes, "note")} surfaced during the latest scan. Review them before treating this machine as a routine patch candidate.";
    }

    // Workspace / operational-history partial — UpdateOperationalHistory,
    // ResolveRecoveryKitPath, ResolveVerificationScriptPath, ResolveLatestDiagnosticsReportPath,
    // and IsExistingTextFile live in MainViewModel.Workspace.cs (same partial class).


    // Guidance / workflow partial — UpdateChangePlan, UpdateActionGuidance,
    // BuildBlockingActionSummary, GetCheckDisplayName, UpdateWorkflowGuide, and
    // UpdateRecommendedActions live in MainViewModel.Guidance.cs (same partial class).


    // Settings & preferences partials (UpdateOptionsSummary, UpdatePreferenceSummary,
    // SetThemeMode, RefreshThemeModeSummary, DebouncedSaveSettings + its timer field, all
    // OnXxxChanged hooks, SyncConfigFromUI) live in MainViewModel.Settings.cs — same class,
    // split for readability.

    private static string Pluralize(int count, string singular, string? plural = null)
        => count == 1 ? singular : plural ?? $"{singular}s";

    private int GetPlannedComponentCount() =>
        AppConfig.GetTotalComponents(Config.PatchProfile, IncludeServerKey);

    private void CheckPostReboot()
    {
        try
        {
            var stateFile = Path.Combine(Config.WorkingDir, "last_patch_state.json");
            var status = RegistryService.GetPatchStatus();
            var nativeStatus = _preflight?.NativeNVMeStatus ?? DriveService.TestNativeNVMeActive();

            var currentState = new { Applied = status.Applied, Count = status.Count, NativeActive = nativeStatus.IsActive, ActiveDriver = nativeStatus.ActiveDriver };

            if (File.Exists(stateFile))
            {
                try
                {
                    var raw = File.ReadAllText(stateFile);
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    bool lastApplied = root.TryGetProperty("Applied", out var ap) && ap.ValueKind == JsonValueKind.True;
                    bool lastNativeActive = root.TryGetProperty("NativeActive", out var na) && na.ValueKind == JsonValueKind.True;

                    if (lastApplied && !lastNativeActive && currentState.NativeActive)
                    {
                        Log("========== POST-REBOOT VERIFICATION ==========", "SUCCESS");
                        Log("  Native NVMe driver (nvmedisk.sys) is now ACTIVE after reboot", "SUCCESS");
                        if (_preflight?.CachedMigration is not null)
                        {
                            foreach (var d in _preflight.CachedMigration.Migrated) Log($"    + {d}", "SUCCESS");
                            foreach (var d in _preflight.CachedMigration.Legacy) Log($"    - {d} (still legacy)", "WARNING");
                        }
                        Log("===============================================", "SUCCESS");
                        ToastService.Show("NVMe Driver Active", "Native NVMe driver is now active", ToastType.Success, Config.EnableToasts);
                    }
                }
                catch
                {
                    // Corrupt state file — quietly recreate. Don't surface as a user-facing error.
                }
            }

            // Atomic write: a power loss between WriteAllText and disk flush could leave a
            // zero-byte state file, which would break the next post-reboot detection silently.
            var tempFile = stateFile + ".tmp";
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                sw.Write(JsonSerializer.Serialize(currentState));
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempFile, stateFile, overwrite: true);
        }
        catch { }
    }

    private string BuildConfirmMessage(string title)
    {
        var blockers = new List<string>();   // [!!] you really should read these before clicking yes
        var warnings = new List<string>();   // [!]  tradeoffs you accept by continuing
        var notes = new List<string>();      // [i]  informational — no action needed

        if (title == "Apply Patch" && !_preflight!.HasNVMeDrives)
            warnings.Add("No NVMe drives were found. SATA and USB storage are unaffected by this patch.");
        if (_preflight!.BitLockerEnabled)
            notes.Add("BitLocker will be suspended for one reboot to avoid a recovery-key prompt, then re-enabled automatically.");

        // Critical-severity items (Intel RST, Intel VMD) go into blockers so they render
        // visually distinct from notes. The Compatibility check is already marked critical:true
        // and gates the Apply button — this just mirrors that in the confirmation text.
        foreach (var sw in _preflight.IncompatibleSoftware.Where(s => s.Severity == "Critical"))
            blockers.Add($"{sw.Name}: {sw.Message}");
        foreach (var sw in _preflight.IncompatibleSoftware.Where(s => s.Severity != "Critical"))
            notes.Add($"{sw.Name}: {sw.Message}");
        if (_preflight.DriverInfo?.HasThirdParty == true)
            notes.Add($"Third-party driver detected: {_preflight.DriverInfo.ThirdPartyName}. If it owns the controller, these feature flags may not change the live driver.");

        if (title == "Apply Patch")
        {
            // Educational opener — set expectations before the list of disclaimers so users
            // understand WHAT they're turning on, not just what might break.
            var profileLine = Config.PatchProfile == PatchProfile.Safe
                ? "Mode: SAFE — writes only the primary feature flag (735209102). Extended flags stay off because they correlate with community BSOD reports."
                : "Mode: FULL — writes the primary flag plus two extended flags. This can improve peak performance on some drives, but carries higher crash risk.";
            notes.Insert(0, profileLine);

            // BypassIO / DirectStorage — elevated from an afterthought to a first-class
            // warning. nvmedisk.sys vetoes BypassIO, which hurts DirectStorage games.
            if (_preflight.BypassIOStatus?.Supported == true)
                warnings.Add("DirectStorage tradeoff — BypassIO is active on the system drive. nvmedisk.sys does not support it, so DirectStorage games may fall back to a slower path.");
            else
                notes.Add("DirectStorage — BypassIO is not active on the system drive, so games should not notice the switch.");

            var ssdTools = _preflight.IncompatibleSoftware.Where(s => s.Message.Contains("SCSI pass-through")).ToList();
            if (ssdTools.Count > 0)
                warnings.Add($"SSD vendor tools — {string.Join(", ", ssdTools.Select(s => s.Name))} may stop detecting the drive through nvmedisk.sys. Run firmware updates before patching.");

            if (_preflight.BuildDetails is { Is24H2OrLater: false })
                warnings.Add($"Older Windows build — {_preflight.BuildDetails.DisplayVersion}. This patch is designed for Windows 11 24H2 or later.");

            if (_preflight.IsLaptop)
                warnings.Add("Laptop power — nvmedisk.sys disables APST. Expect shorter battery life and higher idle SSD temperatures.");

            // Microsoft's Feb/Mar 2026 block — let the user know the patch may silently
            // no-op on the latest Insider builds, and that we'll tell them post-reboot.
            notes.Add("Compatibility — some post-February 2026 Insider builds block the registry override. The app will verify after restart and offer a fallback if Windows stays on stornvme.sys.");

            // Recovery kit freshness. Frame the absence as "we'll make one for you" — not
            // as a scary warning. Emphasize that rollback is always a click away.
            var kitPath = ResolveRecoveryKitPath();
            if (kitPath is null)
            {
                notes.Add("Recovery kit — one will be generated after the patch succeeds. Copy it to removable media for the safest rollback path.");
            }
            else
            {
                try
                {
                    var age = DateTime.Now - Directory.GetLastWriteTime(kitPath);
                    if (age.TotalDays > 30)
                        notes.Add($"Recovery kit — the existing kit is {(int)age.TotalDays} days old. A fresh one will be regenerated after the patch.");
                }
                catch { }
            }

            notes.Add("Rollback — the app creates a registry backup, restore point, and recovery kit before applying changes. Removal stays available from the main screen.");
        }

        string header, body;
        if (title == "Apply Patch")
        {
            header = "Enable Microsoft's native NVMe driver?";
            body =
                "This stages a switch from stornvme.sys to nvmedisk.sys, the newer Microsoft driver stack used by Windows Server 2025. " +
                "On modern NVMe drives, the expected upside is stronger random-write performance and lower CPU use under heavy load; sequential reads are usually unchanged.\n\n" +
                "Nothing changes live until Windows restarts.";
        }
        else
        {
            header = "Remove the native NVMe driver?";
            body =
                "This restores the legacy stornvme.sys path and clears the registry flags and Safe Boot entries " +
                "this tool created. Your data is not touched. A restart is required for the change to take effect.";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(header).Append("\n\n").Append(body);

        if (blockers.Count > 0)
        {
            sb.Append("\n\nCritical blockers\n");
            sb.Append(string.Join("\n\n", blockers.Select(s => "• " + s)));
        }
        if (warnings.Count > 0)
        {
            sb.Append("\n\nTradeoffs to accept\n");
            sb.Append(string.Join("\n\n", warnings.Select(s => "• " + s)));
        }
        if (notes.Count > 0)
        {
            sb.Append("\n\nGood to know\n");
            sb.Append(string.Join("\n\n", notes.Select(s => "• " + s)));
        }

        sb.Append(title == "Apply Patch"
            ? "\n\nChoose Apply Patch only if this matches your plan."
            : "\n\nChoose Remove Patch only if you are ready to restart back to the legacy path.");
        return sb.ToString();
    }

    // Commands partial -- every [RelayCommand]-decorated method plus the re-entrancy
    // guards live in MainViewModel.Commands.cs (same partial class). CommunityToolkit.Mvvm
    // source generators walk partial declarations, so XxxCommand bindings still resolve.

    internal static ProcessStartInfo CreateExplorerStartInfo(string path)
    {
        var psi = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false
        };
        psi.ArgumentList.Add(path);
        return psi;
    }

    internal static ProcessStartInfo CreateNotepadStartInfo(string path)
    {
        var psi = new ProcessStartInfo("notepad.exe")
        {
            UseShellExecute = false
        };
        psi.ArgumentList.Add(path);
        return psi;
    }

    private void OpenUrlInBrowser(string url)
    {
        // Defense: only allow absolute HTTPS URLs. With UseShellExecute=true any "file:" or
        // custom scheme would be honored by Windows; plain HTTP is also unnecessary here since
        // every app-owned link is an HTTPS endpoint.
        if (!IsAllowedBrowserUrl(url))
        {
            Log($"Refusing to open non-HTTPS URL: {url}", "WARNING");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"Failed to open URL: {ex.Message}", "ERROR");
            InfoDialog?.Invoke("Could Not Open Browser",
                $"Windows refused to open the URL:\n{url}\n\n{ex.Message}",
                DialogIcon.Error);
        }
    }

    internal static bool IsAllowedBrowserUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    public void OnClosing()
    {
        // Stop the trailing-edge debounce timer before we do the final Save below — otherwise
        // a pending tick can fire AFTER OnClosing returns and race the process teardown,
        // sometimes racing the EventLogService write and always forcing a redundant save.
        try { _settingsSaveDebouncer?.Stop(); } catch { }

        try { SyncConfigFromUI(); } catch { }
        try { ConfigService.Save(Config); } catch { }

        if (Config.AutoSaveLog)
        {
            var snapshot = SnapshotLogHistory();
            if (snapshot.Count > 5)
            {
                try
                {
                    if (!string.IsNullOrEmpty(Config.WorkingDir) && !Directory.Exists(Config.WorkingDir))
                        Directory.CreateDirectory(Config.WorkingDir);
                    var path = Path.Combine(Config.WorkingDir, $"NVMe_Patcher_Log_{DateTime.Now:yyyyMMdd_HHmmss}_autosave.txt");
                    File.WriteAllLines(path, snapshot);
                }
                catch { }
            }
        }

        try
        {
            if (!string.IsNullOrEmpty(Config.WorkingDir) && Directory.Exists(Config.WorkingDir))
            {
                var logFiles = Directory.GetFiles(Config.WorkingDir, "NVMe_Patcher_Log_*.txt")
                    .OrderByDescending(File.GetLastWriteTime).ToArray();
                foreach (var f in logFiles.Skip(20))
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }

        try { EventLogService.Write($"{AppConfig.AppName} closed"); } catch { }
        try { ToastService.DisposeAll(); } catch { }
    }

    private void SetProgress(int value, string text)
    {
        ProgressValue = Math.Min(value, 100);
        ProgressText = text;
        ProgressVisible = value > 0 && value < 100;
    }

    private void RecoverOperationFailure(string operation, Exception ex)
    {
        Log($"{operation} failed unexpectedly: {ex.Message}", "ERROR");
        try
        {
            SetProgress(0, "");
            ButtonsEnabled = true;
            UpdateRegistryDisplay();
            UpdateStatusDisplay();
            UpdateOverviewSummary();
            UpdateOperationalHistory();
        }
        catch
        {
            ButtonsEnabled = true;
        }

        ToastService.Show($"{operation} Failed", "Unexpected error. See activity log.", ToastType.Error, Config.EnableToasts);
        InfoDialog?.Invoke($"{operation} Failed",
            $"The operation stopped before it could finish cleanly:\n{ex.Message}\n\nReview the activity log and exported diagnostics before retrying.",
            DialogIcon.Error);
    }

    private const int MaxVisibleLogEntries = 5000;

    private void AppendLogEntry(string entry, string message, string level)
    {
        LogEntries.Add(entry);
        LogEntryCount++;

        // Bound the visible buffer so a runaway logging loop cannot blow up the UI thread by
        // forcing the TextBox to re-render millions of lines. The full audit trail still lives
        // in _logHistory and gets exported in diagnostics.
        if (LogEntries.Count > MaxVisibleLogEntries)
        {
            int toRemove = LogEntries.Count - MaxVisibleLogEntries;
            for (int i = 0; i < toRemove; i++)
                LogEntries.RemoveAt(0);
        }

        switch (level.ToUpperInvariant())
        {
            case "SUCCESS":
                LogSuccessCount++;
                break;
            case "WARNING":
                LogWarningCount++;
                break;
            case "ERROR":
                LogErrorCount++;
                break;
        }

        LatestActivityText = message;
        UpdateActivitySummary();
        OnPropertyChanged(nameof(LogText));
    }

    // UpdateActivitySummary + UpdateWorkspaceBadges live in MainViewModel.Workspace.cs.


    private void LogBeforeAfter(PatchSnapshot? before, PatchSnapshot? after, string operation)
    {
        if (before is null || after is null) return;
        Log("========== BEFORE / AFTER ==========");
        Log($"  Operation: {operation}");

        string StatusStr(PatchStatus s) => s.Applied ? $"Applied ({s.Count}/{s.Total})" :
            s.Partial ? $"Partial ({s.Count}/{s.Total})" : "Not Applied";

        var bs = StatusStr(before.Status);
        var at = StatusStr(after.Status);
        Log(bs != at ? $"  Status:  {bs}  -->  {at}" : $"  Status:  {bs}  (unchanged)", bs != at ? "SUCCESS" : "INFO");

        foreach (var key in after.Components.Keys)
        {
            if (before.Components.TryGetValue(key, out var bv) && bv != after.Components[key])
            {
                string friendly = AppConfig.FeatureNames.TryGetValue(key, out var fn) ? fn : key;
                Log($"  {friendly}:  {bv}  -->  {after.Components[key]}", "SUCCESS");
            }
        }
        Log("====================================");
    }

    // Diagnostics+ tab commands and their [ObservableProperty] fields live in
    // MainViewModel.Commands.cs (same partial class).

}

// Item-view-model types used by the ObservableCollection<T>s above (PreflightCheckVM,
// RegistryFlagVM, AttentionNoteVM, ChangePlanStepVM, DriveRowVM) live in RowViewModels.cs.
