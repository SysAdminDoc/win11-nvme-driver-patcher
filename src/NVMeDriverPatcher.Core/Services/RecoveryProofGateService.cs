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
    public BitLockerRecoveryProof? BitLockerRecovery { get; set; }
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

        try
        {
            report.BitLockerRecovery = BitLockerRecoveryService.InspectSystemVolume();
            report.Items.Add(EvaluateBitLockerRecovery(report.BitLockerRecovery));
        }
        catch (Exception ex)
        {
            report.Items.Add(new()
            {
                Label = "BitLocker recovery",
                Passed = false,
                Detail = $"Authoritative recovery proof failed: {ex.GetType().Name}"
            });
        }
        report.Items.Add(EvaluateRecoveryKit(config));
        report.Items.Add(EvaluateBackupCapability(config));
        report.Items.Add(EvaluateSafeBootEntries());
        report.Items.Add(EvaluateRestorePointCapability());

        return report;
    }

    internal static RecoveryProofItem EvaluateBitLockerRecovery(BitLockerRecoveryProof proof) => new()
    {
        Label = "BitLocker recovery",
        Passed = proof.ReadyForMutation,
        Detail = proof.Detail
    };

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

    internal static RecoveryProofItem EvaluateBackupCapability(AppConfig config)
    {
        string? probePath = null;
        try
        {
            // Match PatchService.Install exactly: an explicit per-operation directory wins;
            // only an unset value falls back to the configured app data location.
            var configured = string.IsNullOrWhiteSpace(config.WorkingDir)
                ? AppConfig.GetWorkingDir()
                : config.WorkingDir;
            var workDir = Path.GetFullPath(configured);
            Directory.CreateDirectory(workDir);
            probePath = Path.Combine(workDir, $".recovery-probe-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                       probePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write("recovery-probe"u8);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(probePath);
            probePath = null;
            return new()
            {
                Label = "Backup directory",
                Passed = true,
                Detail = $"Writable operation directory verified at {workDir}"
            };
        }
        catch (Exception ex)
        {
            return new()
            {
                Label = "Backup directory",
                Passed = false,
                Detail = $"Configured operation directory is not writable — registry backup would fail ({ex.GetType().Name})"
            };
        }
        finally
        {
            try { if (!string.IsNullOrWhiteSpace(probePath) && File.Exists(probePath)) File.Delete(probePath); }
            catch { }
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

    internal static RecoveryProofItem EvaluateWinReInjectionPlan(WinReInjectionPlan plan)
    {
        if (plan.IsExecutable)
        {
            return new()
            {
                Label = "WinRE stornvme injection",
                Passed = true,
                Detail = "WinRE image and stornvme.inf are available; --apply will back up and checksum before DISM changes"
            };
        }

        var warnings = plan.Warnings
            .Where(w => w.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                        w.Contains("NOT", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return new()
        {
            Label = "WinRE stornvme injection",
            Passed = false,
            Detail = warnings.Count == 0
                ? "WinRE injection plan is not executable"
                : string.Join("; ", warnings)
        };
    }

    private static RecoveryProofItem EvaluateRestorePointCapability()
    {
        try
        {
            // RPSessionInterval (scheduled-checkpoint cadence) is a weak proxy: it can be present
            // and non-zero while System Protection on the system drive is OFF, so CreateRestorePoint
            // would silently no-op. The authoritative signals are (1) the global DisableSR flag and
            // (2) whether the system drive actually has shadow-copy storage configured.
            bool globallyDisabled = false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
                if (key?.GetValue("DisableSR") is int disable)
                    globallyDisabled = disable == 1;
            }
            catch { }

            bool systemDriveProtected = SystemDriveHasShadowStorage();

            var (passed, detail) = ClassifyRestoreCapability(globallyDisabled, systemDriveProtected);
            return new() { Label = "System Restore", Passed = passed, Detail = detail };
        }
        catch (Exception ex)
        {
            return new() { Label = "System Restore", Passed = false, Detail = $"Check failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Pure verdict for restore-point capability. System Protection must be active for the system
    /// drive (shadow storage configured) for a restore point to actually be created. A non-zero
    /// RPSessionInterval is deliberately NOT trusted here — it governs cadence, not enablement.
    /// </summary>
    internal static (bool Passed, string Detail) ClassifyRestoreCapability(bool globallyDisabled, bool systemDriveProtected)
    {
        if (globallyDisabled)
            return (false, "System Restore is disabled (DisableSR=1) — no automatic rollback point will be created");
        if (!systemDriveProtected)
            return (false, "System Protection is off for the system drive — CreateRestorePoint would silently no-op, so no rollback point will be created");
        return (true, "System Protection is enabled for the system drive — a restore point will be created");
    }

    // Best-effort: does the system drive have shadow-copy storage configured (MaxSpace > 0)?
    // That is the on-disk evidence that System Protection is actually turned on for that volume.
    private static bool SystemDriveHasShadowStorage()
    {
        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

            // Resolve the system volume's GUID device path so we can match it against the
            // Win32_ShadowStorage.Volume reference (which embeds the same Volume{guid}).
            string? sysVolumeGuid = null;
            using (var volSearch = new System.Management.ManagementObjectSearcher(
                $"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter='{systemDrive}'"))
            using (var vols = WmiQueryHelper.ExecuteWithTimeout(volSearch))
            {
                foreach (var raw in vols)
                {
                    if (raw is not System.Management.ManagementObject v) continue;
                    using (v)
                    {
                        var deviceId = v["DeviceID"] as string;
                        sysVolumeGuid = ExtractVolumeGuid(deviceId);
                    }
                    break;
                }
            }
            if (string.IsNullOrEmpty(sysVolumeGuid)) return false;

            using var ss = new System.Management.ManagementObjectSearcher("SELECT Volume, MaxSpace FROM Win32_ShadowStorage");
            using var col = WmiQueryHelper.ExecuteWithTimeout(ss);
            foreach (var raw in col)
            {
                if (raw is not System.Management.ManagementObject m) continue;
                using (m)
                {
                    var volRef = m["Volume"] as string;
                    ulong max = 0;
                    try { max = Convert.ToUInt64(m["MaxSpace"] ?? 0UL); } catch { }
                    if (!string.IsNullOrEmpty(volRef)
                        && volRef.IndexOf(sysVolumeGuid, StringComparison.OrdinalIgnoreCase) >= 0
                        && max > 0)
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    // Pulls the "Volume{guid}" token out of a volume device path so two differently-escaped
    // representations of the same volume still compare equal.
    internal static string? ExtractVolumeGuid(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        const string marker = "Volume{";
        var start = deviceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var end = deviceId.IndexOf('}', start);
        if (end < 0) return null;
        return deviceId.Substring(start, end - start + 1);
    }
}
