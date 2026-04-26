using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher.ViewModels;

// Bindable command surface of MainViewModel. Every [RelayCommand]-decorated method plus
// the re-entrancy guards (static in-flight slots + TryAcquireInFlight/ReleaseInFlight
// helpers) that protect the long-running commands. CommunityToolkit.Mvvm's source
// generator walks partial declarations of the same class, so the XxxCommand properties
// it emits still bind from MainWindow.xaml exactly as before.
//
// Split purpose: the main MainViewModel.cs file is dominated by constructor + preflight
// render + [ObservableProperty] declarations; keeping ~800 lines of command handlers
// here lets each file stay focused on its own responsibility.
public partial class MainViewModel
{
    [RelayCommand]
    private async Task ApplyPatch()
    {
        if (!TryAcquireInFlight(ref _applyInFlight))
        {
            // A prior Apply is still running. The UI button is normally disabled, but this
            // guards against a rapid double-click landing before the binding updates.
            Log("Apply Patch is already running — waiting for the current run to finish.", "WARNING");
            return;
        }

        try
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
                    "VeraCrypt system encryption is a hard stop for this patch.\n\nEnabling the native NVMe driver (nvmedisk.sys) breaks VeraCrypt boot, so this machine should stay on the current driver path unless that configuration changes.\n\nThis block cannot be overridden.",
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
                var verificationScriptPath = RecoveryKitService.GenerateVerificationScript(
                    Config.WorkingDir,
                    Config.PatchProfile,
                    Config.IncludeServerKey);
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
                // Open the post-patch watchdog window. Any Storport/disk/bugcheck events
                // inside this window feed the Unstable verdict + auto-revert path on next run.
                EventLogWatchdogService.Arm(Config);
                ConfigService.Save(Config);
                UpdateOperationalHistory();

                // Offer restart
                var restartMsg = $"Patch applied successfully ({result.AppliedCount}/{result.TotalExpected} components).\n\n" +
                    "Restart to let Windows activate the native NVMe driver.\n\n" +
                    $"If you choose Restart, Windows will restart in {Config.RestartDelay} seconds.\n\n" +
                    "After reboot:\n• Drives move from Disk drives to Storage disks.\n" +
                    "• The active driver changes from stornvme.sys to nvmedisk.sys.\n" +
                    "• A recovery kit has been saved; copy it to removable media for the safest rollback path.";

                if (ConfirmDialog?.Invoke("Installation Complete", restartMsg) == true)
                {
                    Log($"Initiating system restart in {Config.RestartDelay} seconds…");
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
        catch (Exception ex)
        {
            RecoverOperationFailure("Apply Patch", ex);
        }
        finally
        {
            ReleaseInFlight(ref _applyInFlight);
        }
    }

    [RelayCommand]
    private async Task RemovePatch()
    {
        if (!TryAcquireInFlight(ref _removeInFlight))
        {
            Log("Remove Patch is already running — waiting for the current run to finish.", "WARNING");
            return;
        }
        try
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
                // Close the watchdog window — no more post-patch monitoring makes sense
                // after an explicit uninstall. Safe even if it was never armed.
                try { EventLogWatchdogService.Disarm(Config); } catch { }
                ToastService.Show("NVMe Patch Removed", "Patch components removed. Restart required.", ToastType.Info, Config.EnableToasts);

                var restartMsg = $"Patch removed successfully ({result.AppliedCount} component(s)).\n\n" +
                    "Restart to restore the legacy NVMe driver path.\n\n" +
                    "After reboot, drives return to Disk drives using stornvme.sys.";

                if (ConfirmDialog?.Invoke("Removal Complete", restartMsg) == true)
                {
                    Log($"Initiating system restart in {Config.RestartDelay} seconds…");
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
        catch (Exception ex)
        {
            RecoverOperationFailure("Remove Patch", ex);
        }
        finally
        {
            ReleaseInFlight(ref _removeInFlight);
        }
    }

    [RelayCommand]
    private async Task RunBackup()
    {
        if (!TryAcquireInFlight(ref _backupInFlight))
        {
            Log("Backup is already running.", "WARNING");
            return;
        }
        try
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
        }
        finally
        {
            // Re-enable buttons on every exit path so a backup-service crash can't leave the
            // UI locked. Previous code missed this finally; the full-audit flagged it as LOW.
            ButtonsEnabled = true;
            ReleaseInFlight(ref _backupInFlight);
        }
    }

    [RelayCommand]
    private async Task RunBenchmark()
    {
        if (!TryAcquireInFlight(ref _benchmarkInFlight))
        {
            Log("A benchmark is already running.", "WARNING");
            return;
        }
        ButtonsEnabled = false;
        _benchmarkCts = new System.Threading.CancellationTokenSource();
        BenchmarkRunning = true;
        try
        {
            var status = RegistryService.GetPatchStatus();
            string label = status.Applied ? "Post-Patch" : "Pre-Patch";
            Log($"Starting storage benchmark ({label})...");
            Log("This will take approximately 60 seconds. Do not use disk-heavy apps. Click Cancel to stop early.", "WARNING");

            var result = await BenchmarkService.RunBenchmarkAsync(
                Config.WorkingDir, label,
                msg => Log(msg),
                (val, text) => Application.Current?.Dispatcher.BeginInvoke(() => SetProgress(val, text)),
                _benchmarkCts.Token);

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
        }
        finally
        {
            // Guarantee the UI is usable again even if BenchmarkService throws.
            ButtonsEnabled = true;
            BenchmarkRunning = false;
            try { _benchmarkCts?.Dispose(); } catch { }
            _benchmarkCts = null;
            ReleaseInFlight(ref _benchmarkInFlight);
        }
    }

    /// <summary>Cancels the currently-running benchmark. Safe to call when no benchmark is
    /// running — no-ops if the CTS has already been cleared. Wired to a Cancel button that
    /// the XAML shows only while <see cref="BenchmarkRunning"/> is true.</summary>
    [RelayCommand]
    private void CancelBenchmark()
    {
        var cts = _benchmarkCts;
        if (cts is null)
        {
            Log("No benchmark is running.", "DEBUG");
            return;
        }
        try
        {
            Log("Canceling benchmark...", "WARNING");
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS was disposed between our null-check and Cancel() — the benchmark already
            // finished on its own. Nothing to do.
        }
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
        if (!TryAcquireInFlight(ref _fallbackInFlight))
        {
            Log("ViVeTool fallback is already running.", "WARNING");
            return;
        }
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
            ReleaseInFlight(ref _fallbackInFlight);
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
            Config.LastSupportBundlePath = path;
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

    // Re-entrancy guards for long-running commands. Each is 0 when idle, 1 while a single
    // invocation is in flight. `ButtonsEnabled = false` guards the UI but it's set AFTER the
    // async method starts, so a rapid double-click before the property binding re-renders
    // could race two invocations. Interlocked.CompareExchange closes that window.
    //
    // Kept STATIC so a future second MainViewModel instance (hypothetical multi-window
    // support) can't concurrently run the same registry-mutating command. App.xaml.cs
    // already enforces single-instance via mutex so the process boundary is covered;
    // these guards close the in-process boundary. The process dying clears static state
    // on next launch, so there's no stuck-guard recovery concern.
    private static int _refreshInFlight;
    private static int _applyInFlight;
    private static int _removeInFlight;
    private static int _benchmarkInFlight;
    private static int _backupInFlight;
    private static int _fallbackInFlight;

    /// <summary>Acquires a re-entrancy slot using an atomic 0→1 transition. Returns true if
    /// the caller owns the slot (and MUST release it via <see cref="ReleaseInFlight"/> in a
    /// finally block); false if another invocation is already running.</summary>
    private static bool TryAcquireInFlight(ref int slot) =>
        System.Threading.Interlocked.CompareExchange(ref slot, 1, 0) == 0;

    private static void ReleaseInFlight(ref int slot) =>
        System.Threading.Interlocked.Exchange(ref slot, 0);

    [RelayCommand]
    private async Task Refresh()
    {
        // Block re-entrant Refresh: clicking the button repeatedly was kicking off concurrent
        // PreflightService.RunAllAsync executions that fought over the WMI queries and the
        // _preflight field. Only one refresh runs at a time.
        if (!TryAcquireInFlight(ref _refreshInFlight))
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
            ReleaseInFlight(ref _refreshInFlight);
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
            Process.Start(CreateExplorerStartInfo(Config.WorkingDir));
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
            Process.Start(CreateExplorerStartInfo(recoveryKitPath));
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
            Process.Start(CreateNotepadStartInfo(verificationScriptPath));
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
            Process.Start(CreateNotepadStartInfo(diagnosticsReportPath));
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
        var verificationScriptPath = RecoveryKitService.GenerateVerificationScript(
            Config.WorkingDir,
            Config.PatchProfile,
            Config.IncludeServerKey);
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


    // =====================================================================
    // v4.4 additions — watchdog / reliability / minidump / dry-run commands.
    // Bindable from MainWindow.xaml; also exercised by tests without requiring the XAML.
    // Kept at the bottom of the class so the existing UI layout is untouched.
    // =====================================================================

    [ObservableProperty] private string _watchdogVerdictText = "Watchdog status has not been checked in this session.";
    [ObservableProperty] private string _reliabilitySummaryText = "Reliability correlation has not been refreshed yet.";
    [ObservableProperty] private string _minidumpSummaryText = "Minidumps have not been triaged for NVMe-related crashes yet.";
    [ObservableProperty] private string _dryRunPreviewText = "Select Dry Run to inspect planned registry writes before anything changes.";

    [RelayCommand]
    private void RefreshWatchdogStatus()
    {
        try
        {
            var report = EventLogWatchdogService.Evaluate(Config);
            WatchdogVerdictText = $"{report.Verdict}: {report.Summary}";
            Log($"Watchdog: {report.Summary}");
        }
        catch (Exception ex)
        {
            WatchdogVerdictText = $"Watchdog check failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshReliability()
    {
        try
        {
            DateTime? patchTs = null;
            if (!string.IsNullOrWhiteSpace(Config.PendingVerificationSince) &&
                DateTime.TryParse(Config.PendingVerificationSince,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                patchTs = ts;
            var report = ReliabilityService.GetCorrelation(patchTs);
            ReliabilitySummaryText = report.Summary;
            Log($"Reliability: {report.Summary}");
        }
        catch (Exception ex)
        {
            ReliabilitySummaryText = $"Reliability check failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void TriageMinidumps()
    {
        try
        {
            DateTime? patchTs = null;
            if (!string.IsNullOrWhiteSpace(Config.PendingVerificationSince) &&
                DateTime.TryParse(Config.PendingVerificationSince,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                patchTs = ts;
            var report = MinidumpTriageService.Analyze(patchTs);
            MinidumpSummaryText = report.Summary;
            Log($"Minidump triage: {report.Summary}");
            foreach (var d in report.Dumps.Where(d => d.MentionsNVMeStack))
                Log($"  [NVMe] {d.CreatedUtc:u} — {Path.GetFileName(d.FilePath)}: {d.Notes}", "WARN");
        }
        catch (Exception ex)
        {
            MinidumpSummaryText = $"Minidump scan failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PreviewDryRun()
    {
        try
        {
            var report = DryRunService.PlanInstall(Config, _preflight);
            DryRunPreviewText = DryRunService.RenderMarkdown(report);
            Log($"Dry-run: {report.Summary}");
        }
        catch (Exception ex)
        {
            DryRunPreviewText = $"Dry-run preview failed: {ex.Message}";
        }
    }
}
