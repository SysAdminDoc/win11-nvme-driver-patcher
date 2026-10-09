using System.IO;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class BenchmarkBaseline
{
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public double ReadIops { get; set; }
    public double WriteIops { get; set; }
    public double ReadLatencyMs { get; set; }
    public double WriteLatencyMs { get; set; }
    public double DesktopReadIops { get; set; }
    public double DesktopWriteIops { get; set; }
    public double DesktopReadLatencyMs { get; set; }
    public double DesktopWriteLatencyMs { get; set; }
    public string Notes { get; set; } = string.Empty;
    // DiskSpd that produced the numbers; empty when the baseline predates this being recorded.
    public string DiskSpdVersion { get; set; } = string.Empty;
    public string DiskSpdSha256 { get; set; } = string.Empty;
}

public class RegressionVerdict
{
    public bool Regressed { get; set; }
    public double ReadDeltaPercent { get; set; }
    public double WriteDeltaPercent { get; set; }
    public bool HasDesktopComparison { get; set; }
    public double DesktopReadDeltaPercent { get; set; }
    public double DesktopWriteDeltaPercent { get; set; }
    public string Summary { get; set; } = string.Empty;
    // False when the two runs came from different DiskSpd builds; the deltas are then left at 0.
    public bool Comparable { get; set; } = true;
}

// Persistent rolling baseline of benchmark results. Pairs with the `scheduled-benchmark`
// CLI subcommand to detect long-term regressions (Windows Update quietly altering driver
// behavior is the common cause). Stores under %ProgramData%\NVMePatcher\baseline.json.
public static class AutoBenchmarkService
{
    private const string BaselineFile = "baseline.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string BaselinePath(AppConfig config) => Path.Combine(
        string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir,
        BaselineFile);

    public static BenchmarkBaseline? LoadBaseline(AppConfig config)
    {
        try
        {
            var path = BaselinePath(config);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<BenchmarkBaseline>(json);
        }
        catch { return null; }
    }

    /// <summary>
    /// Publishes the baseline in one step. False means it wasn't written and says why through
    /// <paramref name="log"/>; the previous baseline, if any, is still intact.
    /// </summary>
    public static bool SaveBaseline(AppConfig config, BenchmarkBaseline baseline, Action<string>? log = null)
    {
        var path = BaselinePath(config);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(baseline, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log?.Invoke($"[WARN] Benchmark baseline not saved to {path}: {ex.Message}");
            return false;
        }
    }

    public static BenchmarkBaseline? LoadBaselineFromPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<BenchmarkBaseline>(json);
        }
        catch { return null; }
    }

    /// <summary>Project a recorded benchmark result into the baseline shape so it can be the
    /// "current" side of a regression comparison.</summary>
    public static BenchmarkBaseline FromResult(BenchmarkResult result) => new()
    {
        CreatedAt = string.IsNullOrWhiteSpace(result.Timestamp) ? DateTime.UtcNow.ToString("o") : result.Timestamp,
        ReadIops = result.Read?.IOPS ?? 0,
        WriteIops = result.Write?.IOPS ?? 0,
        ReadLatencyMs = result.Read?.AvgLatencyMs ?? 0,
        WriteLatencyMs = result.Write?.AvgLatencyMs ?? 0,
        DesktopReadIops = result.Desktop?.Read?.IOPS ?? 0,
        DesktopWriteIops = result.Desktop?.Write?.IOPS ?? 0,
        DesktopReadLatencyMs = result.Desktop?.Read?.AvgLatencyMs ?? 0,
        DesktopWriteLatencyMs = result.Desktop?.Write?.AvgLatencyMs ?? 0,
        Notes = result.Label ?? string.Empty,
        DiskSpdVersion = result.DiskSpdVersion ?? string.Empty,
        DiskSpdSha256 = result.DiskSpdSha256 ?? string.Empty
    };

    public static RegressionVerdict Compare(BenchmarkBaseline baseline, BenchmarkBaseline current, double thresholdPercent)
    {
        var mismatch = BenchmarkService.DescribeDiskSpdMismatch(
            baseline.DiskSpdVersion, baseline.DiskSpdSha256, current.DiskSpdVersion, current.DiskSpdSha256);
        if (mismatch is not null)
            return new RegressionVerdict { Comparable = false, Summary = "NOT COMPARABLE: " + mismatch };

        double readDelta = PercentDelta(baseline.ReadIops, current.ReadIops);
        double writeDelta = PercentDelta(baseline.WriteIops, current.WriteIops);
        bool hasDesktopRead = baseline.DesktopReadIops > 0 && current.DesktopReadIops > 0;
        bool hasDesktopWrite = baseline.DesktopWriteIops > 0 && current.DesktopWriteIops > 0;
        double desktopReadDelta = PercentDelta(baseline.DesktopReadIops, current.DesktopReadIops);
        double desktopWriteDelta = PercentDelta(baseline.DesktopWriteIops, current.DesktopWriteIops);
        bool regressed = readDelta <= -thresholdPercent ||
                         writeDelta <= -thresholdPercent ||
                         (hasDesktopRead && desktopReadDelta <= -thresholdPercent) ||
                         (hasDesktopWrite && desktopWriteDelta <= -thresholdPercent);
        bool hasDesktopComparison = hasDesktopRead || hasDesktopWrite;
        var status = regressed ? "REGRESSION" : "OK";
        var highQueue = $"high-QD read {FormatDelta(readDelta)} / write {FormatDelta(writeDelta)}";
        var summary = $"{status}: {highQueue}";

        if (hasDesktopComparison)
        {
            var desktopQueue =
                $"desktop QD1 read {FormatDelta(desktopReadDelta)} / write {FormatDelta(desktopWriteDelta)}";
            bool highImproved = readDelta > 0 || writeDelta > 0;
            bool desktopDidNotImprove =
                (!hasDesktopRead || desktopReadDelta <= 0) &&
                (!hasDesktopWrite || desktopWriteDelta <= 0);
            summary = highImproved && desktopDidNotImprove
                ? $"{status}: High-QD improved while desktop QD1 did not: {highQueue}; {desktopQueue}"
                : $"{status}: {highQueue}; {desktopQueue}";
        }

        return new RegressionVerdict
        {
            Regressed = regressed,
            ReadDeltaPercent = readDelta,
            WriteDeltaPercent = writeDelta,
            HasDesktopComparison = hasDesktopComparison,
            DesktopReadDeltaPercent = desktopReadDelta,
            DesktopWriteDeltaPercent = desktopWriteDelta,
            Summary = regressed
                ? $"{summary} (threshold ±{thresholdPercent}%)"
                : summary
        };
    }

    private static string FormatDelta(double value) => $"{value:+0.0;-0.0}%";

    internal static double PercentDelta(double baseline, double current)
    {
        if (baseline <= 0) return 0;
        return (current - baseline) / baseline * 100.0;
    }
}
