using System.IO;
using System.Management;
using Microsoft.Win32;

namespace NVMeDriverPatcher.Services;

public enum GuardrailSeverity { Info, Warning, Blocker }

public class GuardrailFinding
{
    public string Name { get; set; } = string.Empty;
    public GuardrailSeverity Severity { get; set; } = GuardrailSeverity.Info;
    public string Detail { get; set; } = string.Empty;
}

public class SystemGuardrailsReport
{
    public List<GuardrailFinding> Findings { get; set; } = new();
    public bool HasBlocker => Findings.Any(f => f.Severity == GuardrailSeverity.Blocker);
    public string Summary { get; set; } = string.Empty;
}

// Detects OS-level guardrails that interact with Native NVMe: HVCI / Memory Integrity,
// WDAC enforcement, Intel VROC, and NTFS compression on the system drive. These aren't
// hard failures but materially change the risk profile of enabling the patch, so we
// surface them in preflight.
public static class SystemGuardrailsService
{
    public static SystemGuardrailsReport Evaluate()
    {
        var report = new SystemGuardrailsReport();
        try { report.Findings.Add(CheckHvci()); } catch { }
        try { report.Findings.Add(CheckWdac()); } catch { }
        try { report.Findings.Add(CheckVroc()); } catch { }
        try { report.Findings.Add(CheckSystemDriveCompression()); } catch { }
        try { report.Findings.Add(CheckAppLockerOrSrp()); } catch { }
        report.Findings = report.Findings.Where(f => f is not null).ToList();
        report.Summary = BuildSummary(report);
        return report;
    }

    internal static GuardrailFinding CheckAppLockerOrSrp()
    {
        // AppLocker enforced rules live under HKLM\SOFTWARE\Policies\Microsoft\Windows\SrpV2\
        // with EnforcementMode values. SRP lives under \Safer\CodeIdentifiers\. Either mode
        // being non-audit will refuse our ViVeTool download + MSI side-load path. Warn — but
        // never block; a well-pinned fleet may have exceptions already.
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var srpv2 = hklm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\SrpV2");
            if (srpv2 is not null)
            {
                foreach (var name in srpv2.GetSubKeyNames())
                {
                    using var sub = srpv2.OpenSubKey(name);
                    if (sub?.GetValue("EnforcementMode") is int mode && mode == 1)
                    {
                        return new GuardrailFinding
                        {
                            Name = "AppLocker",
                            Severity = GuardrailSeverity.Warning,
                            Detail = $"AppLocker collection '{name}' is in Enforced mode. Downloaded binaries (ViVeTool) may be blocked — pre-approve or whitelist the hash."
                        };
                    }
                }
            }
            using var srp = hklm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers");
            if (srp?.GetValue("DefaultLevel") is int def && def != 0x40000)
            {
                return new GuardrailFinding
                {
                    Name = "Software Restriction Policies",
                    Severity = GuardrailSeverity.Warning,
                    Detail = "SRP default level is not 'Unrestricted'. Side-loaded binaries may be refused."
                };
            }
        }
        catch { }
        return new GuardrailFinding
        {
            Name = "AppLocker / SRP",
            Severity = GuardrailSeverity.Info,
            Detail = "No AppLocker or SRP enforcement detected."
        };
    }

    internal static GuardrailFinding CheckHvci()
    {
        // DeviceGuard state via Win32_DeviceGuard in root\Microsoft\Windows\DeviceGuard.
        // SecurityServicesRunning contains 2 when HVCI (Hypervisor-protected Code Integrity)
        // is active. On a locked-down SKU this class can be missing entirely.
        try
        {
            using var search = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            using var collection = search.Get();
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject mo) continue;
                using (mo)
                {
                    if (mo["SecurityServicesRunning"] is uint[] running && running.Contains(2u))
                    {
                        return new GuardrailFinding
                        {
                            Name = "HVCI / Memory Integrity",
                            Severity = GuardrailSeverity.Warning,
                            Detail = "HVCI is active. Unsigned or mismatched NVMe driver loads can be blocked — verify nvmedisk.sys is WHQL-signed on this build."
                        };
                    }
                }
            }
        }
        catch { /* WMI denial — treat as "unknown, not a blocker" */ }
        return new GuardrailFinding
        {
            Name = "HVCI / Memory Integrity",
            Severity = GuardrailSeverity.Info,
            Detail = "HVCI not active — no memory-integrity restriction on driver load."
        };
    }

    internal static GuardrailFinding CheckWdac()
    {
        // WDAC policy state lives at HKLM\SYSTEM\CurrentControlSet\Control\CI\Protected.
        // A non-audit enforced policy blocks arbitrary downloaded binaries (i.e. ViVeTool.exe).
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Protected");
            if (key is not null)
            {
                var val = key.GetValue("EnforceMode");
                if (val is int i && i != 0)
                {
                    return new GuardrailFinding
                    {
                        Name = "WDAC enforcement",
                        Severity = GuardrailSeverity.Warning,
                        Detail = "Windows Defender Application Control is enforced. The ViVeTool fallback download will likely be blocked — have a pre-approved copy ready."
                    };
                }
            }
        }
        catch { }
        return new GuardrailFinding
        {
            Name = "WDAC enforcement",
            Severity = GuardrailSeverity.Info,
            Detail = "WDAC not in enforced mode."
        };
    }

    internal static GuardrailFinding CheckVroc()
    {
        // Intel VROC advertises iaStorAfs.sys or iaStorVD.sys and an RST-style service.
        // The NVMe patch has high BSOD correlation on VROC-configured arrays.
        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT Name, PathName FROM Win32_SystemDriver WHERE State='Running'");
            using var collection = search.Get();
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject mo) continue;
                using (mo)
                {
                    var name = (mo["Name"] as string) ?? string.Empty;
                    if (name.StartsWith("iaStorAfs", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("iaStorVD", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("iaVROC", StringComparison.OrdinalIgnoreCase))
                    {
                        return new GuardrailFinding
                        {
                            Name = "Intel VROC",
                            Severity = GuardrailSeverity.Blocker,
                            Detail = $"Intel VROC driver '{name}' is loaded. Native NVMe is incompatible with VROC-managed arrays."
                        };
                    }
                }
            }
        }
        catch { }
        return new GuardrailFinding
        {
            Name = "Intel VROC",
            Severity = GuardrailSeverity.Info,
            Detail = "No Intel VROC driver detected."
        };
    }

    internal static GuardrailFinding CheckSystemDriveCompression()
    {
        // NTFS compression on the system drive plays badly with the BypassIO path — and the
        // Native NVMe patch already disables BypassIO. Warn so the user understands the combined
        // effect instead of attributing the slowdown to the patch.
        try
        {
            var sys = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var root = new DirectoryInfo(sys + Path.DirectorySeparatorChar);
            if ((root.Attributes & FileAttributes.Compressed) == FileAttributes.Compressed)
            {
                return new GuardrailFinding
                {
                    Name = "NTFS compression on SystemDrive",
                    Severity = GuardrailSeverity.Warning,
                    Detail = $"{sys} is NTFS-compressed at the root. Expect higher CPU load under Native NVMe — consider disabling compression on the OS drive."
                };
            }
        }
        catch { }
        return new GuardrailFinding
        {
            Name = "NTFS compression on SystemDrive",
            Severity = GuardrailSeverity.Info,
            Detail = "System drive is not NTFS-compressed."
        };
    }

    internal static string BuildSummary(SystemGuardrailsReport report)
    {
        int blockers = report.Findings.Count(f => f.Severity == GuardrailSeverity.Blocker);
        int warnings = report.Findings.Count(f => f.Severity == GuardrailSeverity.Warning);
        if (blockers > 0) return $"{blockers} blocker(s), {warnings} warning(s) — review before applying.";
        if (warnings > 0) return $"{warnings} guardrail warning(s) — review before applying.";
        return "No guardrail issues detected.";
    }
}
