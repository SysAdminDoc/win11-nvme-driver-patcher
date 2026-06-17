using System.Management;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class ReliabilityPoint
{
    public DateTime Timestamp { get; set; }
    public double Index { get; set; }  // 1.0 (worst) .. 10.0 (best)
}

public class ReliabilityCorrelationReport
{
    public List<ReliabilityPoint> Series { get; set; } = new();
    public double? PrePatchAverage { get; set; }
    public double? PostPatchAverage { get; set; }
    public double? Delta => (PrePatchAverage is null || PostPatchAverage is null)
        ? null
        : PostPatchAverage - PrePatchAverage;
    public DateTime? PatchAppliedAt { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool DataAvailable { get; set; }
}

// Correlates Windows Reliability Monitor stability index against the patch apply timestamp.
// Answers "is my system measurably less stable since the patch?" with data instead of vibes.
// Stability index is a daily score 1.0–10.0 stored in Win32_ReliabilityStabilityMetrics.
public static class ReliabilityService
{
    // How far back we look either side of the patch apply. 30 days is long enough to absorb
    // a transient bad week pre-patch without drowning the signal in a stable prior quarter.
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromDays(30);

    public static ReliabilityCorrelationReport GetCorrelation(DateTime? patchAppliedAt)
    {
        var report = new ReliabilityCorrelationReport { PatchAppliedAt = patchAppliedAt };

        try
        {
            // The WMI class only exists when the Reliability Monitor service has populated data
            // (usually 24h after a fresh install). Guard the whole block so missing data degrades
            // gracefully to "not available" instead of an exception at startup.
            using var search = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT TimeGenerated, SystemStabilityIndex FROM Win32_ReliabilityStabilityMetrics");
            using var collection = search.Get();
            var cutoff = DateTime.UtcNow - LookbackWindow;
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject row) continue;
                using (row)
                {
                    try
                    {
                        var dmtf = row["TimeGenerated"] as string;
                        if (string.IsNullOrWhiteSpace(dmtf)) continue;
                        var ts = ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime();
                        if (ts < cutoff) continue;
                        var idx = Convert.ToDouble(row["SystemStabilityIndex"], System.Globalization.CultureInfo.InvariantCulture);
                        report.Series.Add(new ReliabilityPoint { Timestamp = ts, Index = idx });
                    }
                    catch { /* one malformed row shouldn't nuke the whole query */ }
                }
            }
        }
        catch
        {
            report.DataAvailable = false;
            report.Summary = "Reliability Monitor data unavailable (service not started or query denied).";
            return report;
        }

        report.Series = report.Series.OrderBy(p => p.Timestamp).ToList();
        report.DataAvailable = report.Series.Count > 0;
        if (!report.DataAvailable)
        {
            report.Summary = "No Reliability Monitor history in the last 30 days.";
            return report;
        }

        if (patchAppliedAt is DateTime patchTs)
        {
            var pre = report.Series.Where(p => p.Timestamp < patchTs).ToList();
            var post = report.Series.Where(p => p.Timestamp >= patchTs).ToList();
            if (pre.Count > 0) report.PrePatchAverage = pre.Average(p => p.Index);
            if (post.Count > 0) report.PostPatchAverage = post.Average(p => p.Index);

            if (report.PrePatchAverage is not null && report.PostPatchAverage is not null)
            {
                var delta = report.Delta ?? 0;
                var arrow = delta >= 0 ? "↑" : "↓";
                report.Summary =
                    $"Stability {report.PrePatchAverage:F1} → {report.PostPatchAverage:F1} {arrow} ({delta:+0.0;-0.0}) " +
                    $"from {pre.Count} pre-patch / {post.Count} post-patch days.";
            }
            else if (report.PostPatchAverage is not null)
            {
                report.Summary = $"Post-patch stability index: {report.PostPatchAverage:F1} ({post.Count} days). No pre-patch baseline.";
            }
            else
            {
                report.Summary = "Patch applied but no post-patch reliability samples yet.";
            }
        }
        else
        {
            var avg = report.Series.Average(p => p.Index);
            report.Summary = $"Stability index average: {avg:F1} over {report.Series.Count} days.";
        }

        return report;
    }
}
