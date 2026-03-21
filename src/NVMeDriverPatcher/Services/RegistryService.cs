using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class RegistryService
{
    public static PatchStatus GetPatchStatus()
    {
        var status = new PatchStatus { Total = AppConfig.TotalComponents };
        int count = 0;
        var keys = new List<string>();

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        using var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey);
        if (overrides is not null)
        {
            foreach (var id in AppConfig.FeatureIDs)
            {
                var val = overrides.GetValue(id);
                if (val is int intVal && intVal == 1)
                {
                    count++;
                    keys.Add(id);
                }
            }
        }

        // SafeBoot Minimal
        using var safeMin = hklm.OpenSubKey(AppConfig.SafeBootMinimalPath);
        if (safeMin is not null)
        {
            var val = safeMin.GetValue("") as string;
            if (val == AppConfig.SafeBootValue)
            {
                count++;
                keys.Add("SafeBootMinimal");
            }
        }

        // SafeBoot Network
        using var safeNet = hklm.OpenSubKey(AppConfig.SafeBootNetworkPath);
        if (safeNet is not null)
        {
            var val = safeNet.GetValue("") as string;
            if (val == AppConfig.SafeBootValue)
            {
                count++;
                keys.Add("SafeBootNetwork");
            }
        }

        status.Count = count;
        status.Keys = keys;
        status.Applied = count == AppConfig.TotalComponents;
        status.Partial = count > 0 && count < AppConfig.TotalComponents;
        return status;
    }

    public static string? ExportRegistryBackup(string workingDir, string description = "NVMe_Backup")
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupFile = Path.Combine(workingDir, $"{description}_{timestamp}.reg");

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var lines = new List<string>
            {
                "Windows Registry Editor Version 5.00",
                "",
                "; NVMe Driver Patcher Registry Backup",
                $"; Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"; Description: {description}",
                "",
                $@"[HKEY_LOCAL_MACHINE\{AppConfig.RegistrySubKey}]"
            };

            using var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey);
            if (overrides is not null)
            {
                foreach (var id in AppConfig.FeatureIDs)
                {
                    var val = overrides.GetValue(id);
                    if (val is int intVal)
                        lines.Add($"\"{id}\"=dword:{intVal:x8}");
                }
                // Server key
                var srvVal = overrides.GetValue(AppConfig.ServerFeatureID);
                if (srvVal is int srvInt)
                    lines.Add($"\"{AppConfig.ServerFeatureID}\"=dword:{srvInt:x8}");
            }

            // SafeBoot keys
            lines.Add("");
            using var safeMin = hklm.OpenSubKey(AppConfig.SafeBootMinimalPath);
            if (safeMin is not null)
            {
                lines.Add($@"[HKEY_LOCAL_MACHINE\{AppConfig.SafeBootMinimalPath}]");
                var val = safeMin.GetValue("") as string;
                if (!string.IsNullOrEmpty(val))
                    lines.Add($"@=\"{val}\"");
                lines.Add("");
            }

            using var safeNet = hklm.OpenSubKey(AppConfig.SafeBootNetworkPath);
            if (safeNet is not null)
            {
                lines.Add($@"[HKEY_LOCAL_MACHINE\{AppConfig.SafeBootNetworkPath}]");
                var val = safeNet.GetValue("") as string;
                if (!string.IsNullOrEmpty(val))
                    lines.Add($"@=\"{val}\"");
                lines.Add("");
            }

            File.WriteAllText(backupFile, string.Join("\r\n", lines), System.Text.Encoding.Unicode);
            return backupFile;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsServerKeyApplied()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(AppConfig.RegistrySubKey);
            if (key is null) return false;
            var val = key.GetValue(AppConfig.ServerFeatureID);
            return val is int intVal && intVal == 1;
        }
        catch { return false; }
    }

    public static PatchSnapshot GetPatchSnapshot(NativeNVMeStatus? nativeStatus, BypassIOResult? bypassStatus)
    {
        var snapshot = new PatchSnapshot
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Status = GetPatchStatus(),
            DriverActive = nativeStatus?.ActiveDriver ?? "Unknown",
            BypassIO = bypassStatus?.Supported ?? false,
            Components = []
        };

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey);

        foreach (var id in AppConfig.FeatureIDs)
        {
            if (overrides is not null)
            {
                var val = overrides.GetValue(id);
                snapshot.Components[id] = val is int intVal && intVal == 1 ? "Set (1)" : "Not Set";
            }
            else
            {
                snapshot.Components[id] = "Not Set";
            }
        }

        // Server key
        if (overrides is not null)
        {
            var srvVal = overrides.GetValue(AppConfig.ServerFeatureID);
            snapshot.Components[AppConfig.ServerFeatureID] = srvVal is int sv && sv == 1 ? "Set (1)" : "Not Set";
        }
        else
        {
            snapshot.Components[AppConfig.ServerFeatureID] = "Not Set";
        }

        using var safeMin = hklm.OpenSubKey(AppConfig.SafeBootMinimalPath);
        snapshot.Components["SafeBootMinimal"] = safeMin is not null ? "Present" : "Absent";

        using var safeNet = hklm.OpenSubKey(AppConfig.SafeBootNetworkPath);
        snapshot.Components["SafeBootNetwork"] = safeNet is not null ? "Present" : "Absent";

        return snapshot;
    }
}
