using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NVMeDriverPatcher.Data;

[Table("Telemetry")]
public class TelemetryRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int DriveNumber { get; set; }

    public DateTime Timestamp { get; set; }

    public int TemperatureCelsius { get; set; }

    public int AvailableSparePercent { get; set; }

    public int PercentageUsed { get; set; }

    public long DataUnitsRead { get; set; }

    public long DataUnitsWritten { get; set; }

    public long PowerOnHours { get; set; }

    public int MediaErrors { get; set; }

    public int UnsafeShutdowns { get; set; }
}
