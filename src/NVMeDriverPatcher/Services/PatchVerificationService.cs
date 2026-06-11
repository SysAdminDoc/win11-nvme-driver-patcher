using System.Management;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum VerificationOutcome
{
    /// <summary>No pending verification. Nothing to report.</summary>
    None,
    /// <summary>Keys written AND nvmedisk.sys is bound. Patch actually worked.</summary>
    Confirmed,
    /// <summary>Keys written but user hasn't rebooted yet. Silent — we keep waiting.</summary>
    AwaitingRestart,
    /// <summary>Keys written AND rebooted, but nvmedisk.sys still isn't bound. The build
    /// is likely one Microsoft has locked against the FeatureManagement override route
    /// (Feb/Mar 2026 Insider change). User needs the ViVeTool fallback.</summary>
    OverrideBlocked,
    /// <summary>The ViVeTool/FeatureStore fallback was applied (feature flags read as
    /// enabled), the machine rebooted, and nvmedisk.sys STILL didn't bind. On builds
    /// 26200.8524+ stornvme no longer exposes the GenNvmeDisk compatible ID, so
    /// nvmedisk.inf can never match — the flags are honored but the driver loads with
    /// zero devices (thebookisclosed/ViVe issue #164). Re-suggesting the fallback here
    /// would be a lie; there is currently no supported enablement path on these builds.</summary>
    FlagsEnabledNotBound,
    /// <summary>Registry writes are gone entirely — user uninstalled, or a Windows update
    /// wiped them. Treated as "no patch active" without sounding the alarm.</summary>
    Reverted,
    /// <summary>Pending flag has been set for longer than we're willing to wait (30 days).
    /// User patched, never rebooted, and we should stop pestering them. Caller clears the
    /// flag without surfacing a user-visible notice.</summary>
    StalePending
}

public class VerificationReport
{
    public VerificationOutcome Outcome { get; set; } = VerificationOutcome.None;
    public string Summary { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime? PatchAppliedAt { get; set; }
    public DateTime? LastBootAt { get; set; }
}

/// <summary>How the native driver came to be active on this machine.</summary>
public enum EnablementSource
{
    /// <summary>Driver not bound — nothing enabled it (or it can't bind).</summary>
    None,
    /// <summary>This tool's registry override keys are present.</summary>
    RegistryPatch,
    /// <summary>ViVeTool/FeatureStore fallback flags are the only enablement evidence.</summary>
    FallbackFlags,
    /// <summary>Driver bound with NO user-driven evidence — Microsoft's official rollout.
    /// The "enable" job is obsolete here; the tool's value shifts to verify/tune/
    /// benchmark/rollback (RD-004).</summary>
    Official,
}

// Post-reboot auditor. Runs once on app startup — if a patch was applied and the machine
// has since rebooted, confirms nvmedisk.sys actually bound. Surfaces a clear, honest
// status when it didn't (e.g. on the post-block Insider builds where the override is a no-op).
public static class PatchVerificationService
{
    /// <summary>
    /// Pure classification of what enabled the native driver. "Official" requires the
    /// driver active with neither registry keys nor fallback evidence — i.e. Windows did
    /// it on its own.
    /// </summary>
    public static EnablementSource ClassifyEnablementSource(bool nativeActive, int overrideKeyCount, bool fallbackEvidence)
    {
        if (!nativeActive) return EnablementSource.None;
        if (overrideKeyCount > 0) return EnablementSource.RegistryPatch;
        if (fallbackEvidence) return EnablementSource.FallbackFlags;
        return EnablementSource.Official;
    }

    // If a user applies but never reboots, we don't want to nag them for months. After
    // this many days we clear the pending flag and treat the patch as abandoned.
    private static readonly TimeSpan PendingMaxAge = TimeSpan.FromDays(30);

    // Small tolerance for NTP re-sync between "apply patch" and "reboot". The recorded
    // patch time is UtcNow at apply; if the system clock then jumps back a few minutes
    // via NTP before the reboot, a strict `lastBoot <= appliedAt` would leave us
    // stuck in AwaitingRestart forever. Five minutes is wide enough for normal NTP
    // corrections and narrow enough that an intentional patch-then-immediate-reopen
    // without reboot still reads as AwaitingRestart.
    private static readonly TimeSpan BootClockSkewTolerance = TimeSpan.FromMinutes(5);

    public static VerificationReport Evaluate(AppConfig config)
    {
        var report = new VerificationReport();

        if (string.IsNullOrWhiteSpace(config.PendingVerificationSince))
        {
            report.Outcome = VerificationOutcome.None;
            return report;
        }

        if (!DateTime.TryParse(
                config.PendingVerificationSince,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var appliedAtRaw))
        {
            // Corrupt timestamp — treat as stale and clear.
            report.Outcome = VerificationOutcome.StalePending;
            report.Summary = "Patch verification timestamp was invalid";
            report.Detail = "The saved pending-verification timestamp could not be parsed. Clearing it so startup verification can recover cleanly.";
            return report;
        }
        // Force UTC so downstream comparisons don't silently mix zones if a future code
        // change writes Local instead of UTC.
        var appliedAt = appliedAtRaw.Kind switch
        {
            DateTimeKind.Utc => appliedAtRaw,
            DateTimeKind.Local => appliedAtRaw.ToUniversalTime(),
            _ => DateTime.SpecifyKind(appliedAtRaw, DateTimeKind.Utc)
        };
        report.PatchAppliedAt = appliedAt;

        // Time-box: patch older than N days with no reboot ever confirmed is abandoned.
        if (DateTime.UtcNow - appliedAt > PendingMaxAge)
        {
            report.Outcome = VerificationOutcome.StalePending;
            report.Summary = "Patch state too old to verify";
            report.Detail = $"The patch was applied more than {(int)PendingMaxAge.TotalDays} days ago without a confirmed restart. Clearing the pending flag.";
            return report;
        }

        var lastBoot = TryGetLastBootTime();
        report.LastBootAt = lastBoot;

        // Waiting on the user to actually restart — don't pester yet. The skew tolerance
        // absorbs small NTP corrections that would otherwise keep us stuck.
        if (lastBoot is null || lastBoot <= appliedAt - BootClockSkewTolerance)
        {
            report.Outcome = VerificationOutcome.AwaitingRestart;
            report.Summary = "Patch applied — restart pending";
            report.Detail = "The registry changes are in place. They take effect after the next restart.";
            return report;
        }

        var status = RegistryService.GetPatchStatus();
        var native = DriveService.TestNativeNVMeActive();
        bool fallbackEvidence;
        try { fallbackEvidence = FeatureStoreWriterService.HasFallbackEvidence(); }
        catch { fallbackEvidence = false; }

        var (outcome, summary, detail) = ClassifyPostRebootState(
            native.IsActive, native.ActiveDriver, status.Count, fallbackEvidence);
        report.Outcome = outcome;
        report.Summary = summary;
        report.Detail = detail;
        return report;
    }

    /// <summary>
    /// Pure post-reboot classification — separated from the WMI/registry probes so the
    /// full truth table is unit-testable. Precedence:
    ///   1. Driver bound → Confirmed (regardless of which route enabled it).
    ///   2. Not bound + fallback evidence → FlagsEnabledNotBound (the fallback itself
    ///      failed; suggesting it again would misdiagnose — ViVe issue #164, 26200.8524+).
    ///   3. Not bound + no evidence + no keys → Reverted.
    ///   4. Not bound + no evidence + keys present → OverrideBlocked (route to fallback).
    /// </summary>
    internal static (VerificationOutcome Outcome, string Summary, string Detail) ClassifyPostRebootState(
        bool nativeActive, string? activeDriver, int overrideKeyCount, bool fallbackEvidence)
    {
        if (nativeActive)
        {
            var detail = overrideKeyCount > 0
                ? $"nvmedisk.sys is bound ({activeDriver}). Your patch is working as intended."
                : $"nvmedisk.sys is bound ({activeDriver}). No registry override keys are present — " +
                  "enablement is via the ViVeTool/FeatureStore fallback or an official Windows rollout.";
            return (VerificationOutcome.Confirmed, "Native NVMe driver is active", detail);
        }

        if (fallbackEvidence)
        {
            return (VerificationOutcome.FlagsEnabledNotBound,
                "Feature flags enabled but the driver cannot bind on this build",
                "The ViVeTool/FeatureStore fallback flags are enabled and the machine has rebooted, " +
                "but Windows is still using the legacy stornvme.sys driver. On builds 26200.8524 and " +
                "later, stornvme no longer exposes the compatible ID that nvmedisk.inf matches, so the " +
                "native driver cannot bind by any supported means (thebookisclosed/ViVe issue #164). " +
                "There is currently no working enablement path on this build — you can leave the flags " +
                "in place (harmless) or remove the patch and wait for Microsoft's official rollout.");
        }

        if (overrideKeyCount == 0)
        {
            // User (or a Windows update) wiped the keys between reboot and now. Silent.
            return (VerificationOutcome.Reverted,
                "Patch no longer present",
                "Registry keys are gone — likely uninstalled or reverted by a Windows update.");
        }

        // Keys present + rebooted + driver NOT bound. This is the Feb/Mar 2026 block signature.
        return (VerificationOutcome.OverrideBlocked,
            "Patch applied but inactive on this build",
            "The registry keys are set, but Windows is still using the legacy stornvme.sys driver. " +
            "Microsoft began neutering this override path on recent Insider builds in early 2026. " +
            "You can remove the patch safely, or try the ViVeTool fallback (the app selects the right feature IDs for your Windows build) " +
            "covered on the project's GitHub README.");
    }

    public static void MarkPending(AppConfig config)
    {
        config.PendingVerificationSince = DateTime.UtcNow.ToString("o");
        config.PendingVerificationProfile = config.PatchProfile.ToString();
        config.LastVerifiedProfile = null;
        config.LastVerificationResult = null;
    }

    public static void Clear(AppConfig config, VerificationReport report)
    {
        var verifiedProfile = string.IsNullOrWhiteSpace(config.PendingVerificationProfile)
            ? config.PatchProfile.ToString()
            : config.PendingVerificationProfile;
        config.PendingVerificationSince = null;
        config.PendingVerificationProfile = null;
        config.LastVerifiedProfile = verifiedProfile;
        config.LastVerificationResult = report.Outcome.ToString();
    }

    private static DateTime? TryGetLastBootTime()
    {
        // Win32_OperatingSystem.LastBootUpTime is the canonical boot timestamp — survives
        // fast startup / hybrid boot (which Get-Uptime famously doesn't).
        try
        {
            using var search = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            using var collection = search.Get();
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject os) continue;
                using (os)
                {
                    var dmtf = os["LastBootUpTime"] as string;
                    if (string.IsNullOrWhiteSpace(dmtf)) return null;
                    // DMTF format: yyyyMMddHHmmss.ffffff+zzz — ManagementDateTimeConverter handles it.
                    return ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime();
                }
            }
        }
        catch
        {
            // WMI can fail transiently during early startup or on hardened SKUs. Return null
            // so the caller treats this as "can't tell yet" rather than "patch broken".
        }
        return null;
    }
}
