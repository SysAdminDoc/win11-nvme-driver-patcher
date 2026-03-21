namespace NVMeDriverPatcher.Models;

/// <summary>
/// Parsed NVMe SMART / Health Information from Log Page 02h.
/// All values decoded from the raw 512-byte NVME_HEALTH_INFO_LOG struct.
/// </summary>
public class NVMeHealthData
{
    // -- Critical Warning Flags --
    public byte CriticalWarningRaw { get; set; }
    public bool AvailableSpareBelow { get; set; }
    public bool TemperatureExceeded { get; set; }
    public bool ReliabilityDegraded { get; set; }
    public bool ReadOnlyMode { get; set; }
    public bool VolatileMemoryBackupFailed { get; set; }

    // -- Temperature --
    public ushort TemperatureKelvin { get; set; }
    public int TemperatureCelsius => TemperatureKelvin > 0 ? TemperatureKelvin - 273 : 0;

    // -- Spare / Endurance --
    public byte AvailableSpare { get; set; }
    public byte AvailableSpareThreshold { get; set; }
    public byte PercentageUsed { get; set; }

    // -- Data Throughput (units of 512 bytes * 1000; use decimal for 128-bit values) --
    public decimal DataUnitsRead { get; set; }
    public decimal DataUnitsWritten { get; set; }

    /// <summary>Total bytes read, computed from DataUnitsRead * 512 * 1000.</summary>
    public decimal TotalBytesRead => DataUnitsRead * 512_000m;

    /// <summary>Total bytes written, computed from DataUnitsWritten * 512 * 1000.</summary>
    public decimal TotalBytesWritten => DataUnitsWritten * 512_000m;

    // -- Host Commands --
    public decimal HostReadCommands { get; set; }
    public decimal HostWriteCommands { get; set; }

    // -- Controller Lifetime --
    public decimal ControllerBusyTime { get; set; }
    public decimal PowerCycles { get; set; }
    public decimal PowerOnHours { get; set; }
    public decimal UnsafeShutdowns { get; set; }

    // -- Error Tracking --
    public decimal MediaErrors { get; set; }
    public decimal ErrorLogEntries { get; set; }

    // -- Thermal Management --
    public uint WarningCompositeTemp { get; set; }
    public uint CriticalCompositeTemp { get; set; }

    // -- Temperature Sensors (Kelvin, 0 = not implemented) --
    public ushort[] TemperatureSensors { get; set; } = new ushort[8];

    // -- Thermal Throttling --
    public uint ThermalMgmtTemp1TransitionCount { get; set; }
    public uint ThermalMgmtTemp2TransitionCount { get; set; }
    public uint TotalTimeThermalMgmt1 { get; set; }
    public uint TotalTimeThermalMgmt2 { get; set; }

    /// <summary>Drive number this data was read from.</summary>
    public int DriveNumber { get; set; }

    /// <summary>Timestamp when this data was captured.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Returns a human-readable summary string.
    /// </summary>
    public string ToSummary()
    {
        var lines = new List<string>
        {
            $"Temperature: {TemperatureCelsius}C ({TemperatureKelvin}K)",
            $"Available Spare: {AvailableSpare}% (Threshold: {AvailableSpareThreshold}%)",
            $"Percentage Used: {PercentageUsed}%",
            $"Power On Hours: {PowerOnHours:N0}",
            $"Power Cycles: {PowerCycles:N0}",
            $"Unsafe Shutdowns: {UnsafeShutdowns:N0}",
            $"Media Errors: {MediaErrors:N0}",
            $"Error Log Entries: {ErrorLogEntries:N0}",
            $"Data Read: {FormatBytes(TotalBytesRead)}",
            $"Data Written: {FormatBytes(TotalBytesWritten)}"
        };

        if (CriticalWarningRaw != 0)
            lines.Insert(0, $"CRITICAL WARNING: 0x{CriticalWarningRaw:X2}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(decimal bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        int index = 0;
        decimal value = bytes;
        while (value >= 1024m && index < units.Length - 1)
        {
            value /= 1024m;
            index++;
        }
        return $"{value:N2} {units[index]}";
    }
}

/// <summary>
/// A single time-series data point for NVMe telemetry polling.
/// Stores a snapshot of all key values at a specific timestamp for graphing/history.
/// </summary>
public class TelemetryDataPoint
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int DriveNumber { get; set; }

    // Instant values
    public int TemperatureCelsius { get; set; }
    public byte AvailableSpare { get; set; }
    public byte PercentageUsed { get; set; }

    // Cumulative counters (snapshot)
    public decimal DataUnitsRead { get; set; }
    public decimal DataUnitsWritten { get; set; }
    public decimal HostReadCommands { get; set; }
    public decimal HostWriteCommands { get; set; }
    public decimal ControllerBusyTime { get; set; }
    public decimal PowerCycles { get; set; }
    public decimal PowerOnHours { get; set; }
    public decimal UnsafeShutdowns { get; set; }
    public decimal MediaErrors { get; set; }
    public decimal ErrorLogEntries { get; set; }

    // Thermal
    public uint WarningCompositeTemp { get; set; }
    public uint CriticalCompositeTemp { get; set; }

    /// <summary>
    /// Creates a TelemetryDataPoint from a full NVMeHealthData snapshot.
    /// </summary>
    public static TelemetryDataPoint FromHealthData(NVMeHealthData data)
    {
        return new TelemetryDataPoint
        {
            Timestamp = data.Timestamp,
            DriveNumber = data.DriveNumber,
            TemperatureCelsius = data.TemperatureCelsius,
            AvailableSpare = data.AvailableSpare,
            PercentageUsed = data.PercentageUsed,
            DataUnitsRead = data.DataUnitsRead,
            DataUnitsWritten = data.DataUnitsWritten,
            HostReadCommands = data.HostReadCommands,
            HostWriteCommands = data.HostWriteCommands,
            ControllerBusyTime = data.ControllerBusyTime,
            PowerCycles = data.PowerCycles,
            PowerOnHours = data.PowerOnHours,
            UnsafeShutdowns = data.UnsafeShutdowns,
            MediaErrors = data.MediaErrors,
            ErrorLogEntries = data.ErrorLogEntries,
            WarningCompositeTemp = data.WarningCompositeTemp,
            CriticalCompositeTemp = data.CriticalCompositeTemp
        };
    }
}
