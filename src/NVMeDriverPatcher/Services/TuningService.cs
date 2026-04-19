using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>
/// Service for reading and writing StorNVMe driver parameters via the registry.
/// Target: HKLM\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device
/// Changes take effect after a reboot or device re-enumeration.
/// </summary>
public static class TuningService
{
    /// <summary>
    /// Reads all tunable StorNVMe parameters from the registry into a TuningProfile.
    /// Returns a profile with null values for any parameters not explicitly set.
    /// </summary>
    public static TuningProfile GetCurrentParameters()
    {
        var profile = new TuningProfile
        {
            Name = "Current",
            Description = "Values currently set in the registry"
        };

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(TuningProfile.RegistrySubKey);

            if (key is null)
                return profile;

            profile.QueueDepth = ReadDword(key, TuningProfile.Key_QueueDepth);
            profile.NvmeMaxReadSplit = ReadDword(key, TuningProfile.Key_MaxReadSplit);
            profile.NvmeMaxWriteSplit = ReadDword(key, TuningProfile.Key_MaxWriteSplit);
            profile.IoSubmissionQueueCount = ReadDword(key, TuningProfile.Key_IoSubmissionQueueCount);
            profile.IdlePowerTimeout = ReadDword(key, TuningProfile.Key_IdlePowerTimeout);
            profile.StandbyPowerTimeout = ReadDword(key, TuningProfile.Key_StandbyPowerTimeout);
        }
        catch
        {
            // Registry access failed; return profile with all nulls
        }

        return profile;
    }

    // Safe bounds for StorNVMe parameters — values outside these can cause BSOD or instability
    private static readonly Dictionary<string, (int Min, int Max)> ParameterBounds = new(StringComparer.OrdinalIgnoreCase)
    {
        [TuningProfile.Key_QueueDepth] = (1, 256),
        [TuningProfile.Key_MaxReadSplit] = (1, 4096),
        [TuningProfile.Key_MaxWriteSplit] = (1, 4096),
        [TuningProfile.Key_IoSubmissionQueueCount] = (0, 256),
        [TuningProfile.Key_IdlePowerTimeout] = (0, 60000),
        [TuningProfile.Key_StandbyPowerTimeout] = (0, 60000)
    };

    private static bool ValidateParameter(string name, int value, Action<string>? log = null)
    {
        if (!ParameterBounds.TryGetValue(name, out var bounds))
        {
            log?.Invoke($"  [BLOCKED] Unknown StorNVMe tuning parameter: {name}");
            return false;
        }

        if (value < bounds.Min || value > bounds.Max)
        {
            log?.Invoke($"  [BLOCKED] {name} = {value} is outside safe range [{bounds.Min}..{bounds.Max}]");
            return false;
        }
        return true;
    }

    internal static bool IsKnownParameterName(string name) =>
        !string.IsNullOrWhiteSpace(name) && ParameterBounds.ContainsKey(name);

    /// <summary>
    /// Applies all non-null values from a TuningProfile to the StorNVMe registry parameters.
    /// Creates the registry key path if it does not exist.
    /// </summary>
    /// <param name="profile">The profile to apply.</param>
    /// <param name="log">Optional logging callback.</param>
    /// <returns>True if all values were written successfully.</returns>
    public static bool ApplyProfile(TuningProfile profile, Action<string>? log = null)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(TuningProfile.RegistrySubKey);

            if (key is null)
            {
                log?.Invoke("[ERROR] Failed to open/create StorNVMe parameters registry key");
                return false;
            }

            log?.Invoke($"Applying tuning profile: {profile.Name}");
            int applied = 0;
            int failed = 0;

            void WriteIfSet(string name, int? value)
            {
                if (value is null) return;
                if (!ValidateParameter(name, value.Value, log)) { failed++; return; }
                try
                {
                    key.SetValue(name, value.Value, RegistryValueKind.DWord);
                    var verify = key.GetValue(name);
                    if (verify is int v && v == value.Value)
                    {
                        log?.Invoke($"  [OK] {name} = {value.Value}");
                        applied++;
                    }
                    else
                    {
                        log?.Invoke($"  [FAIL] {name} - verification mismatch");
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  [FAIL] {name} - {ex.Message}");
                    failed++;
                }
            }

            WriteIfSet(TuningProfile.Key_QueueDepth, profile.QueueDepth);
            WriteIfSet(TuningProfile.Key_MaxReadSplit, profile.NvmeMaxReadSplit);
            WriteIfSet(TuningProfile.Key_MaxWriteSplit, profile.NvmeMaxWriteSplit);
            WriteIfSet(TuningProfile.Key_IoSubmissionQueueCount, profile.IoSubmissionQueueCount);
            WriteIfSet(TuningProfile.Key_IdlePowerTimeout, profile.IdlePowerTimeout);
            WriteIfSet(TuningProfile.Key_StandbyPowerTimeout, profile.StandbyPowerTimeout);

            // Force the writes to disk so a hard reset between Apply and reboot doesn't lose them.
            try { key.Flush(); } catch { }

            log?.Invoke($"Tuning complete: {applied} applied, {failed} failed");
            EventLogService.Write($"StorNVMe tuning profile '{profile.Name}' applied ({applied} parameters)");

            return failed == 0;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Failed to apply tuning profile: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads a single StorNVMe parameter value from the registry.
    /// </summary>
    /// <param name="name">Registry value name (e.g., "IoQueueDepth").</param>
    /// <returns>The DWORD value, or null if not set.</returns>
    public static int? GetParameter(string name)
    {
        if (!IsKnownParameterName(name)) return null;

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(TuningProfile.RegistrySubKey);
            return ReadDword(key, name);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a single StorNVMe parameter value to the registry.
    /// </summary>
    /// <param name="name">Registry value name.</param>
    /// <param name="value">DWORD value to set.</param>
    /// <returns>True if the write succeeded and was verified.</returns>
    public static bool SetParameter(string name, int value)
    {
        if (!ValidateParameter(name, value)) return false;

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(TuningProfile.RegistrySubKey);

            if (key is null)
                return false;

            key.SetValue(name, value, RegistryValueKind.DWord);
            var verify = key.GetValue(name);
            if (verify is not int v || v != value)
                return false;

            try { key.Flush(); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a single StorNVMe parameter from the registry, reverting it to the driver default.
    /// </summary>
    /// <param name="name">Registry value name to remove.</param>
    /// <returns>True if the value was removed or was already absent.</returns>
    public static bool RemoveParameter(string name)
    {
        if (!IsKnownParameterName(name)) return false;

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(TuningProfile.RegistrySubKey, writable: true);

            if (key is null)
                return true; // Key doesn't exist, parameter is already absent

            key.DeleteValue(name, throwOnMissingValue: false);
            try { key.Flush(); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes all tuning parameters, reverting StorNVMe to driver defaults.
    /// </summary>
    /// <param name="log">Optional logging callback.</param>
    /// <returns>True if all removals succeeded.</returns>
    public static bool ResetToDefaults(Action<string>? log = null)
    {
        log?.Invoke("Resetting StorNVMe parameters to driver defaults...");
        bool allOk = true;

        string[] allKeys =
        [
            TuningProfile.Key_QueueDepth,
            TuningProfile.Key_MaxReadSplit,
            TuningProfile.Key_MaxWriteSplit,
            TuningProfile.Key_IoSubmissionQueueCount,
            TuningProfile.Key_IdlePowerTimeout,
            TuningProfile.Key_StandbyPowerTimeout
        ];

        // Open the parent key once and delete all values, then flush. This is both faster
        // (one OpenSubKey + Flush instead of six) and removes the per-iteration race window
        // where a half-deleted set of values is observable.
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(TuningProfile.RegistrySubKey, writable: true);
            if (key is null)
            {
                log?.Invoke("Reset complete (no overrides were present)");
                return true;
            }

            foreach (string name in allKeys)
            {
                try
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                    log?.Invoke($"  [OK] Removed {name}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  [FAIL] Could not remove {name}: {ex.Message}");
                    allOk = false;
                }
            }
            try { key.Flush(); } catch { }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Reset failed to open parameters key: {ex.Message}");
            return false;
        }

        log?.Invoke(allOk ? "Reset complete" : "Reset completed with errors");
        return allOk;
    }

    private static int? ReadDword(RegistryKey? key, string name)
    {
        if (key is null) return null;
        var val = key.GetValue(name);
        return val is int intVal ? intVal : null;
    }
}
