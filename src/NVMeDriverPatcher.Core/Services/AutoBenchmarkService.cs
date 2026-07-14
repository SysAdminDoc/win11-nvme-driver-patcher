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
    public string Notes { get; set; } = string.Empty;
}

public class RegressionVerdict
{
    public bool Regressed { get; set; }
    public double ReadDeltaPercent { get; set; }
    public double WriteDeltaPercent { get; set; }
    public string Summary { get; set; } = string.Empty;
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

    public static void SaveBaseline(AppConfig config, BenchmarkBaseline baseline)
    {
        var path = BaselinePath(config);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(baseline, JsonOptions));
        File.Move(tmp, path, overwrite: true);
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
        Notes = result.Label ?? string.Empty
    };

    public static RegressionVerdict Compare(BenchmarkBaseline baseline, BenchmarkBaseline current, double thresholdPercent)
    {
        double readDelta = PercentDelta(baseline.ReadIops, current.ReadIops);
        double writeDelta = PercentDelta(baseline.WriteIops, current.WriteIops);
        bool regressed = readDelta <= -thresholdPercent || writeDelta <= -thresholdPercent;
        return new RegressionVerdict
        {
            Regressed = regressed,
            ReadDeltaPercent = readDelta,
            WriteDeltaPercent = writeDelta,
            Summary = regressed
                ? $"REGRESSION: read {readDelta:+0.0;-0.0}% / write {writeDelta:+0.0;-0.0}% (threshold ±{thresholdPercent}%)"
                : $"OK: read {readDelta:+0.0;-0.0}% / write {writeDelta:+0.0;-0.0}%"
        };
    }

    internal static double PercentDelta(double baseline, double current)
    {
        if (baseline <= 0) return 0;
        return (current - baseline) / baseline * 100.0;
    }
}
