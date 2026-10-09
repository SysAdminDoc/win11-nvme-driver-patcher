using System.IO;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.ViewModels;

// Workspace / operational-history partial of MainViewModel. Reads the on-disk artifact set
// (registry backups, recovery kit, verification script, diagnostics reports, benchmark
// DB rows) and projects it into the workspace summary strings and sidebar badges.
// Pure state-projection; no I/O beyond filesystem stat + the DataService DB.
public partial class MainViewModel
{
    private void UpdateOperationalHistory()
    {
        try
        {
            _hasBackupFiles = Directory.Exists(Config.WorkingDir)
                && Directory.EnumerateFiles(Config.WorkingDir, "*.reg", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            _hasBackupFiles = false;
        }

        try
        {
            var benchmarks = DataService.GetBenchmarkHistory();
            _hasBenchmarkHistory = DataService.DatabaseState.IsAvailable && benchmarks.Count > 0;
        }
        catch
        {
            _hasBenchmarkHistory = false;
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
            (true, true, true) => "Recovery kit, verification script and diagnostics are all in place. You can confirm or undo the change from here.",
            (true, true, false) => "The recovery kit and verification script are ready. Export diagnostics too if you want a complete support bundle.",
            (true, false, true) => "The recovery kit and diagnostics are ready. Generate the verification script before the next restart.",
            (false, true, true) => "Verification and diagnostics are ready, but there's no recovery kit yet. Export one to a USB drive before you rely on the patch.",
            (true, false, false) => "The recovery kit is ready. Generate the verification script and diagnostics before you restart or hand the machine off.",
            (false, true, false) => "There's a verification script, but no recovery kit or diagnostics yet. Export a recovery kit so rollback doesn't depend on memory.",
            (false, false, true) => "Diagnostics are saved, but there's no recovery kit or verification script yet. Create both before you apply.",
            _ => "Create a recovery kit and a verification script before you apply, so the change is easy to undo or confirm."
        };

        var sharedStatus = RegistryService.GetPatchStatus();
        UpdateWorkflowGuide(sharedStatus);
        UpdateRecommendedActions(sharedStatus);
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
