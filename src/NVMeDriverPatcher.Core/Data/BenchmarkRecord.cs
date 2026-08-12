using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NVMeDriverPatcher.Data;

[Table("Benchmarks")]
public class BenchmarkRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public string Label { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    public double ReadIOPS { get; set; }

    public double ReadThroughputMBs { get; set; }

    public double ReadLatencyMs { get; set; }

    public double WriteIOPS { get; set; }

    public double WriteThroughputMBs { get; set; }

    public double WriteLatencyMs { get; set; }

    public string DesktopProfileId { get; set; } = "desktop-qd1";

    public string DesktopProfileName { get; set; } = "Desktop QD1";

    public int DesktopThreads { get; set; } = 1;

    public int DesktopOutstandingIo { get; set; } = 1;

    public int DesktopDurationSeconds { get; set; } = 30;

    public double DesktopReadIOPS { get; set; }

    public double DesktopReadThroughputMBs { get; set; }

    public double DesktopReadLatencyMs { get; set; }

    public double DesktopWriteIOPS { get; set; }

    public double DesktopWriteThroughputMBs { get; set; }

    public double DesktopWriteLatencyMs { get; set; }

    public string? Notes { get; set; }
}
