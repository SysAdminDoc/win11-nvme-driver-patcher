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
    [ObservableProperty] private string _applyButtonText = "APPLY PATCH";
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
    [ObservableProperty] private string _latestActivityText = "No activity recorded yet.";
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
    [ObservableProperty] private string _removeButtonTooltipText = "Nothing is staged to remove yet.";
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
    [ObservableProperty] private string _backupHistoryText = "No registry backups saved in the working folder yet.";
    [ObservableProperty] private string _snapshotHistoryText = "No change snapshots recorded yet.";
    [ObservableProperty] private string _benchmarkHistoryText = "No benchmark history recorded yet.";
    [ObservableProperty] private string _recoveryKitStatusText = "No local recovery kit has been prepared yet.";
    [ObservableProperty] private string _verificationScriptStatusText = "No verification script is available yet.";
    [ObservableProperty] private string _diagnosticsReportStatusText = "No diagnostics report has been exported yet.";
    [ObservableProperty] private string _recoveryWorkspaceSummaryText = "Generate rollback and verification assets so the system can be reversed or confirmed without guesswork.";
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

    public MainViewModel()
    {
        Config = ConfigService.Load();
        VersionText = $"v{AppConfig.AppVersion}";
        IncludeServerKey = Config.IncludeServerKey;
        SkipWarnings = Config.SkipWarnings;
        AutoSaveLog = Config.AutoSaveLog;
        EnableToasts = Config.EnableToasts;
        WriteEventLog = Config.WriteEventLog;
        RestartDelayText = Config.RestartDelay.ToString();
        UpdateOptionsSummary();
        UpdatePreferenceSummary();
        UpdateOperationalHistory();
        UpdateActivitySummary();
        UpdateWorkspaceBadges();
    }

    public void Log(string message, string level = "INFO")
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"[{timestamp}] [{level}] {message}";
        _logHistory.Add(entry);

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
        _logHistory.Clear();
        LogEntryCount = 0;
        LogSuccessCount = 0;
        LogWarningCount = 0;
        LogErrorCount = 0;
        LatestActivityText = "Log cleared. New activity will appear here.";
        UpdateActivitySummary();
        OnPropertyChanged(nameof(LogText));
    }

    public async Task RunPreflightAsync()
    {
        ResetOverviewState();
        IsLoading = true;
        ButtonsEnabled = false;
        Log($"{AppConfig.AppName} v{AppConfig.AppVersion} started");
        Log($"Working directory: {Config.WorkingDir}");
        Log("----------------------------------------");
        Log("Running pre-flight checks...");

        EventLogService.Initialize(Config.WriteEventLog);
        EventLogService.Write($"{AppConfig.AppName} v{AppConfig.AppVersion} started");

        _preflight = await PreflightService.RunAllAsync(msg => Log(msg, "DEBUG"));

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
            StatusText = "Patch Applied";
            StatusColor = "#FF22c55e";
            ApplyButtonText = "REINSTALL";
            RemoveEnabled = true;
        }
        else if (status.Partial)
        {
            StatusText = $"Partial ({status.Count}/{status.Total})";
            StatusColor = "#FFf59e0b";
            ApplyButtonText = "REPAIR PATCH";
            RemoveEnabled = true;
        }
        else
        {
            StatusText = "Not Applied";
            StatusColor = "#FF71717a";
            ApplyButtonText = "APPLY PATCH";
            RemoveEnabled = false;
        }

        if (_preflight?.NativeNVMeStatus?.IsActive == true)
            DriverLabelText = "Active: nvmedisk.sys (Native NVMe)";
        else if (_preflight?.DriverInfo is not null)
            DriverLabelText = $"Driver: {_preflight.DriverInfo.CurrentDriver}";
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
        RemoveButtonTooltipText = "Nothing is staged to remove yet.";
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
                    BackupHistoryText = "No registry backups saved in the working folder yet.";
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
                SnapshotHistoryText = "No change snapshots recorded yet.";
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
                BenchmarkHistoryText = "No benchmark history recorded yet.";
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
                RecoveryKitStatusText = "No local recovery kit has been prepared yet.";
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
                VerificationScriptStatusText = "No verification script is available yet.";
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
                DiagnosticsReportStatusText = "No diagnostics report has been exported yet.";
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

        var localKitPath = Path.Combine(Config.WorkingDir, "NVMe_Recovery_Kit");
        return Directory.Exists(localKitPath) ? localKitPath : null;
    }

    private string? ResolveVerificationScriptPath()
    {
        if (!string.IsNullOrWhiteSpace(Config.LastVerificationScriptPath) && File.Exists(Config.LastVerificationScriptPath))
            return Config.LastVerificationScriptPath;

        var localScriptPath = Path.Combine(Config.WorkingDir, "Verify_NVMe_Patch.ps1");
        return File.Exists(localScriptPath) ? localScriptPath : null;
    }

    private string? ResolveLatestDiagnosticsReportPath()
    {
        if (!string.IsNullOrWhiteSpace(Config.LastDiagnosticsPath) && File.Exists(Config.LastDiagnosticsPath))
            return Config.LastDiagnosticsPath;

        if (!Directory.Exists(Config.WorkingDir))
            return null;

        return Directory.GetFiles(Config.WorkingDir, "NVMe_Diagnostics_*.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
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
            RemoveButtonTooltipText = "Nothing is staged to remove yet.";
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
                : "Nothing is staged to remove yet.";
            return;
        }

        if (_preflight.NativeNVMeStatus?.IsActive == true && !status.Applied && !status.Partial)
        {
            ActionReadinessText = "Native NVMe is already active. Apply is optional here and mainly helps stage the registry keys, Safe Mode protections, and recovery material for consistency.";
            ActionReadinessColor = "#FF22C55E";
            ApplyButtonTooltipText = $"Apply will stage {plannedComponentCount} patch components plus recovery helpers, even though the native driver path is already live.";
            RemoveButtonTooltipText = "Nothing is staged to remove yet.";
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
            RemoveButtonTooltipText = "Nothing is staged to remove yet.";
            return;
        }

        ActionReadinessText = $"Apply is ready. It will stage {plannedComponentCount} patch components, add Safe Mode protections, save snapshots, and require a reboot before the live driver path changes.";
        ActionReadinessColor = "#FF22C55E";
        ApplyButtonTooltipText = ActionReadinessText;
        RemoveButtonTooltipText = "Nothing is staged to remove yet.";
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
            ? "Server 2025 compatibility key enabled for fuller scheduler activation."
            : "Server 2025 compatibility key is still off, even though it is usually the recommended patch stance.";
        var warningsText = SkipWarnings
            ? "Expert warning confirmations are reduced."
            : "Confirmation warnings stay on for safer review.";

        OptionsSummaryText = $"{serverKeyText} {warningsText}";
    }

    private void UpdatePreferenceSummary()
    {
        string restartSummary = int.TryParse(RestartDelayText, out int delay) && delay >= 5 && delay <= 300
            ? $"Restart countdown is set to {delay} seconds."
            : "Restart countdown needs a number between 5 and 300 seconds.";

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

    partial void OnIncludeServerKeyChanged(bool value)
    {
        UpdateOptionsSummary();
        UpdateChangePlan();
    }

    partial void OnSkipWarningsChanged(bool value)
    {
        UpdateOptionsSummary();
        UpdateChangePlan();
    }

    partial void OnAutoSaveLogChanged(bool value)
    {
        UpdateActivitySummary();
        UpdatePreferenceSummary();
    }

    partial void OnEnableToastsChanged(bool value) => UpdatePreferenceSummary();

    partial void OnWriteEventLogChanged(bool value)
    {
        UpdateActivitySummary();
        UpdatePreferenceSummary();
    }

    partial void OnRestartDelayTextChanged(string value) => UpdatePreferenceSummary();
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
                var raw = File.ReadAllText(stateFile);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                bool lastApplied = root.TryGetProperty("Applied", out var ap) && ap.GetBoolean();
                bool lastNativeActive = root.TryGetProperty("NativeActive", out var na) && na.GetBoolean();

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

            File.WriteAllText(stateFile, JsonSerializer.Serialize(currentState));
        }
        catch { }
    }

    private string BuildConfirmMessage(string title)
    {
        var warnings = new List<string>();

        if (title == "Apply Patch" && !_preflight!.HasNVMeDrives)
            warnings.Add("[!] NO NVMe DRIVES DETECTED - This patch only affects NVMe drives using the Windows inbox driver.");
        if (_preflight!.BitLockerEnabled)
            warnings.Add("[!] BITLOCKER ACTIVE - Will be automatically suspended for one reboot to prevent recovery key prompt.");
        foreach (var sw in _preflight.IncompatibleSoftware.Where(s => s.Severity != "Critical"))
            warnings.Add($"[i] {sw.Name}: {sw.Message}");
        if (_preflight.DriverInfo?.HasThirdParty == true)
            warnings.Add($"[i] THIRD-PARTY DRIVER: {_preflight.DriverInfo.ThirdPartyName} - May have no effect.");
        if (title == "Apply Patch")
        {
            var ssdTools = _preflight.IncompatibleSoftware.Where(s => s.Message.Contains("SCSI pass-through")).ToList();
            if (ssdTools.Count > 0)
                warnings.Add($"[i] SSD TOOLS: {string.Join(", ", ssdTools.Select(s => s.Name))} will not detect drives after patching.");
            if (_preflight.BuildDetails is { Is24H2OrLater: false })
                warnings.Add($"[!] OLDER BUILD: {_preflight.BuildDetails.DisplayVersion} - Designed for 24H2+. Results may be unpredictable.");
            if (_preflight.IsLaptop)
                warnings.Add("[!] LAPTOP DETECTED: Native NVMe breaks APST power management. Expect ~15% battery life reduction.");
            warnings.Add("[i] GAMING NOTE: Native NVMe does not support BypassIO. DirectStorage games may have higher CPU usage.");
        }

        var msg = title == "Apply Patch"
            ? "Apply the NVMe driver enhancement patch?\n\nThis will modify system registry settings."
            : "Remove the NVMe driver patch?\n\nThis will revert to default Windows behavior.";

        if (warnings.Count > 0)
            msg += "\n\n--- NOTICES ---\n" + string.Join("\n\n", warnings);

        msg += "\n\nProceed?";
        return msg;
    }

    [RelayCommand]
    private async Task ApplyPatch()
    {
        if (_preflight?.VeraCryptDetected == true)
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
            _preflight?.BitLockerEnabled ?? false,
            _preflight?.VeraCryptDetected ?? false,
            _preflight?.NativeNVMeStatus,
            _preflight?.BypassIOStatus,
            msg => Log(msg),
            (val, text) => Application.Current?.Dispatcher.Invoke(() => SetProgress(val, text))));

        Application.Current?.Dispatcher.Invoke(() =>
        {
            LogBeforeAfter(result.BeforeSnapshot, result.AfterSnapshot, "Install Patch");
            UpdateRegistryDisplay();
            UpdateStatusDisplay();
            UpdateOverviewSummary();
            UpdateOperationalHistory();
            ButtonsEnabled = true;
            ApplyEnabled = PreflightService.AllCriticalPassed(_preflight!.Checks) && !_preflight.VeraCryptDetected;

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
                    PatchService.InitiateRestart(Config.RestartDelay);
                }
            }
            else if (result.WasRolledBack)
            {
                ToastService.Show("NVMe Patch Failed", "Changes rolled back.", ToastType.Warning, Config.EnableToasts);
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
            (val, text) => Application.Current?.Dispatcher.Invoke(() => SetProgress(val, text))));

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
                    PatchService.InitiateRestart(Config.RestartDelay);
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
            (val, text) => Application.Current?.Dispatcher.Invoke(() => SetProgress(val, text)));

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
        var path = await DiagnosticsService.ExportAsync(Config.WorkingDir, _preflight, _logHistory);
        if (path is not null)
        {
            Config.LastDiagnosticsPath = path;
            ConfigService.Save(Config);
            UpdateOperationalHistory();
            Log($"Diagnostics exported: {path}", "SUCCESS");
            InfoDialog?.Invoke("Export Complete", $"Diagnostics exported to:\n{path}", DialogIcon.Information);
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
        if (_logHistory.Count == 0) return;
        Clipboard.SetText(string.Join("\r\n", _logHistory));
        Log("Log copied to clipboard", "SUCCESS");
    }

    [RelayCommand]
    private void ExportLog()
    {
        if (_logHistory.Count == 0) return;
        var path = Path.Combine(Config.WorkingDir, $"NVMe_Patcher_Log_{DateTime.Now:yyyyMMdd_HHmmss}_manual.txt");
        File.WriteAllLines(path, _logHistory);
        Log($"Log exported: {path}", "SUCCESS");
        InfoDialog?.Invoke("Log Exported", $"Log saved to:\n{path}", DialogIcon.Information);
    }

    [RelayCommand]
    private async Task Refresh()
    {
        Log("----------------------------------------");
        Log("Refreshing system checks...");
        await RunPreflightAsync();
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
    private void OpenDataFolder() => Process.Start("explorer.exe", Config.WorkingDir);

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

        Process.Start(new ProcessStartInfo
        {
            FileName = recoveryKitPath,
            UseShellExecute = true
        });
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

        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{verificationScriptPath}\"")
        {
            UseShellExecute = true
        });
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

        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{diagnosticsReportPath}\"")
        {
            UseShellExecute = true
        });
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
        if (!string.IsNullOrEmpty(UpdateUrl))
            Process.Start(new ProcessStartInfo(UpdateUrl) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenGitHub() => Process.Start(new ProcessStartInfo(AppConfig.GitHubURL) { UseShellExecute = true });

    [RelayCommand]
    private void OpenDocs() => Process.Start(new ProcessStartInfo(AppConfig.DocumentationURL) { UseShellExecute = true });

    public void SyncConfigFromUI()
    {
        Config.IncludeServerKey = IncludeServerKey;
        Config.SkipWarnings = SkipWarnings;
        Config.AutoSaveLog = AutoSaveLog;
        Config.EnableToasts = EnableToasts;
        Config.WriteEventLog = WriteEventLog;
        if (int.TryParse(RestartDelayText, out int delay) && delay >= 5 && delay <= 300)
            Config.RestartDelay = delay;
    }

    public void OnClosing()
    {
        SyncConfigFromUI();
        ConfigService.Save(Config);

        if (Config.AutoSaveLog && _logHistory.Count > 5)
        {
            var path = Path.Combine(Config.WorkingDir, $"NVMe_Patcher_Log_{DateTime.Now:yyyyMMdd_HHmmss}_autosave.txt");
            try { File.WriteAllLines(path, _logHistory); } catch { }
        }

        try
        {
            var logFiles = Directory.GetFiles(Config.WorkingDir, "NVMe_Patcher_Log_*.txt")
                .OrderByDescending(File.GetLastWriteTime).ToArray();
            foreach (var f in logFiles.Skip(20))
                try { File.Delete(f); } catch { }
        }
        catch { }

        EventLogService.Write($"{AppConfig.AppName} closed");
        ToastService.DisposeAll();
    }

    private void SetProgress(int value, string text)
    {
        ProgressValue = Math.Min(value, 100);
        ProgressText = text;
        ProgressVisible = value > 0 && value < 100;
    }

    private void AppendLogEntry(string entry, string message, string level)
    {
        LogEntries.Add(entry);
        LogEntryCount++;

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
