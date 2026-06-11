using Microsoft.Win32;

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
    public bool ApstHonored { get; set; }
    public double? ActivePowerWatts { get; set; }
    public double? LowestIdlePowerWatts { get; set; }
    public double? EstimatedIdleSavingsWatts { get; set; }
    public string Impact { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}

public class ApstInspectionReport
{
    public bool ApstEnabled { get; set; }
    public int? ApstIdleTimeout { get; set; }
    public bool NoLowPowerTransitions { get; set; }
    public List<ApstPowerState> States { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public ApstBatteryEstimate? BatteryEstimate { get; set; }
}

// Inspects Autonomous Power State Transition (APST) settings under stornvme per-drive
// parameters. Lets laptop users see the tradeoff the Native NVMe patch forces (APST gets
// disabled) and optionally restore custom idle timeouts. Closes ROADMAP §3.2.
public static class ApstInspectorService
{
    private const string ParametersRoot = @"SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device";

    public static ApstInspectionReport Inspect()
    {
        var report = new ApstInspectionReport();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(ParametersRoot);
            if (key is null)
            {
                report.Summary = "stornvme Device parameters key is absent.";
                return report;
            }

            report.ApstEnabled = (key.GetValue("AutonomousPowerStateTransitionEnabled") is int apst && apst != 0);
            if (key.GetValue("ApstIdleTimeout") is int timeout) report.ApstIdleTimeout = timeout;
            report.NoLowPowerTransitions = (key.GetValue("NoLowPowerTransitions") is int nl && nl != 0);

            for (int i = 0; i < 32; i++)
            {
                var psKey = key.GetValue($"PowerState{i}_IdleTimeUs");
                if (psKey is not int idle) continue;
                var state = new ApstPowerState { PowerStateNumber = i, IdleTimeMicroseconds = idle };
                if (key.GetValue($"PowerState{i}_EntryLatencyUs") is int entry) state.EntryLatencyUs = entry;
                if (key.GetValue($"PowerState{i}_ExitLatencyUs") is int exit) state.ExitLatencyUs = exit;
                if (key.GetValue($"PowerState{i}_NonOperational") is int no) state.NonOperational = no != 0;
                report.States.Add(state);
            }
            report.Summary = report.ApstEnabled
                ? $"APST enabled with idle timeout {report.ApstIdleTimeout?.ToString() ?? "default"}. {report.States.Count} power-state entries."
                : "APST disabled — drives stay at active power state (higher battery drain on laptops).";
            report.BatteryEstimate = EstimateBatteryImpact(report);
        }
        catch (Exception ex)
        {
            report.Summary = $"APST inspection failed: {ex.Message}";
        }
        return report;
    }

    internal static ApstBatteryEstimate EstimateBatteryImpact(ApstInspectionReport report)
    {
        var est = new ApstBatteryEstimate();
        try
        {
            est.IsLaptop = DriveService.TestLaptopChassis();
        }
        catch { }

        est.ApstHonored = report.ApstEnabled && !report.NoLowPowerTransitions;

        if (report.States.Count > 0)
        {
            var activeState = report.States.FirstOrDefault(s => s.PowerStateNumber == 0);
            est.ActivePowerWatts = activeState?.MaxPowerWatts;

            var lowestIdle = report.States
                .Where(s => s.NonOperational == true && s.MaxPowerWatts.HasValue)
                .OrderBy(s => s.MaxPowerWatts!.Value)
                .FirstOrDefault();
            est.LowestIdlePowerWatts = lowestIdle?.MaxPowerWatts;

            if (est.ActivePowerWatts.HasValue && est.LowestIdlePowerWatts.HasValue)
                est.EstimatedIdleSavingsWatts = est.ActivePowerWatts.Value - est.LowestIdlePowerWatts.Value;
        }

        if (!est.IsLaptop)
        {
            est.Impact = "Desktop system — APST has no battery impact.";
            est.Recommendation = "No action needed.";
        }
        else if (est.ApstHonored)
        {
            var savingsText = est.EstimatedIdleSavingsWatts.HasValue
                ? $" (up to ~{est.EstimatedIdleSavingsWatts:F1}W idle savings)"
                : "";
            est.Impact = $"APST is active{savingsText}. The native NVMe driver (nvmedisk.sys) will ignore these transitions.";
            est.Recommendation = "Expect ~10-15% shorter battery life on idle workloads after patching. Consider keeping the OS drive on stornvme.sys if battery life is critical.";
        }
        else
        {
            est.Impact = "APST is already disabled or blocked — no additional battery regression from patching.";
            est.Recommendation = "No additional impact from the native NVMe patch.";
        }

        return est;
    }

    /// <summary>
    /// Writes a conservative APST idle timeout override to the stornvme parameters key.
    /// Timeout is clamped to 250µs–60s. Does not touch NoLowPowerTransitions (too dangerous
    /// to flip without a per-drive test).
    /// </summary>
    public static bool OverrideIdleTimeout(int microseconds, Action<string>? log = null)
    {
        int clamped = Math.Clamp(microseconds, 250, 60_000_000);
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(ParametersRoot, writable: true);
            if (key is null) { log?.Invoke("[ERROR] Could not open stornvme Device key."); return false; }
            key.SetValue("ApstIdleTimeout", clamped, RegistryValueKind.DWord);
            key.Flush();
            log?.Invoke($"[OK] ApstIdleTimeout set to {clamped}µs. Reboot required.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not write ApstIdleTimeout: {ex.Message}");
            return false;
        }
    }
}
