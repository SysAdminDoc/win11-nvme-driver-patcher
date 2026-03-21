using System.Collections.ObjectModel;
using System.Diagnostics;
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
                LogEntries.Add(entry);
                OnPropertyChanged(nameof(LogText));
            }
            else
            {
                app.Dispatcher.BeginInvoke(() =>
                {
                    LogEntries.Add(entry);
                    OnPropertyChanged(nameof(LogText));
                });
            }
        }
        catch { /* Dispatcher gone during shutdown */ }
    }

    public void ClearLog()
    {
        LogEntries.Clear();
        _logHistory.Clear();
        OnPropertyChanged(nameof(LogText));
        Log("Log cleared");
    }

    public async Task RunPreflightAsync()
    {
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
            ButtonsEnabled = true;
            ApplyEnabled = PreflightService.AllCriticalPassed(_preflight!.Checks) && !_preflight.VeraCryptDetected;

            if (result.Success)
            {
                ToastService.Show("NVMe Patch Applied", "All components applied. Restart required.", ToastType.Success, Config.EnableToasts);
                RecoveryKitService.GenerateVerificationScript(Config.WorkingDir, Config.IncludeServerKey);
                try { RecoveryKitService.Export(Config.WorkingDir); } catch { }

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
            Log($"Registry backup saved: {backupFile}", "SUCCESS");
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
        }
        ButtonsEnabled = true;
    }

    [RelayCommand]
    private async Task ExportDiagnostics()
    {
        var path = await DiagnosticsService.ExportAsync(Config.WorkingDir, _preflight, _logHistory);
        if (path is not null)
        {
            Log($"Diagnostics exported: {path}", "SUCCESS");
            InfoDialog?.Invoke("Export Complete", $"Diagnostics exported to:\n{path}", DialogIcon.Information);
        }
    }

    [RelayCommand]
    private void ExportRecoveryKit()
    {
        using var fbd = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder to save Recovery Kit (e.g., USB drive)",
            ShowNewFolderButton = true
        };
        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var kitDir = RecoveryKitService.Export(fbd.SelectedPath, msg => Log(msg));
            if (kitDir is not null)
            {
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
    private void OpenDataFolder() => Process.Start("explorer.exe", Config.WorkingDir);

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
