using System.IO;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.ViewModels;

// Workspace / operational-history partial of MainViewModel. Reads the on-disk artifact set
// (registry backups, recovery kit, verification script, diagnostics reports, benchmark +
// snapshot DB rows) and projects it into the workspace summary strings and sidebar badges.
// Pure state-projection; no I/O beyond filesystem stat + the DataService DB.
public partial class MainViewModel
{
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
        if (IsExistingTextFile(Config.LastDiagnosticsPath))
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

    internal static bool IsExistingTextFile(string? path) =>
        ConfigService.IsUsableAbsolutePath(path)
        && string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);
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

        var retentionParts = new List<string> { "Local log" };
        retentionParts.Add(AutoSaveLog ? "Auto-save on close" : "Manual export only");
        retentionParts.Add(WriteEventLog ? "Event Log on" : "Event Log off");

        LogRetentionText = string.Join(" | ", retentionParts);
        UpdateWorkspaceBadges();
    }

    private void UpdateWorkspaceBadges()
    {
        if (LogErrorCount > 0)
        {
            ActivityTabBadgeText = $"{LogErrorCount} {Pluralize(LogErrorCount, "issue")}";
            ActivityTabBadgeColor = "Red";
        }
        else if (LogWarningCount > 0)
        {
            ActivityTabBadgeText = $"{LogWarningCount} {Pluralize(LogWarningCount, "warning")}";
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

        if (BenchmarkRunCount > 0)
        {
            BenchmarkTabBadgeText = $"{BenchmarkRunCount} {Pluralize(BenchmarkRunCount, "run")}";
            BenchmarkTabBadgeColor = "Accent";
        }
        else
        {
            BenchmarkTabBadgeText = "New";
            BenchmarkTabBadgeColor = "TextDim";
        }

        if (NvmeDriveCount > 0)
        {
            TelemetryTabBadgeText = $"{NvmeDriveCount} {Pluralize(NvmeDriveCount, "drive")}";
            TelemetryTabBadgeColor = "Accent";
        }
        else if (HasDriveData && TotalDriveCount > 0)
        {
            TelemetryTabBadgeText = "No NVMe";
            TelemetryTabBadgeColor = "Yellow";
        }
        else
        {
            TelemetryTabBadgeText = "Waiting";
            TelemetryTabBadgeColor = "TextDim";
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
