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

// Post-reboot auditor. Runs once on app startup — if a patch was applied and the machine
// has since rebooted, confirms nvmedisk.sys actually bound. Surfaces a clear, honest
// status when it didn't (e.g. on the post-block Insider builds where the override is a no-op).
public static class PatchVerificationService
{
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

        if (status.Count == 0)
        {
            // User (or a Windows update) wiped the keys between reboot and now. Silent.
            report.Outcome = VerificationOutcome.Reverted;
            report.Summary = "Patch no longer present";
            report.Detail = "Registry keys are gone — likely uninstalled or reverted by a Windows update.";
            return report;
        }

        if (native.IsActive)
        {
            report.Outcome = VerificationOutcome.Confirmed;
            report.Summary = "Native NVMe driver is active";
            report.Detail = $"nvmedisk.sys is bound ({native.ActiveDriver}). Your patch is working as intended.";
            return report;
        }

        // Keys present + rebooted + driver NOT bound. This is the Feb/Mar 2026 block signature.
        report.Outcome = VerificationOutcome.OverrideBlocked;
        report.Summary = "Patch applied but inactive on this build";
        report.Detail =
            "The registry keys are set, but Windows is still using the legacy stornvme.sys driver. " +
            "Microsoft began neutering this override path on recent Insider builds in early 2026. " +
            "You can remove the patch safely, or try the ViVeTool fallback (feature IDs 60786016 and 48433719) " +
            "covered on the project's GitHub README.";
        return report;
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
