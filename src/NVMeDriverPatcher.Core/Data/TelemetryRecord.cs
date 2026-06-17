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

    /// <summary>
    /// UTC timestamp of the sample. Rows written before the v4.7 hardening pass may have been
    /// stored in local time — we deliberately do NOT attempt to normalize those in place.
    /// Telemetry has a 90-day retention window (<see cref="NVMeDriverPatcher.Services.DataService.PruneTelemetry"/>),
    /// so any historical local-time rows self-heal within three months. Attempting an in-place
    /// conversion would risk interpreting an ambiguous DST-transition row twice (permanent
    /// data loss), which is a worse outcome than the minor wear-trend inaccuracy that retention
    /// naturally repairs. See CHANGELOG v4.7 "Known limitations" for context.
    /// </summary>
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
