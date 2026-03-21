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

    public string? Notes { get; set; }
}
