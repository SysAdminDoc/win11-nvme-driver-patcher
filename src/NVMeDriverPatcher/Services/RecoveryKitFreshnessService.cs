using System.IO;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum RecoveryKitFreshness
{
    Missing,
    Fresh,
    Stale,
    Unknown
}

public class RecoveryKitFreshnessReport
{
    public RecoveryKitFreshness State { get; set; }
    public int? AgeDays { get; set; }
    public string? Path { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool ShouldNag => State is RecoveryKitFreshness.Missing or RecoveryKitFreshness.Stale;
}

// Computes freshness of the saved recovery kit so the hero card can render a persistent
// "Generate recovery kit now" CTA when the current kit is missing or older than 30 days.
// Closes ROADMAP §1.5 — previously the freshness check only fired inside the Apply-Patch
// confirmation dialog, which users can bypass with Skip-Warnings.
public static class RecoveryKitFreshnessService
{
    public const int StaleAfterDays = 30;

    public static RecoveryKitFreshnessReport Evaluate(AppConfig config)
    {
        var report = new RecoveryKitFreshnessReport { Path = config.LastRecoveryKitPath };

        if (string.IsNullOrWhiteSpace(config.LastRecoveryKitPath))
        {
            report.State = RecoveryKitFreshness.Missing;
            report.Summary = "No recovery kit has been generated. Generate one before the next apply.";
            return report;
        }

        try
        {
            if (!Directory.Exists(config.LastRecoveryKitPath))
            {
                report.State = RecoveryKitFreshness.Missing;
                report.Summary = $"Recovery kit missing at {config.LastRecoveryKitPath}. Regenerate.";
                return report;
            }

            var latest = Directory.EnumerateFiles(config.LastRecoveryKitPath)
                .Select(f => new FileInfo(f))
                .Select(f => f.LastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            if (latest == DateTime.MinValue)
            {
                report.State = RecoveryKitFreshness.Unknown;
                report.Summary = "Recovery kit folder exists but contains no files.";
                return report;
            }

            var age = (int)(DateTime.UtcNow - latest).TotalDays;
            report.AgeDays = age;
            report.State = age > StaleAfterDays
                ? RecoveryKitFreshness.Stale
                : RecoveryKitFreshness.Fresh;
            report.Summary = report.State == RecoveryKitFreshness.Stale
                ? $"Recovery kit is {age} day(s) old — regenerate before the next apply."
                : $"Recovery kit is {age} day(s) old — fresh.";
        }
        catch (Exception ex)
        {
            report.State = RecoveryKitFreshness.Unknown;
            report.Summary = $"Freshness check failed: {ex.Message}";
        }
        return report;
    }
}
