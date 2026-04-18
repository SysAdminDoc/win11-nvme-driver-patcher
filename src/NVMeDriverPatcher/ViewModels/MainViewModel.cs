using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    [ObservableProperty] private string _statusText = "Checking...";
    [ObservableProperty] private string _statusColor = "#FF71717a";
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
    [ObservableProperty] private string _logRetentionText = "Session logs stay local. Auto-save and Event Log settings shape how much audit trail is retained.";
    [ObservableProperty] private string _latestActivityText = NoActivityYetText;
    [ObservableProperty] private string _activityTabBadgeText = "Idle";
    [ObservableProperty] private string _activityTabBadgeColor = "#FF71717a";
    [ObservableProperty] private string _benchmarkTabBadgeText = "New";
    [ObservableProperty] private string _benchmarkTabBadgeColor = "#FF71717a";
    [ObservableProperty] private string _telemetryTabBadgeText = "Waiting";
    [ObservableProperty] private string _telemetryTabBadgeColor = "#FF71717a";
    [ObservableProperty] private string _recoveryTabBadgeText = "3 missing";
    [ObservableProperty] private string _recoveryTabBadgeColor = "#FFF59E0B";
    [ObservableProperty] private int _benchmarkRunCount;
    [ObservableProperty] private int _recoveryMissingAssetCount = 3;
    [ObservableProperty] private string _statusSummaryText = "Checking build support, storage layout, and rollback safety.";
    [ObservableProperty] private string _buildSummaryText = "Windows build check pending";
    [ObservableProperty] private string _driveInventorySummaryText = "Scanning local drives";
    [ObservableProperty] private string _riskSummaryText = "Risk summary pending";
    [ObservableProperty] private string _optionsSummaryText = "Safety-first defaults keep confirmations and recovery helpers enabled.";
    [ObservableProperty] private string _preferenceSummaryText = "Notifications, audit trail, and restart timing can be tuned here.";
    [ObservableProperty] private string _attentionSummaryText = "Important compatibility notes will surface here after the readiness scan completes.";
    [ObservableProperty] private bool _hasAttentionNotes;
    [ObservableProperty] private string _changePlanSummaryText = "The machine-specific change plan will appear here after readiness checks finish.";
    [ObservableProperty] private bool _hasChangePlanSteps;
    [ObservableProperty] private string _actionReadinessText = "Readiness checks will explain when apply or remove becomes available.";
    [ObservableProperty] private string _actionReadinessColor = "#FFA1B0C8";
    [ObservableProperty] private string _applyButtonTooltipText = "Readiness checks are still running.";
    [ObservableProperty] private string _removeButtonTooltipText = RemoveUnavailableText;
    [ObservableProperty] private string _nextStepTitle = "Running readiness checks";
    [ObservableProperty] private string _nextStepDescription = "Driver changes stay locked until Windows build support, drive visibility, and rollback safety are confirmed.";
    [ObservableProperty] private string _nextStepColor = "#FF60a5fa";
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
    [ObservableProperty] private string _preparationStageColor = "#FF60a5fa";
    [ObservableProperty] private string _restartStageStateText = "Patch not staged yet";
    [ObservableProperty] private string _restartStageDetailText = "Once the patch is applied, this phase will tell you when a reboot is actually required.";
    [ObservableProperty] private string _restartStageColor = "#FF71717a";
    [ObservableProperty] private string _validationStageStateText = "Validation comes after the driver changes";
    [ObservableProperty] private string _validationStageDetailText = "Use benchmarks, telemetry, and diagnostics after reboot to confirm the migration on this exact machine.";
    [ObservableProperty] private string _validationStageColor = "#FF71717a";
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
        "Safe Mode writes only 735209102 — enough to swap the driver with no reports of BSODs tied to it.";

    // Lit when post-reboot verification detects that the override was blocked. Surfaces a
    // persistent "Try ViVeTool Fallback" affordance on the Overview card so the user can
    // revisit the choice without reopening the dialog.
    [ObservableProperty] private bool _showViVeToolFallbackBadge;

    // UI collections
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
            ? "Safe Mode writes only 735209102 — enough to swap the driver with no reports of BSODs tied to it. This is what you want on a daily-driver machine."
            : "Full Mode adds 1853569164 (UxAccOptimization) and 156965516 (Standalone_Future). Higher peak performance on some drives; community BSOD reports cluster on these two flags. Try Safe Mode first — you can always opt in later.";
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
            LeftChecks.Clear();
            RightChecks.Clear();

            var leftMap = new[] { "WindowsVersion", "NVMeDrives", "BitLocker", "VeraCrypt", "LaptopPower", "DriverStatus" };
            var leftLabels = new[] { "Build", "NVMe", "BitLocker", "VeraCrypt", "Power", "Driver" };
            var rightMap = new[] { "ThirdPartyDriver", "Compatibility", "SystemProtection", "BypassIO" };
            var rightLabels = new[] { "3rd Party", "Compat.", "Sys Prot.", "BypassIO" };

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

            // Update badge
            if (_preflight.UpdateAvailable is not null)
            {
                UpdateAvailable = true;
                UpdateVersionText = $"v{_preflight.UpdateAvailable.Version}";
                UpdateUrl = _preflight.UpdateAvailable.URL;
                UpdateTooltip = $"Click to download v{_preflight.UpdateAvailable.Version}";
                Log($"UPDATE AVAILABLE: v{_preflight.UpdateAvailable.Version} -- {AppConfig.GitHubURL}/releases", "WARNING");
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
                var vr = pendingVerification;
                Application.Current?.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(async () =>
                    {
                        bool tryFallback = ConfirmDialog?.Invoke(
                            "Patch Applied But Inactive",
                            "The registry changes are in place, but Windows is still loading the legacy stornvme.sys driver.\n\n" +
                            "Microsoft began blocking this override path on recent Insider builds in early 2026. " +
                            "On those builds the FeatureManagement\\Overrides route is a no-op.\n\n" +
                            "A community fallback exists: ViVeTool writes to a different feature store using IDs 60786016 and 48433719. " +
                            "This works on post-block builds at the cost of an extra dependency.\n\n" +
                            "Would you like this app to download ViVeTool from its official GitHub repository " +
                            $"({ViVeToolService.ViVeToolProjectUrl}) and apply the fallback now?\n\n" +
                            "Yes — download + apply ViVeTool fallback (you will need to restart again afterwards).\n" +
                            "No — leave things as-is. Your registry backup, restore point, and recovery kit are still in place; " +
                            "you can remove the patch from this app at any time."
                        ) == true;
                        if (tryFallback) await ApplyViVeToolFallback();
                    }));
            }
            catch { /* Dispatcher gone during shutdown */ }
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
            Id = "SafeBoot", Name = "Minimal -- BSOD prevention",
            IsSet = status.Keys.Contains("SafeBootMinimal")
        });
        SafeBootFlags.Add(new RegistryFlagVM
        {
            Id = "SafeBoot/Net", Name = "Network -- Safe Mode w/ Networking",
            IsSet = status.Keys.Contains("SafeBootNetwork")
        });
    }

    private void UpdateStatusDisplay()
    {
        var status = RegistryService.GetPatchStatus();

        if (status.Applied)
        {
            StatusText = "Patch applied";
            StatusColor = "#FF22c55e";
            ApplyButtonText = "Reinstall Patch";
            RemoveEnabled = true;
        }
        else if (status.Partial)
        {
            StatusText = $"Patch incomplete ({status.Count}/{status.Total})";
            StatusColor = "#FFf59e0b";
            ApplyButtonText = "Repair Patch";
            RemoveEnabled = true;
        }
        else
        {
            StatusText = "Not applied";
            StatusColor = "#FF71717a";
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
            BuildSummaryText = $"Windows 11 {build.DisplayVersion} • Build {build.BuildNumber}.{build.UBR}";
        else
            BuildSummaryText = "Windows build details unavailable";

        if (TotalDriveCount == 0)
            DriveInventorySummaryText = "No storage devices detected";
        else if (NvmeDriveCount == 0)
            DriveInventorySummaryText = $"No NVMe drives detected across {TotalDriveCount} {Pluralize(TotalDriveCount, "device")}";
        else
            DriveInventorySummaryText = $"{NvmeDriveCount} NVMe {Pluralize(NvmeDriveCount, "drive")} across {TotalDriveCount} detected {Pluralize(TotalDriveCount, "device")}";

        if (CriticalCount > 0)
            RiskSummaryText = $"{CriticalCount} blocking {Pluralize(CriticalCount, "issue")} requires attention before changes are allowed.";
        else if (WarningCount > 0)
            RiskSummaryText = $"{WarningCount} advisory {Pluralize(WarningCount, "note")} detected. Review compatibility and power tradeoffs.";
        else
            RiskSummaryText = "No blocking or advisory issues detected in the current scan.";

        if (_preflight.NativeNVMeStatus?.IsActive == true)
        {
            StatusSummaryText = "Native NVMe is already active. Use the workspace to benchmark, verify migration, or tune the inbox driver.";
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
            NextStepTitle = "Resolve the blocking checks";
            NextStepDescription = "Review the failing readiness items first. Driver changes should wait until the critical blockers are cleared and the scan returns clean.";
            NextStepColor = "#FFef4444";
        }
        else if (_preflight.NativeNVMeStatus?.IsActive == true)
        {
            if (hasBenchmarkHistory)
            {
                NextStepTitle = "Validate and document the outcome";
                NextStepDescription = "Review the benchmark comparison, check telemetry for thermal stability, and export diagnostics if you want a clean record of the final state.";
                NextStepColor = "#FF22c55e";
            }
            else
            {
                NextStepTitle = "Capture a validation benchmark";
                NextStepDescription = "The native driver is already live. Run a benchmark now so this machine has before-and-after style evidence instead of relying on generic expectations.";
                NextStepColor = "#FF22c55e";
            }
        }
        else if (status.Applied)
        {
            NextStepTitle = "Restart to finish the migration";
            NextStepDescription = "Windows has the patch staged, but the live driver path will not change until after the next reboot. Restart when ready, then use the Recovery workspace to review the verification script or rollback kit before you walk away.";
            NextStepColor = "#FFf59e0b";
        }
        else if (WarningCount > 0)
        {
            NextStepTitle = "Review the tradeoffs, then decide";
            NextStepDescription = hasBenchmarkHistory
                ? "You already have baseline data. Read the advisory notes, refresh the backup or recovery assets if needed, and apply only if the compatibility and power tradeoffs are acceptable."
                : "Read the advisory notes, capture a baseline benchmark if you want a comparison, and prepare backup or recovery assets before you apply.";
            NextStepColor = "#FFf59e0b";
        }
        else if (hasBenchmarkHistory)
        {
            NextStepTitle = "Apply when you are comfortable";
            NextStepDescription = "Your checks are clear and you already have baseline data. Refresh the backup or recovery kit, apply the patch, and plan for a restart to activate it.";
            NextStepColor = "#FF60a5fa";
        }
        else
        {
            NextStepTitle = "Create a baseline before changing drivers";
            NextStepDescription = "Take a registry backup, optionally export the recovery kit and run a pre-patch benchmark, then apply once the current state is documented and easy to roll back.";
            NextStepColor = "#FF60a5fa";
        }

        UpdateActionGuidance();
        UpdateChangePlan();
        UpdateWorkflowGuide();
        UpdateRecommendedActions();
        UpdateWorkspaceBadges();
    }

    private void ResetOverviewState()
    {
        StatusText = "Checking...";
        StatusColor = "#FF71717a";
        StatusSummaryText = "Scanning your system build, storage layout, and rollback safety.";
        BuildSummaryText = "Windows build check pending";
        DriveInventorySummaryText = "Scanning local drives";
        RiskSummaryText = "Risk summary pending";
        NextStepTitle = "Running readiness checks";
        NextStepDescription = "Driver changes stay locked until Windows build support, drive visibility, and rollback safety are confirmed.";
        NextStepColor = "#FF60a5fa";
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
        ActionReadinessColor = "#FFA1B0C8";
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
                ToneColor = "#FFEF4444"
            });
        }

        if (_preflight.BitLockerEnabled)
        {
            AttentionNotes.Add(new AttentionNoteVM
            {
                Title = "BitLocker will be suspended for one reboot",
                Detail = "That avoids an unexpected recovery-key prompt during the migration, then Windows resumes normal protection after restart.",
                ToneColor = "#FFF59E0B"
            });
        }

        if (_preflight.IsLaptop)
        {
            AttentionNotes.Add(new AttentionNoteVM
            {
                Title = "Laptop power behavior may change",
                Detail = "Native NVMe can reduce APST power savings on mobile systems, which usually means higher idle SSD temperature and shorter battery life.",
                ToneColor = "#FFF59E0B"
            });
        }

        if (_preflight.BypassIOStatus?.Warning is { Length: > 0 } bypassWarning)
        {
            AttentionNotes.Add(new AttentionNoteVM
            {
                Title = "DirectStorage path has caveats",
                Detail = bypassWarning,
                ToneColor = "#FFF59E0B"
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
                    ? "#FFEF4444"
                    : "#FFF59E0B"
            });
        }

        HasAttentionNotes = AttentionNotes.Count > 0;
        if (!HasAttentionNotes)
        {
            AttentionSummaryText = "The latest readiness scan did not surface any special compatibility notes beyond the standard checks.";
            return;
        }

        int blockingNotes = AttentionNotes.Count(note => string.Equals(note.ToneColor, "#FFEF4444", StringComparison.OrdinalIgnoreCase));
        int advisoryNotes = AttentionNotes.Count - blockingNotes;
        AttentionSummaryText = blockingNotes > 0
            ? $"{blockingNotes} blocking compatibility {Pluralize(blockingNotes, "note")} and {advisoryNotes} advisory {Pluralize(advisoryNotes, "note")} deserve review before you rely on the migration plan."
            : $"{advisoryNotes} advisory {Pluralize(advisoryNotes, "note")} surfaced during the latest scan. Review them before treating this machine as a routine patch candidate.";
    }

    private void UpdateOperationalHistory()
    {
        try
        {
            if (Directory.Exists(Config.WorkingDir))
            {
                var backupFiles = Directory.GetFiles(Config.WorkingDir, "*.reg", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTime)
                    .ToList();
                HasBackupFiles = backupFiles.Count > 0;

                if (backupFiles.Count == 0)
                {
                    BackupHistoryText = NoBackupHistoryText;
                }
                else
                {
                    var latestBackup = backupFiles[0];
                    BackupHistoryText = $"{backupFiles.Count} backup {Pluralize(backupFiles.Count, "file")} available. Latest: {latestBackup.Name} on {latestBackup.LastWriteTime:g}.";
                }
            }
            else
            {
                HasBackupFiles = false;
                BackupHistoryText = "Working folder is unavailable, so backup history cannot be read.";
            }
        }
        catch
        {
            HasBackupFiles = false;
            BackupHistoryText = "Backup history could not be read from the working folder.";
        }

        try
        {
            var snapshots = DataService.GetSnapshots();
            if (snapshots.Count == 0)
            {
                SnapshotHistoryText = NoSnapshotHistoryText;
            }
            else
            {
                var latestSnapshot = snapshots[0];
                SnapshotHistoryText = $"{snapshots.Count} snapshot {Pluralize(snapshots.Count, "entry")} recorded. Latest: {latestSnapshot.Description} on {latestSnapshot.Timestamp:g}.";
            }
        }
        catch
        {
            SnapshotHistoryText = "Snapshot history could not be loaded.";
        }

        try
        {
            var benchmarks = DataService.GetBenchmarkHistory();
            if (benchmarks.Count == 0)
            {
                HasBenchmarkHistory = false;
                BenchmarkRunCount = 0;
                BenchmarkHistoryText = NoBenchmarkHistoryText;
            }
            else
            {
                HasBenchmarkHistory = true;
                BenchmarkRunCount = benchmarks.Count;
                var latestBenchmark = benchmarks[0];
                BenchmarkHistoryText = $"{benchmarks.Count} benchmark {Pluralize(benchmarks.Count, "run")} saved. Latest: {latestBenchmark.Label} on {latestBenchmark.Timestamp:g}.";
            }
        }
        catch
        {
            HasBenchmarkHistory = false;
            BenchmarkRunCount = 0;
            BenchmarkHistoryText = "Benchmark history could not be loaded.";
        }

        try
        {
            var recoveryKitPath = ResolveRecoveryKitPath();
            HasRecoveryKit = !string.IsNullOrWhiteSpace(recoveryKitPath);

            if (!HasRecoveryKit)
            {
                RecoveryKitStatusText = NoRecoveryKitText;
            }
            else
            {
                var latestRecoveryWrite = Directory.GetFiles(recoveryKitPath!, "*", SearchOption.TopDirectoryOnly)
                    .Select(File.GetLastWriteTime)
                    .DefaultIfEmpty(Directory.GetLastWriteTime(recoveryKitPath!))
                    .Max();
                var locationLabel = string.Equals(recoveryKitPath, Path.Combine(Config.WorkingDir, "NVMe_Recovery_Kit"), StringComparison.OrdinalIgnoreCase)
                    ? "working folder"
                    : "export location";

                RecoveryKitStatusText = $"Recovery kit ready in the {locationLabel}. Last updated {latestRecoveryWrite:g}. Includes offline rollback files for Windows and WinRE.";
            }
        }
        catch
        {
            HasRecoveryKit = false;
            RecoveryKitStatusText = "Recovery kit status could not be read.";
        }

        try
        {
            var verificationScriptPath = ResolveVerificationScriptPath();
            HasVerificationScript = !string.IsNullOrWhiteSpace(verificationScriptPath);

            if (!HasVerificationScript)
            {
                VerificationScriptStatusText = NoVerificationScriptText;
            }
            else
            {
                var fileInfo = new FileInfo(verificationScriptPath!);
                VerificationScriptStatusText = $"Verification script ready as {fileInfo.Name}, updated {fileInfo.LastWriteTime:g}. Use it after reboot to confirm every expected registry and Safe Mode key is present.";
            }
        }
        catch
        {
            HasVerificationScript = false;
            VerificationScriptStatusText = "Verification script status could not be read.";
        }

        try
        {
            var diagnosticsReportPath = ResolveLatestDiagnosticsReportPath();
            HasDiagnosticsReport = !string.IsNullOrWhiteSpace(diagnosticsReportPath);

            if (!HasDiagnosticsReport)
            {
                DiagnosticsReportStatusText = NoDiagnosticsReportText;
            }
            else
            {
                var fileInfo = new FileInfo(diagnosticsReportPath!);
                DiagnosticsReportStatusText = $"Latest diagnostics report: {fileInfo.Name}, exported {fileInfo.LastWriteTime:g}. Keep it with the recovery kit when you need a support-ready snapshot of this machine.";
            }
        }
        catch
        {
            HasDiagnosticsReport = false;
            DiagnosticsReportStatusText = "Diagnostics report status could not be read.";
        }

        RecoveryWorkspaceSummaryText = (HasRecoveryKit, HasVerificationScript, HasDiagnosticsReport) switch
        {
            (true, true, true) => "Rollback, verification, and diagnostics assets are all in place. This machine has a strong paper trail if you need to confirm or reverse the change.",
            (true, true, false) => "Rollback and verification assets are ready. Export diagnostics too if you want a complete support bundle for this machine.",
            (true, false, true) => "Rollback and diagnostics are ready, but the verification script is still missing. Generate it before the next reboot so confirmation stays simple.",
            (false, true, true) => "Verification and diagnostics are ready, but the offline recovery kit is still missing. Export one to removable media before you rely on the patch long term.",
            (true, false, false) => "Rollback materials are ready, but verification and diagnostics are still missing. Generate both before a risky reboot or remote handoff.",
            (false, true, false) => "A verification script exists, but recovery and diagnostics are still incomplete. Export a full recovery kit so rollback does not depend on memory.",
            (false, false, true) => "Diagnostics are available, but rollback and verification assets are still missing. Generate them so troubleshooting stays actionable.",
            _ => "Generate rollback and verification assets so the system can be reversed or confirmed without guesswork."
        };

        UpdateChangePlan();
        UpdateWorkflowGuide();
        UpdateRecommendedActions();
        UpdateWorkspaceBadges();
    }

    private string? ResolveRecoveryKitPath()
    {
        if (!string.IsNullOrWhiteSpace(Config.LastRecoveryKitPath) && Directory.Exists(Config.LastRecoveryKitPath))
            return Config.LastRecoveryKitPath;

        if (string.IsNullOrWhiteSpace(Config.WorkingDir))
            return null;

        var localKitPath = Path.Combine(Config.WorkingDir, "NVMe_Recovery_Kit");
        return Directory.Exists(localKitPath) ? localKitPath : null;
    }

    private string? ResolveVerificationScriptPath()
    {
        if (!string.IsNullOrWhiteSpace(Config.LastVerificationScriptPath) && File.Exists(Config.LastVerificationScriptPath))
            return Config.LastVerificationScriptPath;

        if (string.IsNullOrWhiteSpace(Config.WorkingDir))
            return null;

        var localScriptPath = Path.Combine(Config.WorkingDir, "Verify_NVMe_Patch.ps1");
        return File.Exists(localScriptPath) ? localScriptPath : null;
    }

    private string? ResolveLatestDiagnosticsReportPath()
    {
        if (!string.IsNullOrWhiteSpace(Config.LastDiagnosticsPath) && File.Exists(Config.LastDiagnosticsPath))
            return Config.LastDiagnosticsPath;

        if (string.IsNullOrEmpty(Config.WorkingDir) || !Directory.Exists(Config.WorkingDir))
            return null;

        try
        {
            return Directory.GetFiles(Config.WorkingDir, "NVMe_Diagnostics_*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }
        catch
        {
            // Folder enumeration can transiently throw if the user is moving files around.
            return null;
        }
    }

    private void UpdateChangePlan()
    {
        ChangePlanSteps.Clear();

        void AddStep(string title, string detail, string toneColor)
        {
            ChangePlanSteps.Add(new ChangePlanStepVM
            {
                Title = title,
                Detail = detail,
                ToneColor = toneColor
            });
        }

        if (_preflight is null)
        {
            HasChangePlanSteps = false;
            ChangePlanSummaryText = "The machine-specific change plan will appear here after readiness checks finish.";
            return;
        }

        var patchStatus = RegistryService.GetPatchStatus();
        int plannedComponentCount = AppConfig.TotalComponents + (IncludeServerKey ? 1 : 0);
        bool nativeDriverActive = _preflight.NativeNVMeStatus?.IsActive == true;

        if (CriticalCount > 0)
        {
            ChangePlanSummaryText = "The plan is paused until blocking checks are cleared. No registry change should be staged on this machine yet.";
            AddStep(
                "Resolve the blocking checks",
                "Start with the failing readiness items and the compatibility highlights. This machine still has one or more hard stops that make patching unsafe or incomplete.",
                "#FFEF4444");
            AddStep(
                "Refresh the readiness scan",
                "Run the checks again after you address the blockers so the app can rebuild the migration plan from a clean state.",
                "#FFF59E0B");
            AddStep(
                "Document the baseline only after the scan clears",
                "Once the blockers are gone, capture a backup or benchmark baseline before treating the patch as a routine change.",
                "#FF60A5FA");
        }
        else if (patchStatus.Partial)
        {
            ChangePlanSummaryText = "This machine is already in a partial patch state. The next step is to normalize that state before relying on a reboot.";
            AddStep(
                "Repair or remove the partial patch",
                "Use the existing controls to finish the missing registry writes or revert them so the system is not left in an ambiguous state.",
                "#FFEF4444");
            AddStep(
                "Rebuild recovery evidence",
                "Refresh the recovery kit, verification script, and diagnostics after the partial state is resolved so the local support trail matches reality.",
                "#FFF59E0B");
            AddStep(
                "Re-run readiness before the next reboot",
                "A clean readiness pass is the best signal that the machine is safe to restart into the intended driver path.",
                "#FF60A5FA");
        }
        else if (nativeDriverActive)
        {
            ChangePlanSummaryText = "Native NVMe is already active, so the remaining work is validation and long-term rollback hygiene rather than staging another driver change.";
            AddStep(
                "Verify the live migration",
                HasVerificationScript
                    ? "Open the verification script or review the current driver path to confirm the expected registry keys and Safe Mode protections are still present."
                    : "Generate the verification script so post-migration checks are easy to repeat or hand off.",
                "#FF22C55E");
            AddStep(
                "Capture machine-specific evidence",
                HasBenchmarkHistory
                    ? "Review the benchmark comparison and telemetry trend so the outcome is supported by this machine's own evidence."
                    : "Run a validation benchmark and review telemetry so the final state is backed by more than a status label.",
                "#FF60A5FA");
            AddStep(
                "Keep rollback material current",
                HasRecoveryKit && HasDiagnosticsReport
                    ? "Recovery assets and diagnostics are already present. Refresh them only when you want a newer support bundle."
                    : "Export the recovery kit and diagnostics so a future rollback or support handoff does not depend on memory.",
                "#FFF59E0B");
        }
        else if (patchStatus.Applied)
        {
            ChangePlanSummaryText = "The registry changes are already staged. The remaining plan is about reboot, verification, and final proof that the driver path actually changed.";
            AddStep(
                "Restart to activate nvmedisk.sys",
                _preflight.BitLockerEnabled
                    ? "Windows still needs a reboot to switch driver paths, and BitLocker has already been prepared to avoid an unnecessary recovery-key interruption."
                    : "Windows still needs a reboot to switch from the legacy path to the native driver. Nothing meaningful changes until that restart finishes.",
                "#FFF59E0B");
            AddStep(
                "Run verification and review recovery assets",
                HasVerificationScript && HasRecoveryKit
                    ? "The verification script and rollback kit are already available, so the post-reboot safety path is in place."
                    : "Before you walk away, make sure the verification script and recovery kit are ready so the reboot path stays calm if anything looks off.",
                "#FF60A5FA");
            AddStep(
                "Validate the outcome with local evidence",
                HasBenchmarkHistory
                    ? "Return after reboot to compare benchmarks, review telemetry, and export diagnostics if you want a support-ready final record."
                    : "After reboot, run a benchmark or export diagnostics so the migration result is documented on this exact machine.",
                "#FF22C55E");
        }
        else
        {
            ChangePlanSummaryText = "Applying on this machine will stage the native NVMe path, create local rollback evidence, and require a reboot before the live driver actually changes.";
            AddStep(
                "Document the current baseline",
                HasBackupFiles || HasBenchmarkHistory
                    ? "You already have some local evidence. Refresh the backup or capture a new benchmark if you want a stronger before-state record."
                    : "Create a registry backup and optionally run a benchmark so the current state is easy to compare or restore later.",
                "#FF60A5FA");
            AddStep(
                "Stage the driver transition",
                IncludeServerKey
                    ? $"The apply action will write {plannedComponentCount} patch components, including the optional Server 2025 key, plus Safe Mode protections and local snapshots."
                    : $"The apply action will write {plannedComponentCount} core patch components, add Safe Mode protections, and save local snapshots before and after the change.",
                "#FF22C55E");
            AddStep(
                "Prepare for reboot and follow-up validation",
                _preflight.BitLockerEnabled
                    ? "BitLocker will be suspended for one reboot, recovery assets will be refreshed, and you should return after restart to confirm the native path is active."
                    : "Recovery assets will be refreshed, then a reboot is required. After restart, verify the migration and capture final evidence through telemetry or diagnostics.",
                "#FFF59E0B");
        }

        HasChangePlanSteps = ChangePlanSteps.Count > 0;
    }

    private void UpdateActionGuidance()
    {
        if (_preflight is null)
        {
            ActionReadinessText = "Readiness checks will explain when apply or remove becomes available.";
            ActionReadinessColor = "#FFA1B0C8";
            ApplyButtonTooltipText = "Readiness checks are still running.";
            RemoveButtonTooltipText = RemoveUnavailableText;
            return;
        }

        var status = RegistryService.GetPatchStatus();
        int plannedComponentCount = AppConfig.TotalComponents + (IncludeServerKey ? 1 : 0);

        if (CriticalCount > 0)
        {
            string blockerSummary = BuildBlockingActionSummary();
            ActionReadinessText = $"Apply is blocked until the critical checks are resolved. {blockerSummary}";
            ActionReadinessColor = "#FFEF4444";
            ApplyButtonTooltipText = ActionReadinessText;
            RemoveButtonTooltipText = status.Applied || status.Partial
                ? "Remove can still revert the staged or partial registry state."
                : RemoveUnavailableText;
            return;
        }

        if (_preflight.NativeNVMeStatus?.IsActive == true && !status.Applied && !status.Partial)
        {
            ActionReadinessText = "Native NVMe is already active. Apply is optional here and mainly helps stage the registry keys, Safe Mode protections, and recovery material for consistency.";
            ActionReadinessColor = "#FF22C55E";
            ApplyButtonTooltipText = $"Apply will stage {plannedComponentCount} patch components plus recovery helpers, even though the native driver path is already live.";
            RemoveButtonTooltipText = RemoveUnavailableText;
            return;
        }

        if (status.Applied)
        {
            ActionReadinessText = "Reinstall refreshes the staged registry set and recovery assets. Remove deletes the patch keys and returns the machine to the legacy path after the next reboot.";
            ActionReadinessColor = "#FFF59E0B";
            ApplyButtonTooltipText = $"Reinstall will refresh {plannedComponentCount} patch components, Safe Mode protections, and local snapshots.";
            RemoveButtonTooltipText = "Remove clears the staged patch keys and Safe Mode protections, then requires a reboot to restore the legacy path.";
            return;
        }

        if (status.Partial)
        {
            ActionReadinessText = "This machine is in a partial patch state. Repair is available to complete the missing writes, and Remove can also cleanly revert the partial state.";
            ActionReadinessColor = "#FFF59E0B";
            ApplyButtonTooltipText = $"Repair will attempt to complete the intended {plannedComponentCount} patch components and refresh recovery helpers.";
            RemoveButtonTooltipText = "Remove can clean up the partial registry state before you retry the migration.";
            return;
        }

        if (WarningCount > 0)
        {
            ActionReadinessText = "Apply is available, but this machine still has advisory notes. Review Compatibility Highlights and the machine impact plan before you commit.";
            ActionReadinessColor = "#FFF59E0B";
            ApplyButtonTooltipText = $"Apply will stage {plannedComponentCount} patch components, Safe Mode protections, and local recovery material once you accept the advisory tradeoffs.";
            RemoveButtonTooltipText = RemoveUnavailableText;
            return;
        }

        ActionReadinessText = $"Apply is ready. It will stage {plannedComponentCount} patch components, add Safe Mode protections, save snapshots, and require a reboot before the live driver path changes.";
        ActionReadinessColor = "#FF22C55E";
        ApplyButtonTooltipText = ActionReadinessText;
        RemoveButtonTooltipText = RemoveUnavailableText;
    }

    private string BuildBlockingActionSummary()
    {
        if (_preflight is null)
            return "The current machine state is still unknown.";

        var blockingChecks = _preflight.Checks
            .Where(pair => pair.Value.Critical && pair.Value.Status == CheckStatus.Fail)
            .Select(pair => $"{GetCheckDisplayName(pair.Key)}: {pair.Value.Message}")
            .Take(2)
            .ToList();

        if (blockingChecks.Count == 0)
            return "Resolve the blocking readiness items and run the scan again.";

        return string.Join(" ", blockingChecks);
    }

    private static string GetCheckDisplayName(string key)
    {
        return key switch
        {
            "WindowsVersion" => "Windows build",
            "NVMeDrives" => "NVMe inventory",
            "BitLocker" => "BitLocker",
            "VeraCrypt" => "VeraCrypt",
            "LaptopPower" => "Power model",
            "DriverStatus" => "Driver state",
            "ThirdPartyDriver" => "Third-party driver",
            "Compatibility" => "Compatibility",
            "SystemProtection" => "System protection",
            "BypassIO" => "BypassIO",
            _ => key
        };
    }

    private void UpdateWorkflowGuide()
    {
        var patchStatus = RegistryService.GetPatchStatus();
        bool nativeDriverActive = _preflight?.NativeNVMeStatus?.IsActive == true;
        bool prepEvidenceReady = HasBackupFiles || HasBenchmarkHistory || HasRecoveryKit;
        bool prepReady = CriticalCount == 0 && HasBackupFiles && (HasBenchmarkHistory || HasRecoveryKit);
        bool validationEvidenceReady = HasBenchmarkHistory || HasDiagnosticsReport;

        if (CriticalCount > 0)
        {
            PreparationStageStateText = "Blocked by readiness checks";
            PreparationStageDetailText = "Resolve the critical preflight issues first. Backups and baselines matter, but only after the system is allowed to proceed.";
            PreparationStageColor = "#FFef4444";
        }
        else if (prepReady)
        {
            PreparationStageStateText = "Prepared for a controlled change";
            PreparationStageDetailText = "Backups and baseline evidence are in place, so the patch can be staged with a much clearer rollback story.";
            PreparationStageColor = "#FF22c55e";
        }
        else if (prepEvidenceReady)
        {
            PreparationStageStateText = "Partially prepared";
            PreparationStageDetailText = "You already captured some safety material. Add the missing backup or baseline pieces before treating this as a fully documented change.";
            PreparationStageColor = "#FFf59e0b";
        }
        else
        {
            PreparationStageStateText = "Capture baseline and safety artifacts";
            PreparationStageDetailText = "Start with a registry backup, optional pre-patch benchmark, and recovery materials so the change stays easy to explain and reverse.";
            PreparationStageColor = "#FF60a5fa";
        }

        if (nativeDriverActive)
        {
            RestartStageStateText = "Migration is live";
            RestartStageDetailText = "Windows is already running on nvmedisk.sys, so the reboot phase is complete for this machine.";
            RestartStageColor = "#FF22c55e";
        }
        else if (patchStatus.Applied)
        {
            RestartStageStateText = "Restart required";
            RestartStageDetailText = "The patch is staged, but Windows will stay on the legacy path until the next reboot actually completes.";
            RestartStageColor = "#FFf59e0b";
        }
        else if (patchStatus.Partial)
        {
            RestartStageStateText = "Staging is incomplete";
            RestartStageDetailText = "Some components are present, but the system is not in a clean restart-ready state yet. Repair or remove the partial patch first.";
            RestartStageColor = "#FFef4444";
        }
        else
        {
            RestartStageStateText = "Patch not staged yet";
            RestartStageDetailText = "Once the patch is applied, this phase will flip to a clear restart requirement instead of making you infer it from logs.";
            RestartStageColor = "#FF71717a";
        }

        if (nativeDriverActive && validationEvidenceReady && HasVerificationScript)
        {
            ValidationStageStateText = "Validated with local evidence";
            ValidationStageDetailText = "The native path is active and this machine has local proof through benchmarks, diagnostics, and verification materials.";
            ValidationStageColor = "#FF22c55e";
        }
        else if (nativeDriverActive && (validationEvidenceReady || HasVerificationScript))
        {
            ValidationStageStateText = "Validation is in progress";
            ValidationStageDetailText = "The driver migration is live. Finish the proof trail with a benchmark comparison or diagnostics export so the outcome is easy to defend later.";
            ValidationStageColor = "#FFf59e0b";
        }
        else if (patchStatus.Applied)
        {
            ValidationStageStateText = "Available after reboot";
            ValidationStageDetailText = "Telemetry, benchmarks, and diagnostics become meaningful proof once Windows has restarted onto the native driver.";
            ValidationStageColor = "#FF60a5fa";
        }
        else
        {
            ValidationStageStateText = "Validation comes after the driver changes";
            ValidationStageDetailText = "Use benchmarks, telemetry, and diagnostics after reboot to confirm the migration on this exact machine.";
            ValidationStageColor = "#FF71717a";
        }
    }

    private void UpdateRecommendedActions()
    {
        void SetPrimary(string id, string text, bool enabled)
        {
            HasNextStepPrimaryAction = true;
            NextStepPrimaryActionId = id;
            NextStepPrimaryActionText = text;
            NextStepPrimaryActionEnabled = enabled;
        }

        void SetSecondary(string id, string text, bool enabled)
        {
            HasNextStepSecondaryAction = true;
            NextStepSecondaryActionId = id;
            NextStepSecondaryActionText = text;
            NextStepSecondaryActionEnabled = enabled;
        }

        HasNextStepPrimaryAction = false;
        NextStepPrimaryActionText = "";
        NextStepPrimaryActionId = "";
        NextStepPrimaryActionEnabled = false;
        HasNextStepSecondaryAction = false;
        NextStepSecondaryActionText = "";
        NextStepSecondaryActionId = "";
        NextStepSecondaryActionEnabled = false;

        if (IsLoading)
            return;

        var patchStatus = RegistryService.GetPatchStatus();
        bool nativeDriverActive = _preflight?.NativeNVMeStatus?.IsActive == true;

        if (CriticalCount > 0)
        {
            SetPrimary("refresh_checks", "Refresh Checks", ButtonsEnabled);
            SetSecondary(
                HasRecoveryKit || HasVerificationScript || HasDiagnosticsReport ? "open_recovery" : "open_activity",
                HasRecoveryKit || HasVerificationScript || HasDiagnosticsReport ? "Review Recovery" : "Open Activity",
                true);
            return;
        }

        if (nativeDriverActive)
        {
            if (HasBenchmarkHistory)
            {
                SetPrimary("open_telemetry", "Open Telemetry", true);
                SetSecondary(
                    HasDiagnosticsReport ? "open_recovery" : "export_diagnostics",
                    HasDiagnosticsReport ? "Review Recovery" : "Export Diagnostics",
                    HasDiagnosticsReport || ButtonsEnabled);
            }
            else
            {
                SetPrimary("run_benchmark", "Run Benchmark", ButtonsEnabled);
                SetSecondary("open_telemetry", "Open Telemetry", true);
            }

            return;
        }

        if (patchStatus.Applied)
        {
            SetPrimary("open_recovery", "Open Recovery", true);
            if (!HasDiagnosticsReport)
                SetSecondary("export_diagnostics", "Export Diagnostics", ButtonsEnabled);
            else if (!HasRecoveryKit)
                SetSecondary("create_recovery_kit", "Create Recovery Kit", ButtonsEnabled);
            else
                SetSecondary("open_activity", "Open Activity", true);
            return;
        }

        if (WarningCount > 0)
        {
            if (!HasBenchmarkHistory)
                SetPrimary("run_benchmark", "Capture Baseline", ButtonsEnabled);
            else if (!HasBackupFiles)
                SetPrimary("create_backup", "Create Backup", ButtonsEnabled);
            else
                SetPrimary("open_recovery", "Review Recovery", true);

            if (!HasBackupFiles && !HasBenchmarkHistory)
                SetSecondary("create_backup", "Create Backup", ButtonsEnabled);
            else if (!HasRecoveryKit)
                SetSecondary("create_recovery_kit", "Create Recovery Kit", ButtonsEnabled);
            else
                SetSecondary("open_activity", "Open Activity", true);
            return;
        }

        if (!HasBenchmarkHistory)
            SetPrimary("run_benchmark", "Run Benchmark", ButtonsEnabled);
        else if (!HasBackupFiles)
            SetPrimary("create_backup", "Create Backup", ButtonsEnabled);
        else if (!HasRecoveryKit)
            SetPrimary("create_recovery_kit", "Create Recovery Kit", ButtonsEnabled);
        else
            SetPrimary("apply_patch", "Apply Patch", ApplyEnabled);

        if (!HasBackupFiles && !HasBenchmarkHistory)
            SetSecondary("create_backup", "Create Backup", ButtonsEnabled);
        else if (!HasRecoveryKit)
            SetSecondary("open_recovery", "Open Recovery", true);
        else
            SetSecondary("open_benchmarks", "Review Benchmarks", true);
    }

    private void UpdateOptionsSummary()
    {
        var serverKeyText = IncludeServerKey
            ? "Server 2025 compatibility key is enabled for the broader compatibility path."
            : "Server 2025 compatibility key is off. Leave it that way unless this machine needs the broader compatibility path.";
        var warningsText = SkipWarnings
            ? "Expert warning confirmations are reduced."
            : "Confirmation warnings stay on for a safer review pass.";

        OptionsSummaryText = $"{serverKeyText} {warningsText}";
    }

    private void UpdatePreferenceSummary()
    {
        string restartSummary = int.TryParse(RestartDelayText, out int delay) && delay >= 5 && delay <= 300
            ? $"Restart countdown is set to {delay} seconds."
            : "Restart countdown needs a value between 5 and 300 seconds.";

        string toastSummary = EnableToasts
            ? "Toast notifications are enabled."
            : "Toast notifications are muted.";

        string auditSummary = WriteEventLog
            ? "Windows Event Log auditing is enabled."
            : "Windows Event Log auditing is off.";

        string autosaveSummary = AutoSaveLog
            ? "Activity logs will auto-save on close."
            : "Activity logs only persist when you export them manually.";

        PreferenceSummaryText = $"{toastSummary} {auditSummary} {autosaveSummary} {restartSummary}";
    }

    // Persist settings shortly after a change so a crash before normal close doesn't lose the
    // user's preferences. Trailing-edge throttle: a rapid burst of toggles only writes once at
    // the end. Avoids hammering disk while still being durable.
    private System.Windows.Threading.DispatcherTimer? _settingsSaveDebouncer;
    private void DebouncedSaveSettings()
    {
        if (Application.Current is null) return;
        if (_settingsSaveDebouncer is null)
        {
            _settingsSaveDebouncer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            _settingsSaveDebouncer.Tick += (_, _) =>
            {
                _settingsSaveDebouncer?.Stop();
                try
                {
                    SyncConfigFromUI();
                    ConfigService.Save(Config);
                }
                catch { /* Best-effort */ }
            };
        }
        _settingsSaveDebouncer.Stop();
        _settingsSaveDebouncer.Start();
    }

    partial void OnIncludeServerKeyChanged(bool value)
    {
        UpdateOptionsSummary();
        UpdateChangePlan();
        DebouncedSaveSettings();
    }

    partial void OnSkipWarningsChanged(bool value)
    {
        UpdateOptionsSummary();
        UpdateChangePlan();
        DebouncedSaveSettings();
    }

    partial void OnAutoSaveLogChanged(bool value)
    {
        UpdateActivitySummary();
        UpdatePreferenceSummary();
        DebouncedSaveSettings();
    }

    partial void OnEnableToastsChanged(bool value)
    {
        UpdatePreferenceSummary();
        DebouncedSaveSettings();
    }

    partial void OnWriteEventLogChanged(bool value)
    {
        UpdateActivitySummary();
        UpdatePreferenceSummary();
        DebouncedSaveSettings();
    }

    partial void OnRestartDelayTextChanged(string value)
    {
        UpdatePreferenceSummary();
        DebouncedSaveSettings();
    }
    partial void OnButtonsEnabledChanged(bool value) => UpdateRecommendedActions();
    partial void OnApplyEnabledChanged(bool value) => UpdateRecommendedActions();
    partial void OnIsLoadingChanged(bool value) => UpdateRecommendedActions();

    private static string Pluralize(int count, string singular, string? plural = null)
        => count == 1 ? singular : plural ?? $"{singular}s";

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
            warnings.Add("NO NVMe DRIVES FOUND — The patch only benefits NVMe drives using Microsoft's inbox driver. SATA/USB storage is unaffected.");
        if (_preflight!.BitLockerEnabled)
            notes.Add("BITLOCKER — will be auto-suspended for one reboot so you don't get a recovery-key prompt. It re-enables automatically on second boot.");
        foreach (var sw in _preflight.IncompatibleSoftware.Where(s => s.Severity != "Critical"))
            notes.Add($"{sw.Name}: {sw.Message}");
        if (_preflight.DriverInfo?.HasThirdParty == true)
            notes.Add($"THIRD-PARTY DRIVER: {_preflight.DriverInfo.ThirdPartyName} is installed. The feature flags may have no effect if this driver owns the controller.");

        if (title == "Apply Patch")
        {
            // Educational opener — set expectations before the list of disclaimers so users
            // understand WHAT they're turning on, not just what might break.
            var profileLine = Config.PatchProfile == PatchProfile.Safe
                ? "Mode: SAFE — writes the single primary feature flag (735209102) that swaps stornvme.sys for nvmedisk.sys. Community BSOD reports correlate with the two extended flags, so this mode leaves them off."
                : "Mode: FULL — writes all three feature flags (primary + two extended). Higher peak performance on some drives, higher crash risk on others. You can revert at any time.";
            notes.Insert(0, profileLine);

            // BypassIO / DirectStorage — elevated from an afterthought to a first-class
            // warning. nvmedisk.sys vetoes BypassIO, which hurts DirectStorage games.
            if (_preflight.BypassIOStatus?.Supported == true)
                warnings.Add("DIRECTSTORAGE USERS — Your system drive currently supports BypassIO. The native NVMe driver does NOT, so DirectStorage games (Forspoken, Ratchet & Clank, etc.) will fall back to the slower path after this patch. If you game on this machine, skip the patch or plan to toggle it off for game sessions.");
            else
                notes.Add("BypassIO is not currently active on the system drive, so DirectStorage games won't notice the switch.");

            var ssdTools = _preflight.IncompatibleSoftware.Where(s => s.Message.Contains("SCSI pass-through")).ToList();
            if (ssdTools.Count > 0)
                warnings.Add($"SSD VENDOR TOOLS — {string.Join(", ", ssdTools.Select(s => s.Name))} talk to drives through stornvme.sys. After the patch they may stop detecting your drive or fail to update firmware. Run any pending firmware updates BEFORE patching.");

            if (_preflight.BuildDetails is { Is24H2OrLater: false })
                warnings.Add($"OLDER BUILD — {_preflight.BuildDetails.DisplayVersion}. The patch was designed for Windows 11 24H2+. Behavior on earlier builds is not guaranteed.");

            if (_preflight.IsLaptop)
                warnings.Add("LAPTOP — nvmedisk.sys disables APST (Autonomous Power State Transition). Expect ~15% shorter battery life and higher SSD idle temperatures. Desktops are unaffected.");

            // Microsoft's Feb/Mar 2026 block — let the user know the patch may silently
            // no-op on the latest Insider builds, and that we'll tell them post-reboot.
            notes.Add("COMPATIBILITY NOTE — Microsoft began neutering the registry-override path on post-Feb-2026 Insider builds. The patcher will write the keys either way, and on next launch this tool will verify whether Windows actually swapped drivers. If it didn't, you'll get a clear message with no damage done.");

            // Recovery kit freshness. Frame the absence as "we'll make one for you" — not
            // as a scary warning. Emphasize that rollback is always a click away.
            var kitPath = ResolveRecoveryKitPath();
            if (kitPath is null)
            {
                notes.Add("RECOVERY KIT — You don't have one yet. We'll generate one automatically right after the patch succeeds; copy it to a USB stick for peace of mind.");
            }
            else
            {
                try
                {
                    var age = DateTime.Now - Directory.GetLastWriteTime(kitPath);
                    if (age.TotalDays > 30)
                        notes.Add($"RECOVERY KIT — Your existing kit is {(int)age.TotalDays} days old. A fresh one will be regenerated automatically post-patch.");
                }
                catch { }
            }

            notes.Add("ROLLBACK — This app takes a registry backup, creates a System Restore point, and saves a recovery kit before touching anything. Uninstalling the patch is one click; worst-case recovery is documented in the kit's README.");
        }

        string header, body;
        if (title == "Apply Patch")
        {
            header = "Enable Microsoft's native NVMe driver?";
            body =
                "This replaces the legacy stornvme.sys driver with the newer nvmedisk.sys driver " +
                "(the same stack Windows Server 2025 ships by default). Typical gains on modern NVMe drives: " +
                "~80% higher random-write IOPS and ~45% lower CPU under heavy load. Sequential read throughput " +
                "is essentially unchanged.\n\n" +
                "The change takes effect after a restart.";
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
            sb.Append("\n\n-- CRITICAL --\n");
            sb.Append(string.Join("\n\n", blockers.Select(s => "[!!] " + s)));
        }
        if (warnings.Count > 0)
        {
            sb.Append("\n\n-- TRADEOFFS YOU ACCEPT BY CONTINUING --\n");
            sb.Append(string.Join("\n\n", warnings.Select(s => "[!] " + s)));
        }
        if (notes.Count > 0)
        {
            sb.Append("\n\n-- GOOD TO KNOW --\n");
            sb.Append(string.Join("\n\n", notes.Select(s => "[i] " + s)));
        }

        sb.Append("\n\nProceed?");
        return sb.ToString();
    }

    [RelayCommand]
    private async Task ApplyPatch()
    {
        if (_preflight is null)
        {
            // Should never happen because the button is disabled until preflight finishes,
            // but if it does we'd rather refuse than dereference null and crash.
            Log("Preflight not yet complete. Waiting before apply.", "WARNING");
            return;
        }

        if (_preflight.VeraCryptDetected)
        {
            Log("[ERROR] BLOCKED: VeraCrypt system encryption detected", "ERROR");
            InfoDialog?.Invoke("VeraCrypt Incompatibility",
                "CANNOT APPLY PATCH\n\nVeraCrypt system encryption detected. Enabling the native NVMe driver (nvmedisk.sys) breaks VeraCrypt boot entirely.\n\nThis block cannot be overridden.",
                DialogIcon.Error);
            return;
        }

        SyncConfigFromUI();

        if (!Config.SkipWarnings)
        {
            var msg = BuildConfirmMessage("Apply Patch");
            if (ConfirmDialog?.Invoke("Apply Patch", msg) != true) return;
        }

        ButtonsEnabled = false;
        ApplyEnabled = false;

        var result = await Task.Run(() => PatchService.Install(
            Config,
            _preflight.BitLockerEnabled,
            _preflight.VeraCryptDetected,
            _preflight.NativeNVMeStatus,
            _preflight.BypassIOStatus,
            msg => Log(msg),
            (val, text) => Application.Current?.Dispatcher.BeginInvoke(() => SetProgress(val, text))));

        Application.Current?.Dispatcher.Invoke(() =>
        {
            LogBeforeAfter(result.BeforeSnapshot, result.AfterSnapshot, "Install Patch");
            UpdateRegistryDisplay();
            UpdateStatusDisplay();
            UpdateOverviewSummary();
            UpdateOperationalHistory();
            ButtonsEnabled = true;
            ApplyEnabled = PreflightService.AllCriticalPassed(_preflight.Checks) && !_preflight.VeraCryptDetected;

            if (result.Success)
            {
                ToastService.Show("NVMe Patch Applied", "All components applied. Restart required.", ToastType.Success, Config.EnableToasts);
                var verificationScriptPath = RecoveryKitService.GenerateVerificationScript(Config.WorkingDir, Config.IncludeServerKey);
                if (verificationScriptPath is not null)
                    Config.LastVerificationScriptPath = verificationScriptPath;

                try
                {
                    var recoveryKitPath = RecoveryKitService.Export(Config.WorkingDir);
                    if (recoveryKitPath is not null)
                        Config.LastRecoveryKitPath = recoveryKitPath;
                }
                catch { }
                // Mark pending — next launch (after reboot) will confirm nvmedisk actually
                // bound, so we can surface a clear message if Microsoft's block neutered
                // the override on this build.
                PatchVerificationService.MarkPending(Config);
                ConfigService.Save(Config);
                UpdateOperationalHistory();

                // Offer restart
                var restartMsg = $"Patch applied successfully ({result.AppliedCount}/{result.TotalExpected} components).\n\n" +
                    "Restart your computer now to enable the new NVMe driver?\n\n" +
                    $"(System will restart in {Config.RestartDelay} seconds if you click Yes)\n\n" +
                    "After reboot:\n- Drives will move from 'Disk drives' to 'Storage disks'\n" +
                    "- Driver changes from stornvme.sys to nvmedisk.sys\n" +
                    "- A recovery kit has been saved (copy to USB for safety)";

                if (ConfirmDialog?.Invoke("Installation Complete", restartMsg) == true)
                {
                    Log($"Initiating system restart in {Config.RestartDelay} seconds...");
                    if (!PatchService.InitiateRestart(Config.RestartDelay, m => Log(m, "ERROR")))
                    {
                        InfoDialog?.Invoke("Restart Could Not Be Scheduled",
                            "Windows did not accept the restart request. Save your work and use Start > Power > Restart manually.",
                            DialogIcon.Warning);
                    }
                }
            }
            else if (result.WasRolledBack)
            {
                if (result.RollbackFullyReversed)
                {
                    ToastService.Show("NVMe Patch Failed", "Changes rolled back cleanly.", ToastType.Warning, Config.EnableToasts);
                }
                else
                {
                    // Rollback itself failed — some keys remained set. We cannot claim the
                    // system is in a clean state. Point the user at the safety net instead of
                    // pretending everything is fine.
                    ToastService.Show("Rollback Incomplete", "Some writes could not be reversed. See the dialog.", ToastType.Error, Config.EnableToasts);
                    InfoDialog?.Invoke("Rollback Incomplete",
                        "The patch failed partway through and the automatic rollback could not reverse every change.\n\n" +
                        "The system may still have some of the feature flags or Safe Boot entries set. To return to a fully clean state:\n\n" +
                        $"  1. Double-click the pre-patch registry backup in {Config.WorkingDir} to re-import the previous values, OR\n" +
                        "  2. Use System Restore to roll back to the checkpoint created before this attempt.\n\n" +
                        "Your activity log shows which specific keys could not be removed.",
                        DialogIcon.Error);
                }
            }
        });
    }

    [RelayCommand]
    private async Task RemovePatch()
    {
        SyncConfigFromUI();

        if (!Config.SkipWarnings)
        {
            var msg = BuildConfirmMessage("Remove Patch");
            if (ConfirmDialog?.Invoke("Remove Patch", msg) != true) return;
        }

        ButtonsEnabled = false;
        RemoveEnabled = false;

        var result = await Task.Run(() => PatchService.Uninstall(
            Config,
            _preflight?.NativeNVMeStatus,
            _preflight?.BypassIOStatus,
            msg => Log(msg),
            (val, text) => Application.Current?.Dispatcher.BeginInvoke(() => SetProgress(val, text))));

        Application.Current?.Dispatcher.Invoke(() =>
        {
            LogBeforeAfter(result.BeforeSnapshot, result.AfterSnapshot, "Remove Patch");
            UpdateRegistryDisplay();
            UpdateStatusDisplay();
            UpdateOverviewSummary();
            UpdateOperationalHistory();
            ButtonsEnabled = true;

            if (result.Success)
            {
                ToastService.Show("NVMe Patch Removed", "Patch components removed. Restart required.", ToastType.Info, Config.EnableToasts);

                var restartMsg = $"Patch removed successfully ({result.AppliedCount} component(s)).\n\n" +
                    "Restart your computer now to restore the original NVMe driver?\n\n" +
                    "After reboot: Drives will return to 'Disk drives' using stornvme.sys";

                if (ConfirmDialog?.Invoke("Removal Complete", restartMsg) == true)
                {
                    Log($"Initiating system restart in {Config.RestartDelay} seconds...");
                    if (!PatchService.InitiateRestart(Config.RestartDelay, m => Log(m, "ERROR")))
                    {
                        InfoDialog?.Invoke("Restart Could Not Be Scheduled",
                            "Windows did not accept the restart request. Save your work and use Start > Power > Restart manually.",
                            DialogIcon.Warning);
                    }
                }
            }
        });
    }

    [RelayCommand]
    private async Task RunBackup()
    {
        ButtonsEnabled = false;
        Log("Creating system backup...");
        var backupFile = await Task.Run(() => RegistryService.ExportRegistryBackup(Config.WorkingDir, "Manual_Backup"));
        if (backupFile is not null)
        {
            Log($"Registry backup saved: {backupFile}", "SUCCESS");
            try
            {
                var nativeStatus = _preflight?.NativeNVMeStatus ?? DriveService.TestNativeNVMeActive();
                var bypassStatus = _preflight?.BypassIOStatus ?? DriveService.GetBypassIOStatus();
                var snapshot = RegistryService.GetPatchSnapshot(nativeStatus, bypassStatus);
                DataService.SaveSnapshot(snapshot, "Manual registry backup", isPrePatch: !snapshot.Status.Applied);
            }
            catch { }
            UpdateOperationalHistory();
        }
        else
            Log("Failed to create backup", "ERROR");
        ButtonsEnabled = true;
    }

    [RelayCommand]
    private async Task RunBenchmark()
    {
        ButtonsEnabled = false;
        var status = RegistryService.GetPatchStatus();
        string label = status.Applied ? "Post-Patch" : "Pre-Patch";
        Log($"Starting storage benchmark ({label})...");
        Log("This will take approximately 60 seconds. Do not use disk-heavy apps.", "WARNING");

        var result = await BenchmarkService.RunBenchmarkAsync(
            Config.WorkingDir, label,
            msg => Log(msg),
            (val, text) => Application.Current?.Dispatcher.BeginInvoke(() => SetProgress(val, text)));

        if (result is not null)
        {
            BenchmarkService.SaveResults(Config.WorkingDir, result);
            Log("");
            Log("============ BENCHMARK RESULTS ============");
            Log($"  {label} @ {DateTime.Now:HH:mm:ss}");
            Log($"  4K Random Read:  {result.Read.IOPS} IOPS  |  {result.Read.ThroughputMBs} MB/s  |  {result.Read.AvgLatencyMs} ms avg", "SUCCESS");
            Log($"  4K Random Write: {result.Write.IOPS} IOPS  |  {result.Write.ThroughputMBs} MB/s  |  {result.Write.AvgLatencyMs} ms avg", "SUCCESS");

            // Compare with previous
            var history = BenchmarkService.GetHistory(Config.WorkingDir);
            var prev = history.Where(h => h.Label != label).LastOrDefault();
            if (prev?.Read.IOPS > 0)
            {
                var readDelta = prev.Read.IOPS > 0 ? Math.Round((result.Read.IOPS - prev.Read.IOPS) / prev.Read.IOPS * 100, 1) : 0;
                var writeDelta = prev.Write.IOPS > 0 ? Math.Round((result.Write.IOPS - prev.Write.IOPS) / prev.Write.IOPS * 100, 1) : 0;
                Log("");
                Log($"  --- vs. Previous ({prev.Label}) ---");
                Log($"  Read IOPS:  {prev.Read.IOPS} --> {result.Read.IOPS} ({(readDelta >= 0 ? "+" : "")}{readDelta}%)", readDelta >= 0 ? "SUCCESS" : "WARNING");
                Log($"  Write IOPS: {prev.Write.IOPS} --> {result.Write.IOPS} ({(writeDelta >= 0 ? "+" : "")}{writeDelta}%)", writeDelta >= 0 ? "SUCCESS" : "WARNING");
            }
            Log("===========================================");

            BenchLabelText = $"Last bench: {result.Read.IOPS} IOPS read / {result.Write.IOPS} IOPS write ({label})";
            BenchLabelVisible = true;
            UpdateOverviewSummary();
            UpdateOperationalHistory();
        }
        ButtonsEnabled = true;
    }

    [RelayCommand]
    private async Task ExportDiagnostics()
    {
        var path = await DiagnosticsService.ExportAsync(Config.WorkingDir, _preflight, SnapshotLogHistory());
        if (path is not null)
        {
            Config.LastDiagnosticsPath = path;
            ConfigService.Save(Config);
            UpdateOperationalHistory();
            Log($"Diagnostics exported: {path}", "SUCCESS");
            InfoDialog?.Invoke("Export Complete", $"Diagnostics exported to:\n{path}", DialogIcon.Information);
        }
    }

    // ViVeTool fallback — downloads ViVeTool from its official GitHub release, caches it in
    // <workingDir>\tools\, then runs it with the two feature IDs the community adopted after
    // Microsoft's Feb/Mar 2026 block on the FeatureManagement\Overrides route.
    // Called both from the post-reboot OverrideBlocked dialog and from a persistent badge so
    // the user can retry without quitting the app.
    [RelayCommand]
    private async Task ApplyViVeToolFallback()
    {
        ButtonsEnabled = false;
        try
        {
            Log("========================================");
            Log("Applying ViVeTool fallback");
            Log("========================================");
            var result = await ViVeToolService.ApplyFallbackAsync(Config.WorkingDir, msg => Log(msg));
            if (!result.Success)
            {
                Log($"[ERROR] ViVeTool fallback failed: {result.Message}", "ERROR");
                InfoDialog?.Invoke("ViVeTool Fallback Failed",
                    "The fallback could not be applied:\n\n" + result.Message +
                    "\n\nYour registry backup, restore point, and recovery kit from the original patch are still in place. " +
                    "You can remove the patch at any time or retry the fallback later (the app will remember the block state until you do).",
                    DialogIcon.Error);
                return;
            }

            Log($"[SUCCESS] ViVeTool fallback applied: {string.Join(", ", result.AppliedIDs)}", "SUCCESS");
            ShowViVeToolFallbackBadge = false;
            ToastService.Show("ViVeTool Fallback Applied",
                "Fallback feature IDs written. Restart to activate the native NVMe driver.",
                ToastType.Success, Config.EnableToasts);
            // Reuse the verification pipeline — the fallback is just a different way of
            // asking Windows to swap the driver, so the same post-reboot check applies.
            PatchVerificationService.MarkPending(Config);
            ConfigService.Save(Config);

            var restartMsg =
                $"ViVeTool wrote feature IDs {string.Join(" and ", result.AppliedIDs)}.\n\n" +
                "Restart now to let Windows pick up the native NVMe driver?\n\n" +
                $"(System will restart in {Config.RestartDelay} seconds if you click Yes.)";
            if (ConfirmDialog?.Invoke("Fallback Applied", restartMsg) == true)
            {
                if (!PatchService.InitiateRestart(Config.RestartDelay, m => Log(m, "ERROR")))
                {
                    InfoDialog?.Invoke("Restart Could Not Be Scheduled",
                        "Windows did not accept the restart request. Save your work and restart manually.",
                        DialogIcon.Warning);
                }
            }
        }
        finally
        {
            ButtonsEnabled = true;
        }
    }

    [RelayCommand]
    private async Task ExportSupportBundle()
    {
        Log("Generating support bundle (zip)...", "INFO");
        var path = await DiagnosticsService.ExportBundleAsync(
            Config.WorkingDir,
            _preflight,
            SnapshotLogHistory(),
            Config.ConfigFile);
        if (path is not null)
        {
            Config.LastDiagnosticsPath = path;
            ConfigService.Save(Config);
            UpdateOperationalHistory();
            Log($"Support bundle exported: {path}", "SUCCESS");
            InfoDialog?.Invoke("Support Bundle Created",
                $"A shareable support bundle was saved to:\n{path}\n\nContains: diagnostics report, config, crash logs, recent registry backups, and the SQLite DB.",
                DialogIcon.Information);
        }
        else
        {
            Log("Support bundle export failed", "ERROR");
            InfoDialog?.Invoke("Export Failed",
                "Could not create support bundle. Check that the working directory is writable.",
                DialogIcon.Error);
        }
    }

    [RelayCommand]
    private void ExportRecoveryKit()
    {
        var existingKitPath = ResolveRecoveryKitPath();
        using var fbd = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder to save Recovery Kit (e.g., USB drive)",
            ShowNewFolderButton = true,
            SelectedPath = existingKitPath is not null
                ? Directory.GetParent(existingKitPath)?.FullName ?? existingKitPath
                : Config.WorkingDir
        };
        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var kitDir = RecoveryKitService.Export(fbd.SelectedPath, msg => Log(msg));
            if (kitDir is not null)
            {
                Config.LastRecoveryKitPath = kitDir;
                ConfigService.Save(Config);
                UpdateOperationalHistory();
                Log($"Recovery kit saved: {kitDir}", "SUCCESS");
                InfoDialog?.Invoke("Recovery Kit Created",
                    $"Recovery kit saved to:\n{kitDir}\n\nCopy this folder to a USB drive for offline recovery.\nContains .reg file, .bat script, and README.",
                    DialogIcon.Information);
            }
        }
    }

    [RelayCommand]
    private void CopyLog()
    {
        var snapshot = SnapshotLogHistory();
        if (snapshot.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join("\r\n", snapshot));
            Log("Log copied to clipboard", "SUCCESS");
        }
        catch (Exception ex)
        {
            // Clipboard occasionally throws COMException when another app holds the lock.
            Log($"Failed to copy log: {ex.Message}", "ERROR");
        }
    }

    [RelayCommand]
    private void ExportLog()
    {
        var snapshot = SnapshotLogHistory();
        if (snapshot.Count == 0) return;
        try
        {
            if (!Directory.Exists(Config.WorkingDir))
                Directory.CreateDirectory(Config.WorkingDir);
            var path = Path.Combine(Config.WorkingDir, $"NVMe_Patcher_Log_{DateTime.Now:yyyyMMdd_HHmmss}_manual.txt");
            File.WriteAllLines(path, snapshot);
            Log($"Log exported: {path}", "SUCCESS");
            InfoDialog?.Invoke("Log Exported", $"Log saved to:\n{path}", DialogIcon.Information);
        }
        catch (Exception ex)
        {
            Log($"Failed to export log: {ex.Message}", "ERROR");
            InfoDialog?.Invoke("Log Export Failed",
                $"The log could not be saved:\n{ex.Message}",
                DialogIcon.Error);
        }
    }

    private int _refreshInFlight; // 0 = idle, 1 = a refresh is in flight

    [RelayCommand]
    private async Task Refresh()
    {
        // Block re-entrant Refresh: clicking the button repeatedly was kicking off concurrent
        // PreflightService.RunAllAsync executions that fought over the WMI queries and the
        // _preflight field. Only one refresh runs at a time.
        if (System.Threading.Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            Log("Refresh already in progress.", "WARNING");
            return;
        }
        try
        {
            Log("----------------------------------------");
            Log("Refreshing system checks...");
            await RunPreflightAsync();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    [RelayCommand]
    private void ToggleSettings() => SettingsPanelVisible = !SettingsPanelVisible;

    [RelayCommand]
    private void UseRecommendedSetup()
    {
        IncludeServerKey = true;
        SkipWarnings = false;
        AutoSaveLog = true;
        EnableToasts = true;
        WriteEventLog = true;
        RestartDelayText = "30";

        Log("Recommended setup restored: server key on, confirmations on, auditing on, notifications on, 30-second restart countdown.", "SUCCESS");
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        // Make sure the folder exists before asking Explorer to open it — otherwise users
        // get a confusing "Windows can't find <path>" shell error rather than a clear message.
        try
        {
            if (string.IsNullOrEmpty(Config.WorkingDir))
            {
                InfoDialog?.Invoke("Working Folder Unknown",
                    "The working folder path is not set. Try restarting the app.",
                    DialogIcon.Warning);
                return;
            }
            if (!Directory.Exists(Config.WorkingDir))
                Directory.CreateDirectory(Config.WorkingDir);
            Process.Start("explorer.exe", Config.WorkingDir);
        }
        catch (Exception ex)
        {
            Log($"Failed to open working folder: {ex.Message}", "ERROR");
            InfoDialog?.Invoke("Could Not Open Folder",
                $"Windows refused to open the working folder:\n{Config.WorkingDir}\n\n{ex.Message}",
                DialogIcon.Error);
        }
    }

    [RelayCommand]
    private void RefreshRecoveryAssets()
    {
        UpdateOperationalHistory();
    }

    [RelayCommand]
    private void OpenLatestRecoveryKit()
    {
        var recoveryKitPath = ResolveRecoveryKitPath();
        if (string.IsNullOrWhiteSpace(recoveryKitPath))
        {
            InfoDialog?.Invoke("Recovery Kit Missing",
                "No recovery kit was found yet. Create one first so rollback files are available outside the main workflow.",
                DialogIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = recoveryKitPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"Failed to open recovery kit folder: {ex.Message}", "ERROR");
            InfoDialog?.Invoke("Could Not Open Recovery Kit",
                $"Windows refused to open:\n{recoveryKitPath}\n\n{ex.Message}",
                DialogIcon.Error);
        }
    }

    [RelayCommand]
    private void OpenVerificationScript()
    {
        var verificationScriptPath = ResolveVerificationScriptPath();
        if (string.IsNullOrWhiteSpace(verificationScriptPath))
        {
            InfoDialog?.Invoke("Verification Script Missing",
                "No verification script was found yet. Generate one first so post-reboot validation stays simple and repeatable.",
                DialogIcon.Warning);
            return;
        }

        try
        {
            // notepad ships with every Windows install, but a hardened SKU can have it stripped.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{verificationScriptPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"Failed to open verification script: {ex.Message}", "ERROR");
            InfoDialog?.Invoke("Could Not Open Verification Script",
                $"Windows refused to open:\n{verificationScriptPath}\n\n{ex.Message}",
                DialogIcon.Error);
        }
    }

    [RelayCommand]
    private void OpenLatestDiagnosticsReport()
    {
        var diagnosticsReportPath = ResolveLatestDiagnosticsReportPath();
        if (string.IsNullOrWhiteSpace(diagnosticsReportPath))
        {
            InfoDialog?.Invoke("Diagnostics Report Missing",
                "No diagnostics report was found yet. Export one to capture the system state before you need support or rollback context.",
                DialogIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{diagnosticsReportPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"Failed to open diagnostics report: {ex.Message}", "ERROR");
            InfoDialog?.Invoke("Could Not Open Diagnostics Report",
                $"Windows refused to open:\n{diagnosticsReportPath}\n\n{ex.Message}",
                DialogIcon.Error);
        }
    }

    [RelayCommand]
    private void GenerateVerificationScript()
    {
        SyncConfigFromUI();
        var verificationScriptPath = RecoveryKitService.GenerateVerificationScript(Config.WorkingDir, Config.IncludeServerKey);
        if (verificationScriptPath is null)
        {
            Log("Failed to generate verification script.", "ERROR");
            InfoDialog?.Invoke("Verification Script Failed",
                "The verification script could not be generated in the working folder.",
                DialogIcon.Error);
            return;
        }

        Config.LastVerificationScriptPath = verificationScriptPath;
        ConfigService.Save(Config);
        UpdateOperationalHistory();
        Log($"Verification script saved: {verificationScriptPath}", "SUCCESS");
        InfoDialog?.Invoke("Verification Script Ready",
            $"Verification script saved to:\n{verificationScriptPath}\n\nRun it after reboot to confirm the patch keys and Safe Mode protections are still present.",
            DialogIcon.Information);
    }

    [RelayCommand]
    private void OpenUpdateUrl()
    {
        if (string.IsNullOrEmpty(UpdateUrl)) return;
        OpenUrlInBrowser(UpdateUrl);
    }

    [RelayCommand]
    private void OpenGitHub() => OpenUrlInBrowser(AppConfig.GitHubURL);

    [RelayCommand]
    private void OpenDocs() => OpenUrlInBrowser(AppConfig.DocumentationURL);

    private void OpenUrlInBrowser(string url)
    {
        // Defense: only allow http(s) URLs. With UseShellExecute=true any "file:" or custom scheme
        // would be honored by Windows — this keeps the surface to plain web links only.
        if (string.IsNullOrEmpty(url) ||
            !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
              url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            Log($"Refusing to open non-HTTP URL: {url}", "WARNING");
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

    public void SyncConfigFromUI()
    {
        Config.IncludeServerKey = IncludeServerKey;
        Config.SkipWarnings = SkipWarnings;
        Config.AutoSaveLog = AutoSaveLog;
        Config.EnableToasts = EnableToasts;
        Config.WriteEventLog = WriteEventLog;
        // The setter on AppConfig.RestartDelay clamps to 0..3600. We additionally enforce the
        // UI's documented 5..300s range here so an invalid free-text entry never silently
        // becomes a 3600-second restart countdown.
        if (int.TryParse(RestartDelayText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int delay)
            && delay >= 5 && delay <= 300)
        {
            Config.RestartDelay = delay;
        }
        else
        {
            // Reset visible field to the last valid value so the user sees what was kept.
            RestartDelayText = Config.RestartDelay.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public void OnClosing()
    {
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

    private void UpdateActivitySummary()
    {
        if (LogEntryCount == 0)
        {
            ActivitySummaryText = "Activity entries will appear here as checks and actions run.";
        }
        else if (LogErrorCount > 0)
        {
            ActivitySummaryText = $"{LogEntryCount} session {Pluralize(LogEntryCount, "entry")} captured with {LogErrorCount} {Pluralize(LogErrorCount, "error")} and {LogWarningCount} {Pluralize(LogWarningCount, "warning")}.";
        }
        else if (LogWarningCount > 0)
        {
            ActivitySummaryText = $"{LogEntryCount} session {Pluralize(LogEntryCount, "entry")} captured with advisory signals but no hard errors.";
        }
        else
        {
            ActivitySummaryText = $"{LogEntryCount} session {Pluralize(LogEntryCount, "entry")} captured so far with a clean audit trail.";
        }

        var retentionParts = new List<string>
        {
            AutoSaveLog ? "Auto-save on close is enabled." : "Auto-save on close is off."
        };

        retentionParts.Add(WriteEventLog
            ? "Windows Event Log auditing is enabled."
            : "Windows Event Log auditing is off.");
        retentionParts.Add("Manual export writes a point-in-time copy to the working folder.");

        LogRetentionText = string.Join(" ", retentionParts);
        UpdateWorkspaceBadges();
    }

    private void UpdateWorkspaceBadges()
    {
        if (LogErrorCount > 0)
        {
            ActivityTabBadgeText = $"{LogErrorCount} {Pluralize(LogErrorCount, "issue")}";
            ActivityTabBadgeColor = "#FFEF4444";
        }
        else if (LogWarningCount > 0)
        {
            ActivityTabBadgeText = $"{LogWarningCount} {Pluralize(LogWarningCount, "warning")}";
            ActivityTabBadgeColor = "#FFF59E0B";
        }
        else if (LogEntryCount > 0)
        {
            ActivityTabBadgeText = "Live";
            ActivityTabBadgeColor = "#FF22C55E";
        }
        else
        {
            ActivityTabBadgeText = "Idle";
            ActivityTabBadgeColor = "#FF71717A";
        }

        if (BenchmarkRunCount > 0)
        {
            BenchmarkTabBadgeText = $"{BenchmarkRunCount} {Pluralize(BenchmarkRunCount, "run")}";
            BenchmarkTabBadgeColor = "#FF60A5FA";
        }
        else
        {
            BenchmarkTabBadgeText = "New";
            BenchmarkTabBadgeColor = "#FF71717A";
        }

        if (NvmeDriveCount > 0)
        {
            TelemetryTabBadgeText = $"{NvmeDriveCount} {Pluralize(NvmeDriveCount, "drive")}";
            TelemetryTabBadgeColor = "#FF60A5FA";
        }
        else if (HasDriveData && TotalDriveCount > 0)
        {
            TelemetryTabBadgeText = "No NVMe";
            TelemetryTabBadgeColor = "#FFF59E0B";
        }
        else
        {
            TelemetryTabBadgeText = "Waiting";
            TelemetryTabBadgeColor = "#FF71717A";
        }

        RecoveryMissingAssetCount = new[] { HasRecoveryKit, HasVerificationScript, HasDiagnosticsReport }.Count(ready => !ready);
        if (RecoveryMissingAssetCount == 0)
        {
            RecoveryTabBadgeText = "Ready";
            RecoveryTabBadgeColor = "#FF22C55E";
        }
        else
        {
            RecoveryTabBadgeText = $"{RecoveryMissingAssetCount} missing";
            RecoveryTabBadgeColor = "#FFF59E0B";
        }
    }

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
}

public class PreflightCheckVM
{
    public string Label { get; set; } = "";
    public CheckStatus Status { get; set; }
    public string Message { get; set; } = "";
    public string? Tooltip { get; set; }

    public string Color => Status switch
    {
        CheckStatus.Pass => "#FF22c55e",
        CheckStatus.Warning => "#FFf59e0b",
        CheckStatus.Fail => "#FFef4444",
        CheckStatus.Info => "#FF3b82f6",
        _ => "#FF71717a"
    };
}

public class RegistryFlagVM
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsSet { get; set; }
    public bool IsOptional { get; set; }

    public string DotColor => IsSet ? "#FF22c55e" : IsOptional ? "#FF71717a" : "#FFef4444";
}

public class AttentionNoteVM
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string ToneColor { get; set; } = "#FFF59E0B";
}

public class ChangePlanStepVM
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string ToneColor { get; set; } = "#FF60A5FA";
}

public class DriveRowVM
{
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public string BusType { get; set; } = "";
    public bool IsNVMe { get; set; }
    public bool IsBoot { get; set; }
    public string Temperature { get; set; } = "N/A";
    public string Wear { get; set; } = "N/A";
    public string SmartTooltip { get; set; } = "";
    public string Firmware { get; set; } = "";
    public bool ShowFirmware => !string.IsNullOrEmpty(Firmware) && IsNVMe;
    public bool IsNativeDrive { get; set; }
    public bool ShowDriverBadge { get; set; }

    public string DotColor => IsNVMe ? "#FF22c55e" : "#FF52525b";
    public string BusPillBg => IsNVMe ? "#FF0c2d5e" : "#FF18181b";
    public string BusPillFg => IsNVMe ? "#FF3b82f6" : "#FF71717a";
    public bool ShowTemp => Temperature != "N/A" && IsNVMe;
    public bool ShowWear => Wear != "N/A" && IsNVMe;
    public string TempColor
    {
        get
        {
            if (int.TryParse(System.Text.RegularExpressions.Regex.Replace(Temperature, @"[^0-9]", ""), out int val))
                return val >= 70 ? "#FFef4444" : val >= 50 ? "#FFf59e0b" : "#FF22c55e";
            return "#FF22c55e";
        }
    }
    public string WearColor
    {
        get
        {
            if (int.TryParse(System.Text.RegularExpressions.Regex.Replace(Wear, @"[^0-9]", ""), out int val))
                return val <= 20 ? "#FFef4444" : val <= 50 ? "#FFf59e0b" : "#FF22c55e";
            return "#FF22c55e";
        }
    }
    public string DriverBadgeText => IsNativeDrive ? "NATIVE" : "LEGACY";
    public string DriverBadgeBg => IsNativeDrive ? "#FF0a2e1a" : "#FF2a1a12";
    public string DriverBadgeFg => IsNativeDrive ? "#FF22c55e" : "#FFf59e0b";
}
