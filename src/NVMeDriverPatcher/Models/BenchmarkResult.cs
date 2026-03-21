namespace NVMeDriverPatcher.Models;

public class BenchmarkResult
{
    public string Label { get; set; } = "benchmark";
    public string Timestamp { get; set; } = string.Empty;
    public BenchmarkMetrics Read { get; set; } = new();
    public BenchmarkMetrics Write { get; set; } = new();
}

public class BenchmarkMetrics
{
    public double IOPS { get; set; }
    public double ThroughputMBs { get; set; }
    public double AvgLatencyMs { get; set; }
}
