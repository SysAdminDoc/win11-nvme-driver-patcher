using System.Text.Json.Serialization;

namespace NVMeDriverPatcher.Models;

public class BenchmarkResult
{
    public string Label { get; set; } = "benchmark";
    public string Timestamp { get; set; } = string.Empty;
    // Read/Write remain the original sustained high-QD profile for compatibility with existing
    // history files and callers. Desktop carries the explicitly lower-queue profile beside it.
    public BenchmarkMetrics Read { get; set; } = new();
    public BenchmarkMetrics Write { get; set; } = new();
    public BenchmarkProfileResult Desktop { get; set; } = new();
}

public class BenchmarkProfileResult
{
    public string ProfileId { get; set; } = "desktop-qd1";
    public string ProfileName { get; set; } = "Desktop QD1";
    public int Threads { get; set; } = 1;
    public int OutstandingIo { get; set; } = 1;
    public int DurationSeconds { get; set; } = 30;
    public BenchmarkMetrics Read { get; set; } = new();
    public BenchmarkMetrics Write { get; set; } = new();

    [JsonIgnore]
    public bool HasMetrics =>
        Read?.IOPS > 0 || Read?.ThroughputMBs > 0 || Read?.AvgLatencyMs > 0 ||
        Write?.IOPS > 0 || Write?.ThroughputMBs > 0 || Write?.AvgLatencyMs > 0;
}

public class BenchmarkMetrics
{
    public double IOPS { get; set; }
    public double ThroughputMBs { get; set; }
    public double AvgLatencyMs { get; set; }
}
