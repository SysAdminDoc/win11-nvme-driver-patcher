using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class SafeBootUpgradeReport
{
    public bool GuidEntriesPresent { get; set; }
    public bool ServiceEntriesComplete { get; set; }
    /// <summary>An older patch wrote the GUID-class SafeBoot entries but the KB5079391-era
    /// service-name entries are missing or incomplete — Safe Mode on 25H2+ would fail to
    /// load the storage driver (INACCESSIBLE_BOOT_DEVICE).</summary>
    public bool UpgradeNeeded { get; set; }
    public string Summary { get; set; } = string.Empty;
}

// KB5079391 (March 2026) changed how Safe Mode resolves storage drivers on 25H2: the
// GUID-class SafeBoot entries alone are no longer sufficient — the canonical service-name
// entries (SafeBoot\Minimal\nvmedisk, SafeBoot\Network\nvmedisk) are required. v4.6.1+
// writes both sets on every patch, but users who patched with an older version have only
// the GUID entries. This service detects that gap and writes the missing entries without
// touching anything else.
public static class SafeBootUpgradeService
{
    public static SafeBootUpgradeReport Evaluate()
    {
        bool guidMin = false, guidNet = false, svcMin = false, svcNet = false;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var k = hklm.OpenSubKey(AppConfig.SafeBootMinimalPath))
                guidMin = k?.GetValue("") as string == AppConfig.SafeBootValue;
            using (var k = hklm.OpenSubKey(AppConfig.SafeBootNetworkPath))
                guidNet = k?.GetValue("") as string == AppConfig.SafeBootValue;
            using (var k = hklm.OpenSubKey(AppConfig.SafeBootMinimalServicePath))
                svcMin = k?.GetValue("") as string == AppConfig.SafeBootServiceValue;
            using (var k = hklm.OpenSubKey(AppConfig.SafeBootNetworkServicePath))
                svcNet = k?.GetValue("") as string == AppConfig.SafeBootServiceValue;
        }
        catch
        {
            // Probe failure (hardened SKU, transient registry error) — report "no upgrade
            // needed" rather than nagging on a state we can't actually see.
            return new SafeBootUpgradeReport { Summary = "SafeBoot state could not be read." };
        }
        return Classify(guidMin, guidNet, svcMin, svcNet);
    }

    /// <summary>
    /// Pure decision logic. Any GUID-class entry means a patch (old or current) is in place;
    /// the upgrade is needed when the service-name pair is not complete. Machines with no
    /// patch at all (no GUID entries) never need the upgrade — fresh installs write both sets.
    /// </summary>
    internal static SafeBootUpgradeReport Classify(bool guidMin, bool guidNet, bool svcMin, bool svcNet)
    {
        var report = new SafeBootUpgradeReport
        {
            GuidEntriesPresent = guidMin || guidNet,
            ServiceEntriesComplete = svcMin && svcNet,
        };
        report.UpgradeNeeded = report.GuidEntriesPresent && !report.ServiceEntriesComplete;
        report.Summary = report switch
        {
            { GuidEntriesPresent: false } =>
                "No patch SafeBoot entries present — nothing to upgrade.",
            { UpgradeNeeded: false } =>
                "SafeBoot entries are current (GUID + service-name).",
            _ =>
                "SafeBoot entries predate the KB5079391 fix: the GUID entries exist but the " +
                "service-name entries (SafeBoot\\Minimal\\nvmedisk, SafeBoot\\Network\\nvmedisk) " +
                "are missing. Booting to Safe Mode on 25H2+ could hit INACCESSIBLE_BOOT_DEVICE. " +
                "Run the one-click upgrade to add them.",
        };
        return report;
    }

    /// <summary>
    /// Writes the missing service-name SafeBoot entries. Idempotent — safe to run when the
    /// entries already exist. Does not touch feature flags or GUID entries.
    /// </summary>
    public static (bool Success, string Message) UpgradeEntries(Action<string>? log = null)
    {
        var recoverySafety = RecoverySafetyGateService.Snapshot();
        if (!recoverySafety.MutationAllowed)
            return (false, "SafeBoot upgrade blocked by unresolved startup recovery: " + recoverySafety.Summary);

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            using (var svcMin = hklm.CreateSubKey(AppConfig.SafeBootMinimalServicePath))
                svcMin?.SetValue("", AppConfig.SafeBootServiceValue);
            log?.Invoke("  [OK] SafeBoot Minimal (service name) written");

            using (var svcNet = hklm.CreateSubKey(AppConfig.SafeBootNetworkServicePath))
                svcNet?.SetValue("", AppConfig.SafeBootServiceValue);
            log?.Invoke("  [OK] SafeBoot Network (service name) written");

            // Verify both landed before claiming success.
            return VerifyUpgrade(Evaluate());
        }
        catch (Exception ex)
        {
            return (false, $"SafeBoot upgrade failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure verify gate. Success REQUIRES the service-name pair to be present after the write —
    /// asserting <see cref="SafeBootUpgradeReport.ServiceEntriesComplete"/> directly. The old gate
    /// keyed off <c>GuidEntriesPresent &amp;&amp; !ServiceEntriesComplete</c>, so a silent write
    /// no-op (CreateSubKey returns null) on a machine with no GUID entries skipped the failure
    /// branch and falsely reported success.
    /// </summary>
    internal static (bool Success, string Message) VerifyUpgrade(SafeBootUpgradeReport after) =>
        after.ServiceEntriesComplete
            ? (true, "SafeBoot service-name entries are in place. Safe Mode on 25H2+ will resolve the storage driver correctly.")
            : (false, "Writes completed but verification reports the service-name entries are not in place.");
}
