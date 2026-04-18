using System.IO.Compression;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class DiagnosticsService
{
    private static readonly JsonSerializerOptions BundleJsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<string?> ExportAsync(
        string workingDir,
        PreflightResult? preflight,
        List<string> logHistory)
    {
        return await Task.Run(() => Export(workingDir, preflight, logHistory));
    }

    public static async Task<string?> ExportBundleAsync(
        string workingDir,
        PreflightResult? preflight,
        List<string> logHistory,
        string? configPath = null)
    {
        return await Task.Run(() => ExportBundle(workingDir, preflight, logHistory, configPath));
    }

    // Single-file ZIP support bundle — bundles the text diagnostics report with every local
    // artifact a support engineer normally has to chase down manually (crash log, config, SQLite
    // DB snapshot, recent registry backups, recovery kit location, benchmark history).
    // Modeled on MavenWinUtil's CollectLogs.ps1 one-click diagnostic ZIP pattern.
    public static string? ExportBundle(
        string workingDir,
        PreflightResult? preflight,
        List<string> logHistory,
        string? configPath = null,
        string? outputPath = null)
    {
        if (string.IsNullOrEmpty(workingDir)) return null;
        try { Directory.CreateDirectory(workingDir); } catch { return null; }

        outputPath ??= Path.Combine(workingDir, $"NVMe_SupportBundle_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

        // Render the full diagnostics report in-memory so we don't leave a stray .txt on disk.
        var reportPath = Path.Combine(Path.GetTempPath(),
            $"NVMe_Diagnostics_{Guid.NewGuid():N}.txt");
        var report = Export(workingDir, preflight, logHistory, reportPath);

        var tempZip = outputPath + ".tmp";
        try
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                // Primary diagnostics report.
                if (report is not null && File.Exists(report))
                {
                    var sanitizedReport = TryCreateShareableDiagnosticsText(report);
                    if (!string.IsNullOrWhiteSpace(sanitizedReport))
                        AddTextEntry(zip, "diagnostics.txt", sanitizedReport);
                    else
                        AddTextEntry(zip, "diagnostics-omitted.txt",
                            "The diagnostics report was generated, but it could not be sanitized safely for inclusion in a shareable bundle.");
                }

                // Application config (sanitize first — strip anything that looks path-y from the
                // user's home so the bundle stays shareable).
                if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
                {
                    try
                    {
                        var sanitizedConfig = TryCreateShareableConfigText(configPath);
                        if (!string.IsNullOrWhiteSpace(sanitizedConfig))
                            AddTextEntry(zip, "config.json", sanitizedConfig);
                        else
                            AddTextEntry(zip, "config-omitted.txt",
                                "config.json was present but could not be parsed safely for inclusion in a shareable bundle.");
                    }
                    catch { }
                }

                // Crash log (separate LocalAppData folder than the working dir).
                try
                {
                    var crashDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "NVMeDriverPatcher");
                    foreach (var name in new[] { "crash.log", "crash.log.old" })
                    {
                        var p = Path.Combine(crashDir, name);
                        if (File.Exists(p))
                            AddSharedFileEntry(zip, p, $"crash/{name}");
                    }
                }
                catch { }

                // Recent registry backups (.reg files in working dir, newest 5 — don't balloon the zip).
                try
                {
                    var regBackups = Directory.GetFiles(workingDir, "*.reg", SearchOption.TopDirectoryOnly)
                        .Select(p => new FileInfo(p))
                        .OrderByDescending(fi => fi.LastWriteTimeUtc)
                        .Take(5);
                    foreach (var fi in regBackups)
                        AddSharedFileEntry(zip, fi.FullName, $"registry/{fi.Name}");
                }
                catch { }

                // Copy the SQLite DB and any live WAL sidecars via read-only FileStreams with
                // FileShare.ReadWrite so we don't collide with open EF Core connections.
                try
                {
                    foreach (var name in new[] { "nvmepatcher.db", "nvmepatcher.db-wal", "nvmepatcher.db-shm" })
                    {
                        var sourcePath = Path.Combine(workingDir, name);
                        if (File.Exists(sourcePath))
                            AddSharedFileEntry(zip, sourcePath, $"data/{name}");
                    }
                }
                catch { }

                // Benchmark history JSON is useful to support even when the DB is unavailable.
                try
                {
                    var benchmarkHistoryPath = Path.Combine(workingDir, "benchmark_results.json");
                    if (File.Exists(benchmarkHistoryPath))
                        AddSharedFileEntry(zip, benchmarkHistoryPath, "data/benchmark_results.json");
                }
                catch { }

                // Manifest — who generated this, when, and what version of the app.
                var manifest = new StringBuilder();
                manifest.AppendLine("NVMe Driver Patcher Support Bundle");
                manifest.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
                manifest.AppendLine($"Version: {AppConfig.AppVersion}");
                manifest.AppendLine("Machine: [redacted]");
                manifest.AppendLine("User: [redacted]");
                manifest.AppendLine($"OS: {Environment.OSVersion.VersionString}");
                manifest.AppendLine();
                manifest.AppendLine("Contents:");
                manifest.AppendLine("  diagnostics.txt   Shareable system + patch report");
                manifest.AppendLine("  config.json       App configuration with local paths redacted");
                manifest.AppendLine("  crash/*           Crash logs (if present)");
                manifest.AppendLine("  registry/*.reg    Up to 5 most-recent registry backups");
                manifest.AppendLine("  data/*.db*        SQLite DB plus WAL sidecars (if present)");
                manifest.AppendLine("  data/benchmark_results.json  Benchmark history cache (if present)");
                AddTextEntry(zip, "MANIFEST.txt", manifest.ToString());
            }

            // Atomic promote — bundle never appears half-written.
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tempZip, outputPath);
            return outputPath;
        }
        catch
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            return null;
        }
        finally
        {
            try { if (File.Exists(reportPath)) File.Delete(reportPath); } catch { }
        }
    }

    internal static string? TryCreateShareableConfigText(string configPath)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(configPath));
            if (root is not JsonObject obj) return null;
            RedactPathLikeProperties(obj);
            return obj.ToJsonString(BundleJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryCreateShareableDiagnosticsText(string reportPath)
    {
        try
        {
            var text = File.ReadAllText(reportPath);
            text = Regex.Replace(text, @"^Computer Name:\s*.*$", "Computer Name: [redacted]", RegexOptions.Multiline);
            text = Regex.Replace(text, @"^User:\s*.*$", "User: [redacted]", RegexOptions.Multiline);
            return text;
        }
        catch
        {
            return null;
        }
    }

    public static string? Export(
        string workingDir,
        PreflightResult? preflight,
        List<string> logHistory,
        string? outputPath = null)
    {
        outputPath ??= Path.Combine(workingDir, $"NVMe_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        var sb = new StringBuilder(4096);
        sb.AppendLine("================================================================================");
        sb.AppendLine("NVMe Driver Patcher - System Diagnostics Report");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Version: {AppConfig.AppVersion}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine("SYSTEM INFORMATION");
        sb.AppendLine("------------------");
        sb.AppendLine($"Computer Name: {Environment.MachineName}");
        sb.AppendLine($"User: {Environment.UserName}");
        sb.AppendLine($"OS: {Environment.OSVersion.VersionString}");

        try
        {
            using var search = new ManagementObjectSearcher("SELECT Caption, BuildNumber, Version, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");
            using var collection = search.Get();
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject os) continue;
                using (os)
                {
                    sb.AppendLine($"OS Caption: {os["Caption"]}");
                    sb.AppendLine($"OS Build: {os["BuildNumber"]}");
                    sb.AppendLine($"OS Version: {os["Version"]}");
                    sb.AppendLine($"Install Date: {os["InstallDate"]}");
                    sb.AppendLine($"Last Boot: {os["LastBootUpTime"]}");
                }
                break;
            }
        }
        catch { sb.AppendLine("Unable to retrieve OS information"); }

        sb.AppendLine().AppendLine("HARDWARE").AppendLine("--------");
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
            using var collection = search.Get();
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject cs) continue;
                using (cs)
                {
                    sb.AppendLine($"Manufacturer: {cs["Manufacturer"]}");
                    sb.AppendLine($"Model: {cs["Model"]}");
                    long? memBytes = null;
                    try { memBytes = Convert.ToInt64(cs["TotalPhysicalMemory"] ?? 0); } catch { }
                    sb.AppendLine(memBytes is { } mb && mb > 0
                        ? $"Total RAM: {Math.Round(mb / 1073741824.0, 2)} GB"
                        : "Total RAM: Unknown");
                }
                break;
            }
        }
        catch { sb.AppendLine("Unable to retrieve hardware information"); }

        var drives = preflight?.CachedDrives ?? DriveService.GetSystemDrives();
        sb.AppendLine().AppendLine("STORAGE DRIVES").AppendLine("--------------");
        foreach (var drive in drives)
        {
            string nvmeTag = drive.IsNVMe ? " [NVMe]" : "";
            string bootTag = drive.IsBoot ? " [BOOT]" : "";
            sb.AppendLine($"Disk {drive.Number}: {drive.Name} ({drive.Size}){nvmeTag}{bootTag}");
            sb.AppendLine($"  Bus Type: {drive.BusType}");
            sb.AppendLine($"  PNP ID: {drive.PNPDeviceID}");
        }

        var healthData = preflight?.CachedHealth ?? DriveService.GetNVMeHealthData();
        sb.AppendLine().AppendLine("NVMe HEALTH DATA").AppendLine("-----------------");
        if (healthData.Count > 0)
        {
            foreach (var (diskKey, hd) in healthData)
            {
                sb.AppendLine($"Disk {diskKey}:");
                sb.AppendLine($"  Health: {hd.HealthStatus} | Status: {hd.OperationalStatus}");
                if (hd.Temperature != "N/A") sb.AppendLine($"  Temperature: {hd.Temperature}");
                if (hd.Wear != "N/A") sb.AppendLine($"  Life Remaining: {hd.Wear}");
                if (hd.PowerOnHours != "N/A") sb.AppendLine($"  Power-On Hours: {hd.PowerOnHours}");
                if (hd.MediaErrors > 0) sb.AppendLine($"  Media Errors: {hd.MediaErrors}");
            }
        }
        else sb.AppendLine("  No health data available");

        var driverInfo = preflight?.DriverInfo ?? DriveService.GetNVMeDriverInfo();
        sb.AppendLine().AppendLine("NVMe DRIVER INFORMATION").AppendLine("-----------------------");
        sb.AppendLine($"Current Driver: {driverInfo.CurrentDriver}");
        sb.AppendLine($"Inbox Version: {driverInfo.InboxVersion}");
        sb.AppendLine($"Third-Party: {(driverInfo.HasThirdParty ? driverInfo.ThirdPartyName : "No")}");
        sb.AppendLine($"Queue Depth: {driverInfo.QueueDepth}");

        var nativeStatus = preflight?.NativeNVMeStatus ?? DriveService.TestNativeNVMeActive();
        sb.AppendLine().AppendLine("NATIVE NVMe DRIVER STATUS").AppendLine("-------------------------");
        sb.AppendLine($"Native NVMe Active: {(nativeStatus.IsActive ? "Yes" : "No")}");
        sb.AppendLine($"Active Driver: {nativeStatus.ActiveDriver}");
        sb.AppendLine($"Device Category: {nativeStatus.DeviceCategory}");
        if (nativeStatus.StorageDisks.Count > 0)
        {
            sb.AppendLine("Storage Disks:");
            foreach (var sd in nativeStatus.StorageDisks) sb.AppendLine($"  - {sd}");
        }
        sb.AppendLine($"Details: {nativeStatus.Details}");

        var bypassStatus = preflight?.BypassIOStatus ?? DriveService.GetBypassIOStatus();
        sb.AppendLine().AppendLine("BYPASSIO / DIRECTSTORAGE STATUS").AppendLine("-------------------------------");
        sb.AppendLine($"BypassIO Supported: {(bypassStatus.Supported ? "Yes" : "No")}");
        sb.AppendLine($"Storage Type: {bypassStatus.StorageType}");
        if (!string.IsNullOrEmpty(bypassStatus.BlockedBy)) sb.AppendLine($"Blocked By: {bypassStatus.BlockedBy}");
        if (!string.IsNullOrEmpty(bypassStatus.Warning)) sb.AppendLine($"WARNING: {bypassStatus.Warning}");

        var buildDetails = preflight?.BuildDetails ?? DriveService.GetWindowsBuildDetails();
        sb.AppendLine().AppendLine("WINDOWS BUILD DETAILS").AppendLine("--------------------");
        sb.AppendLine($"Build Number: {buildDetails.BuildNumber}");
        sb.AppendLine($"Display Version: {buildDetails.DisplayVersion}");
        sb.AppendLine($"UBR: {buildDetails.UBR}");
        sb.AppendLine($"Is 24H2+: {buildDetails.Is24H2OrLater}");

        sb.AppendLine().AppendLine("CHASSIS / POWER").AppendLine("---------------");
        sb.AppendLine($"Is Laptop: {(preflight?.IsLaptop ?? false ? "Yes (APST warning applies)" : "No (Desktop)")}");

        sb.AppendLine().AppendLine("BITLOCKER STATUS").AppendLine("----------------");
        sb.AppendLine($"System Drive Encrypted: {(preflight?.BitLockerEnabled ?? false ? "Yes" : "No")}");

        var incompatSw = preflight?.IncompatibleSoftware ?? DriveService.GetIncompatibleSoftware();
        sb.AppendLine().AppendLine("INCOMPATIBLE SOFTWARE").AppendLine("---------------------");
        if (incompatSw.Count > 0)
        {
            foreach (var sw in incompatSw) sb.AppendLine($"  [{sw.Severity}] {sw.Name}: {sw.Message}");
        }
        else sb.AppendLine("  None detected");

        var migration = preflight?.CachedMigration ?? DriveService.GetStorageDiskMigration();
        sb.AppendLine().AppendLine("DRIVE MIGRATION STATUS").AppendLine("----------------------");
        if (migration.Migrated.Count > 0)
        {
            sb.AppendLine("Under 'Storage disks' (native nvmedisk.sys):");
            foreach (var d in migration.Migrated) sb.AppendLine($"  + {d}");
        }
        if (migration.Legacy.Count > 0)
        {
            sb.AppendLine("Under 'Disk drives' (legacy stornvme.sys):");
            foreach (var d in migration.Legacy) sb.AppendLine($"  - {d}");
        }

        var status = RegistryService.GetPatchStatus();
        sb.AppendLine().AppendLine("PATCH STATUS").AppendLine("------------");
        sb.AppendLine($"Applied: {status.Applied}");
        sb.AppendLine($"Partial: {status.Partial}");
        sb.AppendLine($"Components: {status.Count}/{status.Total}");
        sb.AppendLine($"Applied Keys: {string.Join(", ", status.Keys)}");

        // Benchmark history
        var benchHistory = BenchmarkService.GetHistory(workingDir);
        sb.AppendLine().AppendLine("BENCHMARK HISTORY").AppendLine("-----------------");
        if (benchHistory.Count > 0)
        {
            foreach (var bh in benchHistory)
                sb.AppendLine($"  [{bh.Label}] {bh.Timestamp} -- Read: {bh.Read.IOPS} IOPS, Write: {bh.Write.IOPS} IOPS");
        }
        else sb.AppendLine("  No benchmark history");

        sb.AppendLine().AppendLine("ACTIVITY LOG").AppendLine("------------");
        // The rest of the report uses StringBuilder.AppendLine which emits Environment.NewLine
        // (CRLF on Windows). Match that so the file opens cleanly in Notepad / older editors
        // instead of showing single-line wrap-around for the whole activity section.
        sb.AppendLine(string.Join(Environment.NewLine, logHistory ?? new List<string>()));
        sb.AppendLine().AppendLine("================================================================================");
        sb.AppendLine("End of Diagnostics Report");

        try
        {
            // Atomic write so a power loss in the middle of an export never leaves a half-written report.
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var tempPath = outputPath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                sw.Write(sb.ToString());
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, outputPath, overwrite: true);
            return outputPath;
        }
        catch { return null; }
    }

    private static void RedactPathLikeProperties(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var entry in obj.ToList())
                {
                    if (entry.Value is JsonValue value &&
                        value.TryGetValue<string>(out var textValue) &&
                        ShouldRedactPathLikeProperty(entry.Key, textValue))
                    {
                        obj[entry.Key] = RedactPathValue(textValue);
                        continue;
                    }

                    RedactPathLikeProperties(entry.Value);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                    RedactPathLikeProperties(item);
                break;
        }
    }

    private static bool ShouldRedactPathLikeProperty(string propertyName, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !LooksLikePath(value))
            return false;

        return propertyName.EndsWith("Path", StringComparison.OrdinalIgnoreCase)
            || propertyName.EndsWith("Dir", StringComparison.OrdinalIgnoreCase)
            || propertyName.EndsWith("Directory", StringComparison.OrdinalIgnoreCase)
            || propertyName.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)
            || propertyName.EndsWith("File", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePath(string value) =>
        Path.IsPathRooted(value)
        || value.Contains(Path.DirectorySeparatorChar)
        || value.Contains(Path.AltDirectorySeparatorChar);

    private static string RedactPathValue(string value)
    {
        var trimmed = value.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed))
            return "[redacted path]";

        var leaf = Path.GetFileName(trimmed);
        if (!string.IsNullOrWhiteSpace(leaf))
            return leaf;

        var parent = Path.GetDirectoryName(trimmed);
        var parentLeaf = string.IsNullOrWhiteSpace(parent) ? string.Empty : Path.GetFileName(parent);
        return string.IsNullOrWhiteSpace(parentLeaf) ? "[redacted path]" : parentLeaf;
    }

    private static void AddSharedFileEntry(ZipArchive zip, string sourcePath, string entryName)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.CopyTo(es);
    }

    private static void AddTextEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
