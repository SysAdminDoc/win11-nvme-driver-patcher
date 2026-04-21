using System.Diagnostics;
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

        // Profile detection + Applied/Partial derivation is pushed into a pure helper so it
        // can be exhaustively unit-tested without touching the live registry.
        var classification = ClassifyPatchState(primarySet, extendedA, extendedB, safeBootMin, safeBootNet, count);
        status.DetectedProfile = classification.Profile;
        status.Count = count;
        status.Total = classification.ExpectedTotal;
        status.Keys = keys;
        status.Applied = classification.Applied;
        status.Partial = classification.Partial;
        return status;
    }

    /// <summary>
    /// Pure helper exposing the profile classification and Applied/Partial state derived from
    /// the raw "is this key set?" booleans. Extracted from <see cref="GetPatchStatus"/> so the
    /// logic (which users rely on for "is my patch correctly applied?") can be exhaustively
    /// tested without invoking the live registry.
    /// <para/>
    /// Rules:
    /// <list type="bullet">
    /// <item><c>count == 0</c> → <c>None</c>, not applied, not partial.</item>
    /// <item>Primary + both SafeBoot keys present, no extended flags → clean <c>Safe</c> install.</item>
    /// <item>Primary + both extended + both SafeBoot → clean <c>Full</c> install.</item>
    /// <item>Anything else with at least one key set → <c>Mixed</c> + Partial.</item>
    /// </list>
    /// Applied is true for either clean profile; Partial is the leftover "something is set
    /// but it's not a clean install" bucket.
    /// </summary>
    internal readonly record struct PatchClassification(
        PatchAppliedProfile Profile,
        bool Applied,
        bool Partial,
        int ExpectedTotal);

    internal static PatchClassification ClassifyPatchState(
        bool primarySet,
        bool extendedA,
        bool extendedB,
        bool safeBootMin,
        bool safeBootNet,
        int count)
    {
        if (count <= 0)
            return new PatchClassification(
                PatchAppliedProfile.None,
                Applied: false,
                Partial: false,
                ExpectedTotal: AppConfig.GetTotalComponents(PatchProfile.Safe, includeServerKey: false));

        // Both SafeBoot keys are required for either profile to count as clean; either one
        // missing demotes the install to Mixed even if the feature flags look right.
        bool cleanSafe = primarySet && !extendedA && !extendedB && safeBootMin && safeBootNet;
        bool cleanFull = primarySet && extendedA && extendedB && safeBootMin && safeBootNet;

        if (cleanSafe)
            return new PatchClassification(
                PatchAppliedProfile.Safe,
                Applied: true,
                Partial: false,
                ExpectedTotal: AppConfig.GetTotalComponents(PatchProfile.Safe, includeServerKey: false));
        if (cleanFull)
            return new PatchClassification(
                PatchAppliedProfile.Full,
                Applied: true,
                Partial: false,
                ExpectedTotal: AppConfig.GetTotalComponents(PatchProfile.Full, includeServerKey: false));

        bool looksLikeFullAttempt = extendedA || extendedB;
        return new PatchClassification(
            PatchAppliedProfile.Mixed,
            Applied: false,
            Partial: true,
            ExpectedTotal: AppConfig.GetTotalComponents(
                looksLikeFullAttempt ? PatchProfile.Full : PatchProfile.Safe,
                includeServerKey: false));
    }

    public static string? ExportRegistryBackup(string workingDir, string description = "NVMe_Backup")
    {
        if (string.IsNullOrEmpty(workingDir))
            return null;

        // Sanitize the description so it can't smuggle path separators / invalid filename chars.
        var safeDesc = string.IsNullOrWhiteSpace(description) ? "NVMe_Backup" : description;
        foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
            safeDesc = safeDesc.Replace(bad, '_');

        try
        {
            Directory.CreateDirectory(workingDir);
        }
        catch (Exception ex)
        {
            try { EventLogService.Write($"[RegistryService] Cannot create backup directory '{workingDir}': {ex.Message}", EventLogEntryType.Warning); } catch { }
            return null;
        }

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

            // Supplemental service-name SafeBoot entries (25H2 compat)
            using var safeMinSvc = hklm.OpenSubKey(AppConfig.SafeBootMinimalServicePath);
            if (safeMinSvc is not null)
            {
                lines.Add($@"[HKEY_LOCAL_MACHINE\{AppConfig.SafeBootMinimalServicePath}]");
                var val = safeMinSvc.GetValue("") as string;
                if (!string.IsNullOrEmpty(val))
                    lines.Add($"@=\"{val}\"");
                lines.Add("");
            }

            using var safeNetSvc = hklm.OpenSubKey(AppConfig.SafeBootNetworkServicePath);
            if (safeNetSvc is not null)
            {
                lines.Add($@"[HKEY_LOCAL_MACHINE\{AppConfig.SafeBootNetworkServicePath}]");
                var val = safeNetSvc.GetValue("") as string;
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

            // Supplemental service-name entries (25H2 compat)
            using var safeMinSvc = hklm.OpenSubKey(AppConfig.SafeBootMinimalServicePath);
            snapshot.Components["SafeBootMinimalService"] = safeMinSvc is not null ? "Present" : "Absent";

            using var safeNetSvc = hklm.OpenSubKey(AppConfig.SafeBootNetworkServicePath);
            snapshot.Components["SafeBootNetworkService"] = safeNetSvc is not null ? "Present" : "Absent";
        }
        catch
        {
            // Registry view inaccessible — record placeholders so the diff log doesn't NRE later.
            foreach (var id in AppConfig.FeatureIDs)
                snapshot.Components.TryAdd(id, "Unknown");
            snapshot.Components.TryAdd(AppConfig.ServerFeatureID, "Unknown");
            snapshot.Components.TryAdd("SafeBootMinimal", "Unknown");
            snapshot.Components.TryAdd("SafeBootNetwork", "Unknown");
            snapshot.Components.TryAdd("SafeBootMinimalService", "Unknown");
            snapshot.Components.TryAdd("SafeBootNetworkService", "Unknown");
        }

        return snapshot;
    }
}
