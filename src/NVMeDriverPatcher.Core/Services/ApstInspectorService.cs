using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NVMeDriverPatcher.Interop;

namespace NVMeDriverPatcher.Services;

public class ApstPowerState
{
    public int PowerStateNumber { get; set; }
    public int? IdleTimeMicroseconds { get; set; }
    public double? MaxPowerWatts { get; set; }
    public double? EntryLatencyUs { get; set; }
    public double? ExitLatencyUs { get; set; }
    public bool? NonOperational { get; set; }
}

public class ApstBatteryEstimate
{
    public bool IsLaptop { get; set; }
    /// <summary>True when Windows idles the drive into a low-power state, null when that wasn't reported.</summary>
    public bool? IdleStatesUsed { get; set; }
    public double? ActivePowerWatts { get; set; }
    public double? LowestIdlePowerWatts { get; set; }
    public double? EstimatedIdleSavingsWatts { get; set; }
    public string Impact { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}

/// <summary>The four NVMe idle settings of the active power plan for one power source, in milliseconds.</summary>
public sealed record NvmeIdleSettings(
    int PrimaryIdleTimeoutMs,
    int PrimaryLatencyToleranceMs,
    int SecondaryIdleTimeoutMs,
    int SecondaryLatencyToleranceMs);

public class ApstInspectionReport
{
    /// <summary>
    /// True when Windows moves the drive into a non-operational power state once it's idle, false
    /// when it can't (no state fits the power plan, or a registry override turns it off), and null
    /// when nothing reported it. A missing value never reads as off.
    /// </summary>
    public bool? IdleStatesUsed { get; set; }
    public NvmeIdleSettings? PowerPlanAc { get; set; }
    public NvmeIdleSettings? PowerPlanDc { get; set; }
    public bool OnBattery { get; set; }
    public int? PrimaryIdleState { get; set; }
    public int? SecondaryIdleState { get; set; }

    // stornvme\Parameters\Device values. Microsoft doesn't document these, they aren't among the
    // parameters stornvme is known to read, and neither a 24H2 install nor a retail PC had them.
    // They're reported when present and never decide the idle verdict; the power plan does.
    public bool? ApstEnabledOverride { get; set; }
    public int? ApstIdleTimeout { get; set; }
    public bool NoLowPowerTransitions { get; set; }

    /// <summary>The undocumented value that claims to turn idle off, when one is set
    /// ("NoLowPowerTransitions=1" or "AutonomousPowerStateTransitionEnabled=0").</summary>
    public string? UndocumentedIdleOverride { get; set; }

    public List<ApstPowerState> States { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public ApstBatteryEstimate? BatteryEstimate { get; set; }
}

// Shows how Windows idles the NVMe drive, so laptop users can see the power tradeoff before
// patching. StorNVMe doesn't use the drive's own APST: once the power plan's Primary or Secondary
// NVMe Idle Timeout runs out, it moves the drive into the deepest non-operational state whose
// ENLAT + EXLAT fits that tier's latency tolerance (Microsoft Learn, "NVMe" power management for
// storage devices). This reads those power plan settings and the drive's Identify power table.
public static class ApstInspectorService
{
    private const string ParametersRoot = @"SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device";

    private static readonly Guid DiskSubgroup = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    private static readonly Guid PrimaryIdleTimeoutSetting = new("d639518a-e56d-4345-8af2-b9f32fb26109");
    private static readonly Guid PrimaryLatencyToleranceSetting = new("fc95af4d-40e7-4b6d-835a-56d131dbc80e");
    private static readonly Guid SecondaryIdleTimeoutSetting = new("d3d55efd-c1ff-424e-9dc3-441be7833010");
    private static readonly Guid SecondaryLatencyToleranceSetting = new("dbc9e238-6de9-49e3-92cd-8c2b4946b472");

    public static ApstInspectionReport Inspect()
    {
        var report = new ApstInspectionReport();
        try
        {
            ReadRegistryOverrides(report);
            ApplyIdentifyPowerStates(report, QueryIdentifyPowerStates());
            var (ac, dc) = ReadPowerPlan();
            ApplyIdlePolicy(report, ac, dc, IsOnBattery());
            report.BatteryEstimate = EstimateBatteryImpact(report);
        }
        catch (Exception ex)
        {
            report.Summary = $"APST inspection failed: {ex.Message}";
        }
        return report;
    }

    private static void ReadRegistryOverrides(ApstInspectionReport report)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(ParametersRoot);
        if (key is null) return;

        if (key.GetValue("AutonomousPowerStateTransitionEnabled") is int apst) report.ApstEnabledOverride = apst != 0;
        if (key.GetValue("ApstIdleTimeout") is int timeout) report.ApstIdleTimeout = timeout;
        report.NoLowPowerTransitions = key.GetValue("NoLowPowerTransitions") is int nl && nl != 0;

        for (int i = 0; i < 32; i++)
        {
            if (key.GetValue($"PowerState{i}_IdleTimeUs") is not int idle) continue;
            var state = new ApstPowerState { PowerStateNumber = i, IdleTimeMicroseconds = idle };
            if (key.GetValue($"PowerState{i}_EntryLatencyUs") is int entry) state.EntryLatencyUs = entry;
            if (key.GetValue($"PowerState{i}_ExitLatencyUs") is int exit) state.ExitLatencyUs = exit;
            if (key.GetValue($"PowerState{i}_NonOperational") is int no) state.NonOperational = no != 0;
            report.States.Add(state);
        }
    }

    private static IReadOnlyList<NvmePowerStateDescriptor> QueryIdentifyPowerStates()
    {
        try
        {
            foreach (var drive in DriveService.GetSystemDrives().Where(d => d.IsNVMe))
            {
                var identify = NvmeIdentifyService.Query(drive.Number);
                if (identify.Success && identify.PowerStates.Count > 0)
                    return identify.PowerStates;
            }
        }
        catch { }

        return Array.Empty<NvmePowerStateDescriptor>();
    }

    private static (NvmeIdleSettings? Ac, NvmeIdleSettings? Dc) ReadPowerPlan()
    {
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0 || schemePtr == IntPtr.Zero)
                return (null, null);
            try
            {
                var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
                return (ReadIdleSettings(scheme, ac: true), ReadIdleSettings(scheme, ac: false));
            }
            finally { NativeMethods.LocalFree(schemePtr); }
        }
        catch { return (null, null); }
    }

    private static NvmeIdleSettings? ReadIdleSettings(Guid scheme, bool ac)
    {
        int? Read(Guid setting)
        {
            uint value;
            uint rc = ac
                ? NativeMethods.PowerReadACValueIndex(IntPtr.Zero, scheme, DiskSubgroup, setting, out value)
                : NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, scheme, DiskSubgroup, setting, out value);
            return rc == 0 ? (int)Math.Min(value, int.MaxValue) : null;
        }

        return Read(PrimaryIdleTimeoutSetting) is int primaryIdle &&
               Read(PrimaryLatencyToleranceSetting) is int primaryLatency &&
               Read(SecondaryIdleTimeoutSetting) is int secondaryIdle &&
               Read(SecondaryLatencyToleranceSetting) is int secondaryLatency
            ? new NvmeIdleSettings(primaryIdle, primaryLatency, secondaryIdle, secondaryLatency)
            : null;
    }

    private static bool IsOnBattery()
    {
        try { return NativeMethods.GetSystemPowerStatus(out var status) && status.ACLineStatus == 0; }
        catch { return false; }
    }

    internal static void ApplyIdentifyPowerStates(
        ApstInspectionReport report,
        IEnumerable<NvmePowerStateDescriptor>? identifyStates)
    {
        if (report is null || identifyStates is null) return;

        // MP = 0 is a real reading (the NVMe spec gives it no "unreported" meaning), and StorNVMe
        // picks idle states by latency, so a 0 W state stays in the list.
        var valid = identifyStates
            .Where(state => state is not null && state.Index >= 0 &&
                            double.IsFinite(state.MaxPowerWatts) && state.MaxPowerWatts >= 0)
            .GroupBy(state => state.Index)
            .Select(group => group.First())
            .OrderBy(state => state.Index)
            .ToList();

        // stornvme rarely has per-state registry entries, so the controller's own table is the
        // list when the registry gave none.
        if (report.States.Count == 0)
        {
            foreach (var state in valid)
            {
                report.States.Add(new ApstPowerState
                {
                    PowerStateNumber = state.Index,
                    MaxPowerWatts = state.MaxPowerWatts,
                    EntryLatencyUs = state.EntryLatencyUs,
                    ExitLatencyUs = state.ExitLatencyUs,
                    NonOperational = state.NonOperational
                });
            }
            return;
        }

        var byIndex = valid.ToDictionary(state => state.Index, state => state.MaxPowerWatts);

        foreach (var state in report.States)
        {
            if (byIndex.TryGetValue(state.PowerStateNumber, out var watts))
                state.MaxPowerWatts = watts;
        }
    }

    /// <summary>
    /// StorNVMe's documented choice once an idle timeout runs out: the deepest (lowest power)
    /// non-operational state whose entry plus exit latency fits the latency tolerance.
    /// </summary>
    internal static ApstPowerState? PickIdleState(IEnumerable<ApstPowerState> states, int latencyToleranceMs)
    {
        double budgetUs = latencyToleranceMs * 1000.0;
        return states
            .Where(s => s.NonOperational == true &&
                        s.EntryLatencyUs is double entry && s.ExitLatencyUs is double exit &&
                        entry + exit <= budgetUs)
            .OrderBy(s => s.MaxPowerWatts ?? double.MaxValue)
            .ThenByDescending(s => s.PowerStateNumber)
            .FirstOrDefault();
    }

    /// <summary>
    /// Works out which states Windows idles the drive into with the power plan's settings for the
    /// current power source, fills in their idle times, and writes the summary.
    /// </summary>
    internal static void ApplyIdlePolicy(ApstInspectionReport report, NvmeIdleSettings? ac, NvmeIdleSettings? dc, bool onBattery)
    {
        report.PowerPlanAc = ac;
        report.PowerPlanDc = dc;
        report.OnBattery = onBattery;
        string source = onBattery ? "on battery" : "on AC power";
        string stateCount = report.States.Count == 1 ? "1 power state" : $"{report.States.Count} power states";

        // Community tweaks set these to "turn APST off", but Microsoft documents neither value for
        // stornvme and the driver isn't known to read them, so the power plan still decides. The
        // value is reported so nobody wonders why the verdict ignores it.
        report.UndocumentedIdleOverride = report.NoLowPowerTransitions ? "NoLowPowerTransitions=1"
            : report.ApstEnabledOverride == false ? "AutonomousPowerStateTransitionEnabled=0"
            : null;
        ApplyPowerPlanPolicy(report, onBattery ? dc : ac, source, stateCount);
        if (report.UndocumentedIdleOverride is { } undocumented)
            report.Summary += $" The stornvme registry value {undocumented} is set, but Microsoft doesn't document it and stornvme isn't known to read it, so it isn't counted on to turn idle off.";
    }

    private static void ApplyPowerPlanPolicy(ApstInspectionReport report, NvmeIdleSettings? settings, string source, string stateCount)
    {
        if (settings is null)
        {
            report.IdleStatesUsed = null;
            report.Summary = $"Low-power idle wasn't reported: the power plan's NVMe idle settings couldn't be read. {stateCount}.";
            return;
        }

        string plan = $"The power plan idles NVMe drives after {Ms(settings.PrimaryIdleTimeoutMs)} (up to {Ms(settings.PrimaryLatencyToleranceMs)} wake latency) " +
                      $"and {Ms(settings.SecondaryIdleTimeoutMs)} (up to {Ms(settings.SecondaryLatencyToleranceMs)}) {source}";
        if (settings.PrimaryIdleTimeoutMs == 0 && settings.SecondaryIdleTimeoutMs == 0)
        {
            report.IdleStatesUsed = null;
            report.Summary = $"The power plan sets both NVMe idle timeouts to 0 ms {source}, which Microsoft doesn't document. {stateCount}.";
            return;
        }
        if (!report.States.Any(s => s.NonOperational is not null && s.EntryLatencyUs is not null && s.ExitLatencyUs is not null))
        {
            report.IdleStatesUsed = null;
            report.Summary = $"{plan}, but the drive's power table wasn't readable, so the states it idles into aren't known.";
            return;
        }

        var primary = settings.PrimaryIdleTimeoutMs > 0 ? PickIdleState(report.States, settings.PrimaryLatencyToleranceMs) : null;
        var secondary = settings.SecondaryIdleTimeoutMs > 0 ? PickIdleState(report.States, settings.SecondaryLatencyToleranceMs) : null;
        report.PrimaryIdleState = primary?.PowerStateNumber;
        report.SecondaryIdleState = secondary?.PowerStateNumber;
        if (primary is not null)
            primary.IdleTimeMicroseconds ??= settings.PrimaryIdleTimeoutMs * 1000;
        if (secondary is not null && !ReferenceEquals(secondary, primary))
            secondary.IdleTimeMicroseconds ??= settings.SecondaryIdleTimeoutMs * 1000;

        report.IdleStatesUsed = primary is not null || secondary is not null;
        if (primary is null && secondary is null)
        {
            report.Summary = report.States.Any(s => s.NonOperational == true)
                ? $"{plan}. None of this drive's non-operational states wakes that fast, so it stays in an operational state. {stateCount}."
                : $"{plan}, but this drive reports no non-operational power states, so it stays in an operational state. {stateCount}.";
            return;
        }

        string steps = (primary, secondary) switch
        {
            ({ } p, { } s) when p.PowerStateNumber != s.PowerStateNumber =>
                $"PS{p.PowerStateNumber} after {Ms(settings.PrimaryIdleTimeoutMs)} and PS{s.PowerStateNumber} after {Ms(settings.SecondaryIdleTimeoutMs)}",
            ({ } p, _) => $"PS{p.PowerStateNumber} after {Ms(settings.PrimaryIdleTimeoutMs)}",
            (null, { } s) => $"PS{s.PowerStateNumber} after {Ms(settings.SecondaryIdleTimeoutMs)}",
            _ => string.Empty
        };
        report.Summary = $"Windows idles this drive to {steps} {source}, using the power plan's NVMe settings. {stateCount}.";
    }

    private static string Ms(int milliseconds) => milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";

    /// <summary>The CLI's "Idle savings" line, or null when there's nothing honest to print: the
    /// watts the drive's power table promises mean nothing when Windows never idles it there.</summary>
    public static string? IdleSavingsText(ApstBatteryEstimate est)
    {
        if (est.EstimatedIdleSavingsWatts is not double watts) return null;
        var figure = watts.ToString("F1", CultureInfo.InvariantCulture);
        return est.IdleStatesUsed switch
        {
            true => $"~{figure}W (lost after patching)",
            false => null,
            null => $"up to ~{figure}W, if Windows idles this drive (not confirmed)"
        };
    }

    internal static ApstBatteryEstimate EstimateBatteryImpact(ApstInspectionReport report)
    {
        var est = new ApstBatteryEstimate();
        try
        {
            est.IsLaptop = DriveService.TestLaptopChassis();
        }
        catch { }

        est.IdleStatesUsed = report.IdleStatesUsed;

        if (report.States.Count > 0)
        {
            var activeState = report.States.FirstOrDefault(s => s.PowerStateNumber == 0);
            est.ActivePowerWatts = activeState?.MaxPowerWatts;

            // The deepest state Windows actually uses when that's known; otherwise the drive's
            // lowest non-operational state.
            var used = report.States
                .Where(s => (s.PowerStateNumber == report.PrimaryIdleState || s.PowerStateNumber == report.SecondaryIdleState) &&
                            s.MaxPowerWatts.HasValue)
                .OrderBy(s => s.MaxPowerWatts!.Value)
                .FirstOrDefault();
            var lowestIdle = used ?? report.States
                .Where(s => s.NonOperational == true && s.MaxPowerWatts.HasValue)
                .OrderBy(s => s.MaxPowerWatts!.Value)
                .FirstOrDefault();
            est.LowestIdlePowerWatts = lowestIdle?.MaxPowerWatts;

            if (est.ActivePowerWatts.HasValue && est.LowestIdlePowerWatts.HasValue)
                est.EstimatedIdleSavingsWatts = est.ActivePowerWatts.Value - est.LowestIdlePowerWatts.Value;
        }

        if (!est.IsLaptop)
        {
            est.Impact = "Desktop system. Idle power states have no battery impact.";
            est.Recommendation = "No action needed.";
        }
        else if (est.IdleStatesUsed == true)
        {
            var savingsText = est.EstimatedIdleSavingsWatts.HasValue
                ? $" (up to ~{est.EstimatedIdleSavingsWatts:F1}W idle savings)"
                : "";
            est.Impact = $"Windows idles this drive into a low-power state{savingsText}. The native NVMe driver (nvmedisk.sys) will ignore these transitions.";
            est.Recommendation = "Expect ~10-15% shorter battery life on idle workloads after patching. Consider keeping the OS drive on stornvme.sys if battery life is critical.";
        }
        else if (est.IdleStatesUsed == false)
        {
            est.Impact = "This drive doesn't idle into a low-power state now, so patching adds no battery regression.";
            est.Recommendation = "No additional impact from the native NVMe patch.";
        }
        else
        {
            est.Impact = "Windows didn't report how it idles this drive, so the battery impact can't be estimated.";
            est.Recommendation = "Check the power plan's NVMe idle settings with powercfg /qh before patching a laptop.";
        }

        return est;
    }

    /// <summary>
    /// Modern Standby (Connected Standby / S0 low-power idle) detection via
    /// HKLM\SYSTEM\CurrentControlSet\Control\Power\CsEnabled. On these systems StorNVMe does not
    /// support APST, and the native stack's wake timing can be too optimistic for some controllers.
    /// </summary>
    public static bool IsModernStandbyEnabled()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            return key?.GetValue("CsEnabled") is int cs && cs != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Pure: a distinct Modern-Standby sleep-wake risk warning for laptops, else null. StorNVMe
    /// has no APST on Modern Standby (Microsoft Learn), and nvmedisk.sys can let NVMe drives
    /// "vanish" on wake when controller firmware is too optimistic about wake-up timing. Separate
    /// from the general APST battery warning — this is a data-availability risk, not just battery.
    /// </summary>
    internal static string? ModernStandbyApstWarning(bool isLaptop, bool modernStandby)
    {
        if (!isLaptop || !modernStandby) return null;
        return "Modern Standby laptop: StorNVMe does not support APST on Modern Standby (S0 low-power) " +
               "systems, and nvmedisk.sys can let NVMe drives vanish on wake from sleep when controller " +
               "firmware is too optimistic about wake-up timing. Mitigations before patching: disable Fast " +
               "Startup (powercfg /h off, or Control Panel > Power Options), and set PCIe Link State Power " +
               "Management to Off in the active power plan.";
    }
}
