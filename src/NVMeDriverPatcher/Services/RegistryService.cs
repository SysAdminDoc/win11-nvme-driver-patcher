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
        bool primarySet = false;
        bool extendedA = false;   // 1853569164
        bool extendedB = false;   // 156965516
        bool safeBootMin = false;
        bool safeBootNet = false;

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            using (var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey))
            {
                if (overrides is not null)
                {
                    foreach (var id in AppConfig.FeatureIDs)
                    {
                        try
                        {
                            var val = overrides.GetValue(id);
                            if (val is int intVal && intVal == 1)
                            {
                                count++;
                                keys.Add(id);
                                if (id == AppConfig.PrimaryFeatureID) primarySet = true;
                                else if (id == "1853569164") extendedA = true;
                                else if (id == "156965516") extendedB = true;
                            }
                        }
                        catch { /* one bad value shouldn't poison the entire status read */ }
                    }
                }
            }

            // SafeBoot Minimal
            using (var safeMin = hklm.OpenSubKey(AppConfig.SafeBootMinimalPath))
            {
                if (safeMin is not null && safeMin.GetValue("") as string == AppConfig.SafeBootValue)
                {
                    count++;
                    keys.Add("SafeBootMinimal");
                    safeBootMin = true;
                }
            }

            // SafeBoot Network
            using (var safeNet = hklm.OpenSubKey(AppConfig.SafeBootNetworkPath))
            {
                if (safeNet is not null && safeNet.GetValue("") as string == AppConfig.SafeBootValue)
                {
                    count++;
                    keys.Add("SafeBootNetwork");
                    safeBootNet = true;
                }
            }
        }
        catch
        {
            // Registry view denied or hive missing. Return whatever partial state we accumulated
            // — callers treat zero-applied as "Not Applied" which is the right default.
        }

        // Profile detection — answers "is this a clean Safe install, a clean Full install, or
        // some in-between state?" independently of what the config thinks was last applied.
        // Both SafeBoot keys are required for either profile to count as clean.
        bool cleanSafe = primarySet && !extendedA && !extendedB && safeBootMin && safeBootNet;
        bool cleanFull = primarySet && extendedA && extendedB && safeBootMin && safeBootNet;

        if (count == 0)
            status.DetectedProfile = PatchAppliedProfile.None;
        else if (cleanSafe)
            status.DetectedProfile = PatchAppliedProfile.Safe;
        else if (cleanFull)
            status.DetectedProfile = PatchAppliedProfile.Full;
        else
            status.DetectedProfile = PatchAppliedProfile.Mixed;

        status.Count = count;
        status.Keys = keys;
        // "Applied" now means any clean profile. Mixed state is "Partial" regardless of count —
        // before this fix, a Safe-Mode install (3/5 components) reported as Partial, confusing
        // users into thinking the tool had failed.
        status.Applied = cleanSafe || cleanFull;
        status.Partial = !status.Applied && count > 0;
        return status;
    }

    public static string? ExportRegistryBackup(string workingDir, string description = "NVMe_Backup")
    {
        if (string.IsNullOrEmpty(workingDir))
            return null;

        // Sanitize the description so it can't smuggle path separators / invalid filename chars.
        var safeDesc = string.IsNullOrWhiteSpace(description) ? "NVMe_Backup" : description;
        foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
            safeDesc = safeDesc.Replace(bad, '_');

        try { Directory.CreateDirectory(workingDir); } catch { }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupFile = Path.Combine(workingDir, $"{safeDesc}_{timestamp}.reg");

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

            // .reg files must be UTF-16 LE with BOM. Atomic write so a crash doesn't leave
            // a half-written backup that regedit will silently refuse to import.
            var tempFile = backupFile + ".tmp";
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, System.Text.Encoding.Unicode))
            {
                sw.Write(string.Join("\r\n", lines));
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempFile, backupFile, overwrite: true);
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
        // ISO 8601 round-trip ("o") preserves the full date and is what DataService.SaveSnapshot
        // round-trips back to a DateTime. The previous "HH:mm:ss" form lost the date entirely,
        // so all snapshots saved before midnight ended up with today's date as the prefix.
        var snapshot = new PatchSnapshot
        {
            Timestamp = DateTime.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            Status = GetPatchStatus(),
            DriverActive = nativeStatus?.ActiveDriver ?? "Unknown",
            BypassIO = bypassStatus?.Supported ?? false,
            Components = []
        };

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey);

            foreach (var id in AppConfig.FeatureIDs)
            {
                var val = overrides?.GetValue(id);
                snapshot.Components[id] = val is int intVal && intVal == 1 ? "Set (1)" : "Not Set";
            }

            // Server key
            var srvVal = overrides?.GetValue(AppConfig.ServerFeatureID);
            snapshot.Components[AppConfig.ServerFeatureID] = srvVal is int sv && sv == 1 ? "Set (1)" : "Not Set";

            using var safeMin = hklm.OpenSubKey(AppConfig.SafeBootMinimalPath);
            snapshot.Components["SafeBootMinimal"] = safeMin is not null ? "Present" : "Absent";

            using var safeNet = hklm.OpenSubKey(AppConfig.SafeBootNetworkPath);
            snapshot.Components["SafeBootNetwork"] = safeNet is not null ? "Present" : "Absent";
        }
        catch
        {
            // Registry view inaccessible — record placeholders so the diff log doesn't NRE later.
            foreach (var id in AppConfig.FeatureIDs)
                snapshot.Components.TryAdd(id, "Unknown");
            snapshot.Components.TryAdd(AppConfig.ServerFeatureID, "Unknown");
            snapshot.Components.TryAdd("SafeBootMinimal", "Unknown");
            snapshot.Components.TryAdd("SafeBootNetwork", "Unknown");
        }

        return snapshot;
    }
}
