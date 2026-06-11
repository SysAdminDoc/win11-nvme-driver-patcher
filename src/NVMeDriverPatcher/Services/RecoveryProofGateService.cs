using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public sealed class RecoveryProofItem
{
    public string Label { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed class RecoveryProofReport
{
    public List<RecoveryProofItem> Items { get; } = new();
    public bool AllPassed => Items.All(i => i.Passed);
    public int PassedCount => Items.Count(i => i.Passed);
    public int TotalCount => Items.Count;

    public string Summary
    {
        get
        {
            if (Items.Count == 0) return "No recovery checks performed.";
            if (AllPassed) return $"Recovery readiness: {PassedCount}/{TotalCount} checks passed.";
            var failing = Items.Where(i => !i.Passed).Select(i => i.Label);
            return $"Recovery readiness: {PassedCount}/{TotalCount} — not ready: {string.Join(", ", failing)}.";
        }
    }
}

// Pre-apply gate that verifies recovery infrastructure is in place before touching
// the storage driver stack. AR-2026-009: users who patch without offline rollback
// capability are the ones who file "bricked my PC" issues.
public static class RecoveryProofGateService
{
    public static RecoveryProofReport Evaluate(AppConfig config)
    {
        var report = new RecoveryProofReport();

        report.Items.Add(EvaluateRecoveryKit(config));
        report.Items.Add(EvaluateBackupCapability());
        report.Items.Add(EvaluateSafeBootEntries());
        report.Items.Add(EvaluateRestorePointCapability());

        return report;
    }

    private static RecoveryProofItem EvaluateRecoveryKit(AppConfig config)
    {
        try
        {
            var freshness = RecoveryKitFreshnessService.Evaluate(config);
            return freshness.State switch
            {
                RecoveryKitFreshness.Fresh => new()
                {
                    Label = "Recovery kit",
                    Passed = true,
                    Detail = $"Fresh ({freshness.AgeDays} day(s) old) at {freshness.Path}"
                },
                RecoveryKitFreshness.Stale => new()
                {
                    Label = "Recovery kit",
                    Passed = false,
                    Detail = $"Stale ({freshness.AgeDays} day(s) old) — regenerate before applying"
                },
                RecoveryKitFreshness.Missing => new()
                {
                    Label = "Recovery kit",
                    Passed = false,
                    Detail = "No recovery kit found — generate one before applying"
                },
                _ => new()
                {
                    Label = "Recovery kit",
                    Passed = false,
                    Detail = freshness.Summary
                },
            };
        }
        catch (Exception ex)
        {
            return new() { Label = "Recovery kit", Passed = false, Detail = $"Check failed: {ex.Message}" };
        }
    }

    private static RecoveryProofItem EvaluateBackupCapability()
    {
        try
        {
            var workDir = AppConfig.GetWorkingDir();
            var backupDir = System.IO.Path.Combine(workDir, "backups");
            bool hasDir = System.IO.Directory.Exists(backupDir) || System.IO.Directory.Exists(workDir);
            bool canWrite = false;

            if (hasDir)
            {
                var testDir = System.IO.Directory.Exists(backupDir) ? backupDir : workDir;
                var testFile = System.IO.Path.Combine(testDir, $".recovery-probe-{Guid.NewGuid():N}.tmp");
                try
                {
                    System.IO.File.WriteAllText(testFile, "probe");
                    System.IO.File.Delete(testFile);
                    canWrite = true;
                }
                catch { }
            }

            return canWrite
                ? new() { Label = "Backup directory", Passed = true, Detail = "Writable — registry backup will succeed" }
                : new() { Label = "Backup directory", Passed = false, Detail = "Cannot write to working directory — registry backup would fail" };
        }
        catch (Exception ex)
        {
            return new() { Label = "Backup directory", Passed = false, Detail = $"Check failed: {ex.Message}" };
        }
    }

    internal static RecoveryProofItem EvaluateSafeBootEntries()
    {
        try
        {
            var sb = SafeBootUpgradeService.Evaluate();
            if (sb.GuidEntriesPresent && sb.ServiceEntriesComplete)
                return new() { Label = "SafeBoot entries", Passed = true, Detail = "GUID + service-name entries both present" };
            if (sb.GuidEntriesPresent && !sb.ServiceEntriesComplete)
                return new() { Label = "SafeBoot entries", Passed = false, Detail = "GUID entries present but KB5079391 service-name entries missing — run upgrade-safeboot" };
            return new() { Label = "SafeBoot entries", Passed = true, Detail = "No existing SafeBoot entries (will be created during apply)" };
        }
        catch (Exception ex)
        {
            return new() { Label = "SafeBoot entries", Passed = false, Detail = $"Check failed: {ex.Message}" };
        }
    }

    private static RecoveryProofItem EvaluateRestorePointCapability()
    {
        try
        {
            bool srEnabled = false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
                if (key is not null)
                {
                    var val = key.GetValue("RPSessionInterval");
                    srEnabled = val is not (int and 0);
                }
            }
            catch { }

            return srEnabled
                ? new() { Label = "System Restore", Passed = true, Detail = "System Restore is enabled — a restore point will be created" }
                : new() { Label = "System Restore", Passed = false, Detail = "System Restore is disabled — no automatic rollback point will be created" };
        }
        catch (Exception ex)
        {
            return new() { Label = "System Restore", Passed = false, Detail = $"Check failed: {ex.Message}" };
        }
    }
}
