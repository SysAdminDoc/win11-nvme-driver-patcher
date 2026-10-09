using System.IO;
using System.Windows;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.ViewModels;

// Workspace / operational-history partial of MainViewModel. Reads the on-disk artifact set
// (registry backups, recovery kit, verification script, diagnostics reports, benchmark
// DB rows) and projects it into the workspace summary strings and sidebar badges.
// Pure state-projection; no I/O beyond filesystem stat + the DataService DB.
public partial class MainViewModel
{
    // Newest request wins: a slow gather that finishes after a newer one started must not
    // overwrite the newer state.
    private int _operationalHistoryGeneration;

    /// <summary>
    /// Everything <see cref="GatherOperationalHistory"/> reads from disk, the benchmark database and
    /// the registry, as plain data. Applying it touches only view-model state.
    /// </summary>
    internal sealed record OperationalHistorySnapshot(
        bool HasBackupFiles,
        bool HasBenchmarkHistory,
        string? RecoveryKitPath,
        bool RecoveryKitInWorkingFolder,
        DateTime RecoveryKitWrittenAt,
        bool RecoveryKitReadFailed,
        string? VerificationScriptPath,
        DateTime VerificationScriptWrittenAt,
        bool VerificationScriptReadFailed,
        string? DiagnosticsReportPath,
        DateTime DiagnosticsReportWrittenAt,
        bool DiagnosticsReportReadFailed,
        PatchStatus? PatchStatus);

    /// <summary>The config values the gather needs, copied on the UI thread so the worker never touches Config.</summary>
    internal sealed record OperationalHistoryInputs(
        string WorkingDir,
        string? LastRecoveryKitPath,
        string? LastVerificationScriptPath,
        string? LastDiagnosticsPath);

    // Refreshes the workspace summaries without blocking the caller. The directory scans, the
    // SQLite read and the registry read run on the thread pool; the results are applied back here.
    // Callers never read the outcome straight away, so they stay synchronous and fire this.
    private void UpdateOperationalHistory() => _ = RefreshOperationalHistoryAsync();

    internal async Task RefreshOperationalHistoryAsync()
    {
        int generation = ++_operationalHistoryGeneration;
        var inputs = CurrentHistoryInputs();

        OperationalHistorySnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => GatherOperationalHistory(inputs));
        }
        catch (Exception ex)
        {
            Log($"Workspace history refresh skipped: {ex.Message}", "DEBUG");
            return;
        }

        if (generation != _operationalHistoryGeneration) return;

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                ApplyOperationalHistory(snapshot);
            else
                dispatcher.Invoke(() => ApplyOperationalHistory(snapshot));
        }
        catch (Exception ex)
        {
            Log($"Workspace history refresh skipped: {ex.Message}", "DEBUG");
        }
    }

    // Pure gather: file system, SQLite and registry only. No view-model state, no dispatcher.
    internal static OperationalHistorySnapshot GatherOperationalHistory(OperationalHistoryInputs inputs)
    {
        bool hasBackupFiles;
        try
        {
            hasBackupFiles = Directory.Exists(inputs.WorkingDir)
                && Directory.EnumerateFiles(inputs.WorkingDir, "*.reg", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            hasBackupFiles = false;
        }

        bool hasBenchmarkHistory;
        try
        {
            var benchmarks = DataService.GetBenchmarkHistory();
            hasBenchmarkHistory = DataService.DatabaseState.IsAvailable && benchmarks.Count > 0;
        }
        catch
        {
            hasBenchmarkHistory = false;
        }

        string? recoveryKitPath = null;
        bool recoveryKitInWorkingFolder = false, recoveryKitReadFailed = false;
        DateTime recoveryKitWrittenAt = default;
        try
        {
            recoveryKitPath = ResolveRecoveryKitPath(inputs);
            if (!string.IsNullOrWhiteSpace(recoveryKitPath))
            {
                recoveryKitWrittenAt = Directory.GetFiles(recoveryKitPath, "*", SearchOption.TopDirectoryOnly)
                    .Select(File.GetLastWriteTime)
                    .DefaultIfEmpty(Directory.GetLastWriteTime(recoveryKitPath))
                    .Max();
                recoveryKitInWorkingFolder = string.Equals(
                    recoveryKitPath, Path.Combine(inputs.WorkingDir, "NVMe_Recovery_Kit"), StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            recoveryKitPath = null;
            recoveryKitReadFailed = true;
        }

        string? verificationScriptPath = null;
        bool verificationScriptReadFailed = false;
        DateTime verificationScriptWrittenAt = default;
        try
        {
            verificationScriptPath = ResolveVerificationScriptPath(inputs);
            if (!string.IsNullOrWhiteSpace(verificationScriptPath))
                verificationScriptWrittenAt = new FileInfo(verificationScriptPath).LastWriteTime;
        }
        catch
        {
            verificationScriptPath = null;
            verificationScriptReadFailed = true;
        }

        string? diagnosticsReportPath = null;
        bool diagnosticsReportReadFailed = false;
        DateTime diagnosticsReportWrittenAt = default;
        try
        {
            diagnosticsReportPath = ResolveLatestDiagnosticsReportPath(inputs);
            if (!string.IsNullOrWhiteSpace(diagnosticsReportPath))
                diagnosticsReportWrittenAt = new FileInfo(diagnosticsReportPath).LastWriteTime;
        }
        catch
        {
            diagnosticsReportPath = null;
            diagnosticsReportReadFailed = true;
        }

        PatchStatus? patchStatus = null;
        try { patchStatus = RegistryService.GetPatchStatus(); } catch { }

        return new OperationalHistorySnapshot(
            hasBackupFiles, hasBenchmarkHistory,
            recoveryKitPath, recoveryKitInWorkingFolder, recoveryKitWrittenAt, recoveryKitReadFailed,
            verificationScriptPath, verificationScriptWrittenAt, verificationScriptReadFailed,
            diagnosticsReportPath, diagnosticsReportWrittenAt, diagnosticsReportReadFailed,
            patchStatus);
    }

    // UI-thread half: turns a gathered snapshot into the bound strings and badges.
    private void ApplyOperationalHistory(OperationalHistorySnapshot snapshot)
    {
        _hasBackupFiles = snapshot.HasBackupFiles;
        _hasBenchmarkHistory = snapshot.HasBenchmarkHistory;

        if (snapshot.RecoveryKitReadFailed)
        {
            HasRecoveryKit = false;
            RecoveryKitStatusText = "Recovery kit status could not be read.";
        }
        else
        {
            HasRecoveryKit = !string.IsNullOrWhiteSpace(snapshot.RecoveryKitPath);
            if (!HasRecoveryKit)
            {
                RecoveryKitStatusText = NoRecoveryKitText;
            }
            else
            {
                var locationLabel = snapshot.RecoveryKitInWorkingFolder ? "working folder" : "export location";
                RecoveryKitStatusText = $"Recovery kit ready in the {locationLabel}. Last updated {snapshot.RecoveryKitWrittenAt:g}. Includes offline rollback files for Windows and WinRE.";
            }
        }

        if (snapshot.VerificationScriptReadFailed)
        {
            HasVerificationScript = false;
            VerificationScriptStatusText = "Verification script status could not be read.";
        }
        else
        {
            HasVerificationScript = !string.IsNullOrWhiteSpace(snapshot.VerificationScriptPath);
            VerificationScriptStatusText = !HasVerificationScript
                ? NoVerificationScriptText
                : $"Verification script ready as {Path.GetFileName(snapshot.VerificationScriptPath)}, updated {snapshot.VerificationScriptWrittenAt:g}. Use it after reboot to confirm every expected registry and Safe Mode key is present.";
        }

        if (snapshot.DiagnosticsReportReadFailed)
        {
            HasDiagnosticsReport = false;
            DiagnosticsReportStatusText = "Diagnostics report status could not be read.";
        }
        else
        {
            HasDiagnosticsReport = !string.IsNullOrWhiteSpace(snapshot.DiagnosticsReportPath);
            DiagnosticsReportStatusText = !HasDiagnosticsReport
                ? NoDiagnosticsReportText
                : $"Latest diagnostics report: {Path.GetFileName(snapshot.DiagnosticsReportPath)}, exported {snapshot.DiagnosticsReportWrittenAt:g}. Keep it with the recovery kit when you need a support-ready snapshot of this machine.";
        }

        RecoveryWorkspaceSummaryText = (HasRecoveryKit, HasVerificationScript, HasDiagnosticsReport) switch
        {
            (true, true, true) => "Recovery kit, verification script and diagnostics are all in place. You can confirm or undo the change from here.",
            (true, true, false) => "The recovery kit and verification script are ready. Export diagnostics too if you want a complete support bundle.",
            (true, false, true) => "The recovery kit and diagnostics are ready. Generate the verification script before the next restart.",
            (false, true, true) => "Verification and diagnostics are ready, but there's no recovery kit yet. Export one to a USB drive before you rely on the patch.",
            (true, false, false) => "The recovery kit is ready. Generate the verification script and diagnostics before you restart or hand the machine off.",
            (false, true, false) => "There's a verification script, but no recovery kit or diagnostics yet. Export a recovery kit so rollback doesn't depend on memory.",
            (false, false, true) => "Diagnostics are saved, but there's no recovery kit or verification script yet. Create both before you apply.",
            _ => "Create a recovery kit and a verification script before you apply, so the change is easy to undo or confirm."
        };

        UpdateWorkflowGuide(snapshot.PatchStatus);
        UpdateWorkspaceBadges();
    }

    private string? ResolveRecoveryKitPath() => ResolveRecoveryKitPath(CurrentHistoryInputs());

    private string? ResolveVerificationScriptPath() => ResolveVerificationScriptPath(CurrentHistoryInputs());

    private string? ResolveLatestDiagnosticsReportPath() => ResolveLatestDiagnosticsReportPath(CurrentHistoryInputs());

    private OperationalHistoryInputs CurrentHistoryInputs() => new(
        Config.WorkingDir, Config.LastRecoveryKitPath, Config.LastVerificationScriptPath, Config.LastDiagnosticsPath);

    private static string? ResolveRecoveryKitPath(OperationalHistoryInputs inputs)
    {
        if (!string.IsNullOrWhiteSpace(inputs.LastRecoveryKitPath) && Directory.Exists(inputs.LastRecoveryKitPath))
            return inputs.LastRecoveryKitPath;

        if (string.IsNullOrWhiteSpace(inputs.WorkingDir))
            return null;

        var localKitPath = Path.Combine(inputs.WorkingDir, "NVMe_Recovery_Kit");
        return Directory.Exists(localKitPath) ? localKitPath : null;
    }

    private static string? ResolveVerificationScriptPath(OperationalHistoryInputs inputs)
    {
        if (!string.IsNullOrWhiteSpace(inputs.LastVerificationScriptPath) && File.Exists(inputs.LastVerificationScriptPath))
            return inputs.LastVerificationScriptPath;

        if (string.IsNullOrWhiteSpace(inputs.WorkingDir))
            return null;

        var localScriptPath = Path.Combine(inputs.WorkingDir, "Verify_NVMe_Patch.ps1");
        return File.Exists(localScriptPath) ? localScriptPath : null;
    }

    private static string? ResolveLatestDiagnosticsReportPath(OperationalHistoryInputs inputs)
    {
        if (IsExistingTextFile(inputs.LastDiagnosticsPath))
            return inputs.LastDiagnosticsPath;

        if (string.IsNullOrEmpty(inputs.WorkingDir) || !Directory.Exists(inputs.WorkingDir))
            return null;

        try
        {
            return Directory.GetFiles(inputs.WorkingDir, "NVMe_Diagnostics_*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }
        catch
        {
            // Folder enumeration can transiently throw if the user is moving files around.
            return null;
        }
    }

    internal static bool IsExistingTextFile(string? path) =>
        ConfigService.IsUsableAbsolutePath(path)
        && string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);
    // The activity rail used to glue a bound number to a literal Run (" events"), which rendered
    // "4  events" with a doubled space and "1 events" for a single entry.
    public string LogEntryCountText => $"{LogEntryCount} {Pluralize(LogEntryCount, "entry", "entries")}";
    public string LogWarningCountText => $"{LogWarningCount} {Pluralize(LogWarningCount, "warning")}";
    public string LogErrorCountText => $"{LogErrorCount} {Pluralize(LogErrorCount, "error")}";

    private void UpdateActivitySummary()
    {
        if (LogEntryCount == 0)
        {
            ActivitySummaryText = "Activity entries will appear here as checks and actions run.";
        }
        else if (LogErrorCount > 0)
        {
            ActivitySummaryText = $"{LogEntryCountText} this session, including {LogErrorCountText} and {LogWarningCountText}.";
        }
        else if (LogWarningCount > 0)
        {
            ActivitySummaryText = $"{LogEntryCountText} this session, with warnings but no errors.";
        }
        else
        {
            ActivitySummaryText = $"{LogEntryCountText} this session, no warnings or errors.";
        }

        // Short enough to fit the activity rail without trimming.
        LogRetentionText = $"{(AutoSaveLog ? "Saved on close" : "Manual export only")} · {(WriteEventLog ? "Event Log on" : "Event Log off")}";
        UpdateWorkspaceBadges();
    }

    private void UpdateWorkspaceBadges()
    {
        if (LogErrorCount > 0)
        {
            ActivityTabBadgeText = LogErrorCountText;
            ActivityTabBadgeColor = "Red";
        }
        else if (LogWarningCount > 0)
        {
            ActivityTabBadgeText = LogWarningCountText;
            ActivityTabBadgeColor = "Yellow";
        }
        else if (LogEntryCount > 0)
        {
            ActivityTabBadgeText = "Live";
            ActivityTabBadgeColor = "Green";
        }
        else
        {
            ActivityTabBadgeText = "Idle";
            ActivityTabBadgeColor = "TextDim";
        }

        RecoveryMissingAssetCount = new[] { HasRecoveryKit, HasVerificationScript, HasDiagnosticsReport }.Count(ready => !ready);
        if (RecoveryMissingAssetCount == 0)
        {
            RecoveryTabBadgeText = "Ready";
            RecoveryTabBadgeColor = "Green";
        }
        else
        {
            RecoveryTabBadgeText = $"{RecoveryMissingAssetCount} missing";
            RecoveryTabBadgeColor = "Yellow";
        }
    }
}
