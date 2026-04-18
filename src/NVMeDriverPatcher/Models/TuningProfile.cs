namespace NVMeDriverPatcher.Models;

/// <summary>
/// Represents a set of StorNVMe driver parameters that can be tuned via the registry.
/// Maps to HKLM\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device.
/// </summary>
public class TuningProfile
{
    // ========================================================================
    // Registry Path Constants
    // ========================================================================

    /// <summary>StorNVMe device parameters registry path (relative to HKLM).</summary>
    public const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device";

    /// <summary>Full registry path for display/logging.</summary>
    public const string RegistryFullPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device";

    // ========================================================================
    // Registry Value Names
    // ========================================================================

    public const string Key_QueueDepth = "IoQueueDepth";
    public const string Key_MaxReadSplit = "NvmeMaxReadSplit";
    public const string Key_MaxWriteSplit = "NvmeMaxWriteSplit";
    public const string Key_IoSubmissionQueueCount = "IoSubmissionQueueCount";
    public const string Key_IdlePowerTimeout = "IdlePowerTimeout";
    public const string Key_StandbyPowerTimeout = "StandbyPowerTimeout";

    // ========================================================================
    // Tunable Parameters
    // ========================================================================

    /// <summary>
    /// I/O Queue Depth per submission queue.
    /// Higher values allow more outstanding I/O commands, improving throughput at the cost of latency.
    /// Default: 32 (Windows default), Performance: 128, PowerSave: 16.
    /// </summary>
    public int? QueueDepth { get; set; }

    /// <summary>
    /// Maximum read I/O split size in sectors.
    /// Controls how large reads are broken up before submission to the controller.
    /// Default: 128 (64KB), Performance: 512 (256KB).
    /// </summary>
    public int? NvmeMaxReadSplit { get; set; }

    /// <summary>
    /// Maximum write I/O split size in sectors.
    /// Controls how large writes are broken up before submission to the controller.
    /// Default: 128 (64KB), Performance: 512 (256KB).
    /// </summary>
    public int? NvmeMaxWriteSplit { get; set; }

    /// <summary>
    /// Number of I/O submission queues created by stornvme.
    /// Usually maps to CPU core count. Override to limit or expand queue parallelism.
    /// Default: 0 (auto, one per CPU core).
    /// </summary>
    public int? IoSubmissionQueueCount { get; set; }

    /// <summary>
    /// Idle power management timeout in milliseconds.
    /// How long the controller waits at idle before entering a low-power state.
    /// Default: 100, Performance: 0 (disabled), PowerSave: 50.
    /// </summary>
    public int? IdlePowerTimeout { get; set; }

    /// <summary>
    /// Standby power management timeout in milliseconds.
    /// How long before the controller enters deeper standby.
    /// Default: 0 (disabled), PowerSave: 2000.
    /// </summary>
    public int? StandbyPowerTimeout { get; set; }

    /// <summary>Profile display name.</summary>
    public string Name { get; set; } = "Custom";

    /// <summary>Profile description for UI tooltips.</summary>
    public string Description { get; set; } = string.Empty;

    // ========================================================================
    // Built-in Presets
    //
    // NOTE: These are shared singletons used for read-only comparisons in the UI.
    // Do NOT mutate their properties; clone first if you need to customize.
    // ========================================================================

    /// <summary>
    /// Performance profile: maximizes throughput and IOPS.
    /// Best for desktop workstations and gaming PCs with adequate cooling.
    /// </summary>
    public static TuningProfile Performance { get; } = new()
    {
        Name = "Performance",
        Description = "Maximum throughput and IOPS. Disables power management. Best for desktops with good cooling.",
        QueueDepth = 128,
        NvmeMaxReadSplit = 512,
        NvmeMaxWriteSplit = 512,
        IoSubmissionQueueCount = 0,  // Auto (all cores)
        IdlePowerTimeout = 0,        // Disabled
        StandbyPowerTimeout = 0      // Disabled
    };

    /// <summary>
    /// Balanced profile: Windows defaults with minor tuning.
    /// Good all-around for most systems.
    /// </summary>
    public static TuningProfile Balanced { get; } = new()
    {
        Name = "Balanced",
        Description = "Windows defaults with standard power management. Safe for all systems.",
        QueueDepth = 32,
        NvmeMaxReadSplit = 128,
        NvmeMaxWriteSplit = 128,
        IoSubmissionQueueCount = 0,
        IdlePowerTimeout = 100,
        StandbyPowerTimeout = 0
    };

    /// <summary>
    /// PowerSave profile: reduces queue depth and enables aggressive power management.
    /// Best for laptops and battery-powered devices.
    /// </summary>
    public static TuningProfile PowerSave { get; } = new()
    {
        Name = "Power Save",
        Description = "Reduced queue depth with aggressive power management. Best for laptops on battery.",
        QueueDepth = 16,
        NvmeMaxReadSplit = 64,
        NvmeMaxWriteSplit = 64,
        IoSubmissionQueueCount = 0,
        IdlePowerTimeout = 50,
        StandbyPowerTimeout = 2000
    };

    private static readonly TuningProfile[] _presets = [Performance, Balanced, PowerSave];

    /// <summary>
    /// Returns all built-in presets for UI enumeration.
    /// </summary>
    public static TuningProfile[] GetPresets() => _presets;
}
