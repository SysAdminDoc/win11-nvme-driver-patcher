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

    // Backtracking guard: cap per-call regex time so a pathological diagnostic line
    // (e.g. a stack trace without newlines) cannot stall the export thread.
    private static readonly TimeSpan RedactTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex RxRedactComputerName = new(@"^Computer Name:\s*.*$", RegexOptions.Multiline | RegexOptions.Compiled, RedactTimeout);
    private static readonly Regex RxRedactUser = new(@"^User:\s*.*$", RegexOptions.Multiline | RegexOptions.Compiled, RedactTimeout);
    private static readonly Regex RxRedactPnpId = new(@"^(\s*PNP ID:\s*).*$", RegexOptions.Multiline | RegexOptions.Compiled, RedactTimeout);
    private static readonly Regex RxRedactUserPath = new(@"\b([A-Za-z]:\\Users\\)([^\\\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RedactTimeout);

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
            var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

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
                        if (!File.Exists(p)) continue;

                        var sanitizedCrashLog = TryCreateShareableLogText(p);
                        if (!string.IsNullOrWhiteSpace(sanitizedCrashLog))
                            AddTextEntry(zip, $"crash/{name}", sanitizedCrashLog);
                        else
                            AddTextEntry(zip, $"crash/{name}.omitted.txt",
                                $"{name} was present, but it could not be sanitized safely for inclusion in a shareable bundle.");
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

                // SQLite's Online Backup API takes one point-in-time view across the main DB and
                // active WAL. Only the quick_check-validated, standalone snapshot enters the ZIP.
                var sourceDatabase = Path.Combine(workingDir, "nvmepatcher.db");
                if (File.Exists(sourceDatabase))
                {
                    var snapshotPath = Path.Combine(
                        Path.GetTempPath(),
                        $"NVMePatcher_SupportDb_{Guid.NewGuid():N}.db");
                    try
                    {
                        var snapshot = SqliteSnapshotService.CreateValidatedSnapshot(
                            sourceDatabase,
                            snapshotPath);
                        if (snapshot.Success && snapshot.SnapshotPath is not null)
                            AddSharedFileEntry(zip, snapshot.SnapshotPath, "data/nvmepatcher.db");
                        else
                            AddTextEntry(zip, "data/nvmepatcher.db.omitted.txt", snapshot.Summary);
                    }
                    finally
                    {
                        SqliteSnapshotService.DeleteSnapshotFiles(snapshotPath);
                    }
                }

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
                manifest.AppendLine("  data/nvmepatcher.db  Validated point-in-time SQLite Online Backup snapshot (if present)");
                manifest.AppendLine("  data/benchmark_results.json  Benchmark history cache (if present)");
                manifest.AppendLine();
                manifest.Append(BuildTrustLedger(workingDir));
                AddTextEntry(zip, "MANIFEST.txt", manifest.ToString());
            }

            GeneratedArtifactManifestService.PublishZipManifest(
                tempZip,
                "support-bundle",
                SupportBundleRole);
            var integrity = GeneratedArtifactManifestService.VerifyZip(tempZip);
            if (!integrity.Success)
                throw new InvalidDataException(integrity.Summary + " " +
                    string.Join("; ", integrity.Issues.Select(i => $"{i.RelativePath}: {i.Detail}")));

            // Atomic promote — bundle never appears half-written. File.Move(overwrite:true)
            // is atomic on NTFS, so the final path never transiently disappears (unlike the
            // previous Delete + Move sequence, which had a small window where both were gone).
            File.Move(tempZip, outputPath, overwrite: true);
            return outputPath;
        }
        catch
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            return null;
        }
        finally
        {
            // Export() internally writes `<reportPath>.tmp` then renames — if it threw between
            // the two, the sidecar can leak. Sweep both to keep %TEMP% clean.
            try { if (File.Exists(reportPath)) File.Delete(reportPath); } catch { }
            try
            {
                var reportTemp = reportPath + ".tmp";
                if (File.Exists(reportTemp)) File.Delete(reportTemp);
            }
            catch { }
        }
    }

    /// <summary>
    /// Trust ledger — the auditability section of MANIFEST.txt. Records exactly which
    /// versions/binaries/feature-state the machine was running when the bundle was made,
    /// so support triage doesn't have to guess: app version + channel, ViVeTool cache
    /// identity (path/size/SHA-256/file version), FeatureStore blob hash, per-ID native
    /// feature-configuration state, the build-selected fallback ID set, and the
    /// bind-block rule match. Best-effort: every probe degrades to a "(unavailable)" line.
    /// </summary>
    internal static string BuildTrustLedger(string workingDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Trust ledger:");

        // App identity + channel (MSI installs leave the EventLogSourceRegistered marker).
        sb.AppendLine($"  App version:        {AppConfig.AppVersion}");
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"Software\SysAdminDoc\NVMeDriverPatcher");
            var msi = key?.GetValue("EventLogSourceRegistered") is int i && i == 1;
            sb.AppendLine($"  Install channel:    {(msi ? "MSI (per-machine)" : "portable")}");
        }
        catch { sb.AppendLine("  Install channel:    (unavailable)"); }

        // ViVeTool cache identity.
        try
        {
            var viveExe = ViVeToolService.CachedExePath(workingDir);
            if (File.Exists(viveExe))
            {
                var fi = new FileInfo(viveExe);
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(viveExe))
                    hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
                var fileVer = System.Diagnostics.FileVersionInfo.GetVersionInfo(viveExe).FileVersion ?? "?";
                sb.AppendLine($"  ViVeTool cache:     {viveExe} ({fi.Length} bytes, v{fileVer})");
                sb.AppendLine($"  ViVeTool SHA-256:   {hash}");
                sb.AppendLine($"  ViVeTool auth:      {(ViVeToolService.IsInstalled(workingDir)
                    ? "manifest-sha256 (complete payload)"
                    : "FAILED complete manifest validation")}");
            }
            else
            {
                sb.AppendLine("  ViVeTool cache:     not downloaded");
            }
        }
        catch { sb.AppendLine("  ViVeTool cache:     (unavailable)"); }

        // FeatureStore blob hash + native per-ID configuration state.
        try
        {
            var blobPath = Path.Combine(Path.GetTempPath(), $"nvmep_fs_{Guid.NewGuid():N}.bin");
            var exported = FeatureStoreWriterService.ExportBlob(blobPath);
            if (exported is not null)
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(exported))
                    sb.AppendLine($"  FeatureStore blob:  sha256 {Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant()}");
                try { File.Delete(exported); File.Delete(exported + ".hex.txt"); } catch { }
            }
            else
            {
                sb.AppendLine("  FeatureStore blob:  absent");
            }
        }
        catch { sb.AppendLine("  FeatureStore blob:  (unavailable)"); }

        try
        {
            foreach (var state in FeatureStoreWriterService.QueryAllKnownConfigurations().Where(c => c.Found))
                sb.AppendLine($"  Feature {state.FeatureId} [{state.Store}]: state={state.EnabledState} priority={state.Priority}");
        }
        catch { }

        // Fallback set + bind-block rule for this build.
        try
        {
            var set = ViVeToolService.SelectFallbackSet();
            sb.AppendLine($"  Fallback ID set:    {set.Name} ({string.Join(",", set.Ids)}; {set.Confidence})");
            var build = DriveService.GetWindowsBuildDetails();
            if (build is not null)
                sb.AppendLine($"  Bind-block rule:    build {build.BuildNumber}.{build.UBR} -> " +
                              (AppConfig.IsKnownBindBlockedBuild(build.BuildNumber, build.UBR)
                                  ? "MATCHED (nvmedisk may be unable to bind)" : "not matched"));
        }
        catch { sb.AppendLine("  Fallback ID set:    (unavailable)"); }

        try
        {
            var rule = WindowsBuildRulesService.MatchCurrent(workingDir);
            sb.AppendLine($"  Enablement rule:    {(rule is null ? "(no match)" : $"{rule.Id} -> {rule.ExpectedPath} ({rule.Confidence}, reviewed {rule.LastReviewed})")}");
        }
        catch { sb.AppendLine("  Enablement rule:    (unavailable)"); }

        var recoverySafety = RecoverySafetyGateService.Snapshot();
        sb.AppendLine($"  Recovery safety:    {(recoverySafety.MutationAllowed ? "resolved" : "BLOCKED")}");
        foreach (var failure in recoverySafety.Failures)
            sb.AppendLine($"    {failure.Source}: {failure.Summary} ({failure.DetectedAtUtc:O})");

        try
        {
            sb.AppendLine("  Data files:");
            foreach (var f in DataFileProvenanceService.InspectAll(workingDir))
            {
                sb.AppendLine($"    {f.FileName}: {f.SourceKind}; schema {f.SchemaVersion}; reviewed {f.NewestLastReviewed}; sha256 {f.Sha256}");
                if (!string.IsNullOrWhiteSpace(f.ShippedSha256))
                    sb.AppendLine($"      shipped sha256 {f.ShippedSha256}; customized={(f.IsCustomized ? "yes" : "no")}; stale={(f.IsStale ? "yes" : "no")}");
            }
        }
        catch { sb.AppendLine("  Data files:         (unavailable)"); }

        try
        {
            sb.AppendLine();
            sb.AppendLine("NVMe Controller Identify:");
            bool anyFound = false;
            for (int n = 0; n < 16; n++)
            {
                var id = NvmeIdentifyService.Query(n);
                if (!id.Success) continue;
                anyFound = true;
                sb.AppendLine($"  PhysicalDrive{n}: {id.ModelNumber.Trim()} / FW {id.FirmwareRevision.Trim()} / VID {id.VendorId} / SN {id.RedactedSerialNumber} / {id.NumberOfPowerStates} power states");
            }
            if (!anyFound)
                sb.AppendLine("  (no NVMe controllers responded)");
        }
        catch { sb.AppendLine("  NVMe Identify:      (unavailable)"); }

        return sb.ToString();
    }

    private static string SupportBundleRole(string relativePath)
    {
        if (relativePath.Equals("diagnostics.txt", StringComparison.OrdinalIgnoreCase)) return "diagnostics";
        if (relativePath.Equals("config.json", StringComparison.OrdinalIgnoreCase)) return "redacted-config";
        if (relativePath.Equals("MANIFEST.txt", StringComparison.OrdinalIgnoreCase)) return "human-readable-index";
        if (relativePath.EndsWith("-omitted.txt", StringComparison.OrdinalIgnoreCase)) return "omission-notice";
        if (relativePath.StartsWith("crash/", StringComparison.OrdinalIgnoreCase)) return "redacted-crash-log";
        if (relativePath.StartsWith("registry/", StringComparison.OrdinalIgnoreCase)) return "registry-backup";
        if (relativePath.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) return "diagnostic-data";
        return "support-evidence";
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
            return RedactShareableText(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryCreateShareableLogText(string logPath)
    {
        try
        {
            var text = File.ReadAllText(logPath);
            return RedactShareableText(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string RedactShareableText(string text)
    {
        text = RxRedactComputerName.Replace(text, "Computer Name: [redacted]");
        text = RxRedactUser.Replace(text, "User: [redacted]");
        text = RxRedactPnpId.Replace(text, "${1}[redacted]");
        text = RxRedactUserPath.Replace(text, match => $"{match.Groups[1].Value}[redacted]");
        return text;
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
            using var collection = WmiQueryHelper.ExecuteWithTimeout(search);
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
            using var collection = WmiQueryHelper.ExecuteWithTimeout(search);
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

        var dataFileProvenance = preflight?.DataFileProvenance is { Count: > 0 } fromPreflight
            ? fromPreflight
            : DataFileProvenanceService.InspectAll(workingDir);
        sb.AppendLine().AppendLine("DATA FILE PROVENANCE").AppendLine("--------------------");
        sb.AppendLine(DataFileProvenanceService.RenderForDiagnostics(dataFileProvenance, includePath: true));

        sb.AppendLine().AppendLine("CHASSIS / POWER").AppendLine("---------------");
        sb.AppendLine($"Is Laptop: {(preflight?.IsLaptop ?? false ? "Yes (APST warning applies)" : "No (Desktop)")}");

        sb.AppendLine().AppendLine("BITLOCKER STATUS").AppendLine("----------------");
        var bitLockerProof = preflight?.BitLockerRecovery ?? BitLockerRecoveryService.InspectSystemVolume();
        sb.AppendLine($"Authoritative Probe: {(bitLockerProof.Volume.ProbeSucceeded ? "Succeeded" : "Failed")}");
        sb.AppendLine($"System Drive Encrypted: {(bitLockerProof.Volume.ProbeSucceeded ? (bitLockerProof.Volume.IsEncrypted ? "Yes" : "No") : "Unknown")}");
        sb.AppendLine($"Recovery Proof: {(bitLockerProof.ReadyForMutation ? "Ready" : "Blocked")}");
        sb.AppendLine($"Recovery Protector IDs: {(bitLockerProof.Volume.RecoveryProtectorIds.Count > 0 ? string.Join(", ", bitLockerProof.Volume.RecoveryProtectorIds) : "None")}");
        sb.AppendLine($"Directory Join: {bitLockerProof.DirectoryJoin.Kind}");
        sb.AppendLine($"Detail: {bitLockerProof.Detail}");

        var databaseState = DataService.DatabaseState;
        sb.AppendLine().AppendLine("HISTORY DATABASE").AppendLine("----------------");
        sb.AppendLine($"Availability: {databaseState.Availability}");
        sb.AppendLine($"Schema Version: {databaseState.SchemaVersion}");
        sb.AppendLine($"Summary: {databaseState.Summary}");
        if (!databaseState.IsAvailable)
            sb.AppendLine($"Recovery: {databaseState.RecoveryAction}");
        if (!string.IsNullOrWhiteSpace(databaseState.BackupPath))
            sb.AppendLine($"Pre-upgrade Backup: {databaseState.BackupPath}");

        var recoverySafety = RecoverySafetyGateService.Snapshot();
        sb.AppendLine().AppendLine("STARTUP RECOVERY SAFETY").AppendLine("-----------------------");
        sb.AppendLine($"Mutation Allowed: {(recoverySafety.MutationAllowed ? "Yes" : "No")}");
        sb.AppendLine($"Summary: {recoverySafety.Summary}");
        foreach (var failure in recoverySafety.Failures)
            sb.AppendLine($"  [{failure.DetectedAtUtc:O}] {failure.Source}: {failure.Summary}");

        var criticalProbes = preflight?.CriticalProbes is { Items.Count: > 0 } fromPreflightProbes
            ? fromPreflightProbes
            : CriticalEnvironmentProbeService.EvaluateRegistryPatch();
        sb.AppendLine().AppendLine("CRITICAL ENVIRONMENT PROBES").AppendLine("---------------------------");
        sb.AppendLine($"Scope: {criticalProbes.Scope}");
        sb.AppendLine($"Mutation Ready: {(criticalProbes.AllPassed ? "Yes" : "No")}");
        foreach (var probe in criticalProbes.Items)
        {
            sb.AppendLine($"  [{probe.Verdict}] {probe.Id}: {probe.ReasonCode} — {probe.Detail}");
            sb.AppendLine($"    Observed UTC: {probe.ObservedAtUtc:O}");
            if (!string.IsNullOrWhiteSpace(probe.NativeError))
                sb.AppendLine($"    Native Error: {probe.NativeError}");
            foreach (var evidence in probe.Evidence)
                sb.AppendLine($"    Evidence: {evidence}");
        }

        var incompatSw = preflight?.IncompatibleSoftware ?? DriveService.GetIncompatibleSoftware();
        sb.AppendLine().AppendLine("INCOMPATIBLE SOFTWARE").AppendLine("---------------------");
        if (incompatSw.Count > 0)
        {
            foreach (var sw in incompatSw) sb.AppendLine($"  [{sw.Severity}] {sw.Name}: {sw.Message}");
        }
        else sb.AppendLine("  None detected");

        var blockedBackupDrivers = preflight?.CodeIntegrityBlockedDrivers ?? CodeIntegrityEventService.RecentBackupDriverBlocks();
        sb.AppendLine().AppendLine("CODEINTEGRITY BLOCKED BACKUP DRIVERS").AppendLine("------------------------------------");
        if (blockedBackupDrivers.Count > 0)
        {
            foreach (var ev in blockedBackupDrivers)
            {
                sb.AppendLine($"  [{ev.Mode}] Event {ev.EventId} at {ev.TimestampUtc:u}");
                sb.AppendLine($"    Driver: {ev.DriverFile} ({ev.DriverDescription})");
                sb.AppendLine($"    Affected products: {ev.AffectedProducts}");
                sb.AppendLine($"    Evidence: {ev.Evidence}");
            }
        }
        else sb.AppendLine("  No recent backup-driver CodeIntegrity block events detected");

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

        // PER-CONTROLLER PnP EVIDENCE (RD P1) — captures INF / provider / class / hardware /
        // compatible IDs for each NVMe controller. When nvmedisk.sys is bound with no patch
        // breadcrumbs this is the evidence that distinguishes Microsoft's official rollout from a
        // forced 'driver method' install (which reverts via Device Manager, not registry cleanup).
        try
        {
            var controllerAudit = preflight?.ControllerAudit ?? PerControllerAuditService.Audit();
            var testSigningEnabled = preflight?.TestSigningEnabled ?? PreflightService.DetectBcdTestSigningEnabled();
            var customWorkaround = PreflightService.ClassifyCustomNativeWorkaround(testSigningEnabled, controllerAudit);

            sb.AppendLine().AppendLine("CUSTOM / TEST-SIGNED NVMe WORKAROUND EVIDENCE").AppendLine("---------------------------------------------");
            sb.AppendLine($"BCD TESTSIGNING: {FormatNullableBool(testSigningEnabled)}");
            if (customWorkaround is null)
            {
                sb.AppendLine("  No custom-INF/test-signing native NVMe workaround evidence detected.");
            }
            else
            {
                sb.AppendLine($"  WARNING: {customWorkaround.Message}");
                foreach (var c in PerControllerAuditService.FindCustomNativeWorkaroundEvidence(controllerAudit.Controllers))
                {
                    sb.AppendLine($"  Suspect binding: {c.FriendlyName} (id={c.InstanceId})");
                    sb.AppendLine($"    driver={c.BoundDriver}  inf={c.InfName}  provider={c.DriverProvider}  version={c.BoundDriverVersion}");
                    sb.AppendLine($"    hardware={c.HardwareId}  compat={c.CompatibleId}");
                }
            }

            sb.AppendLine().AppendLine("PER-CONTROLLER PnP EVIDENCE").AppendLine("---------------------------");
            sb.AppendLine(controllerAudit.Summary);

            bool fallbackEvidence;
            try { fallbackEvidence = FeatureStoreWriterService.HasFallbackEvidence(); }
            catch { fallbackEvidence = false; }

            if (PatchVerificationService.IsUntrackedDriverActivation(
                    nativeStatus.IsActive, status.Count, fallbackEvidence))
            {
                sb.AppendLine();
                sb.AppendLine(PatchVerificationService.UntrackedDriverActivationNote);
                sb.AppendLine();
                sb.AppendLine(controllerAudit.RenderForcedDriverEvidence());
            }
            else
            {
                foreach (var c in controllerAudit.Controllers)
                {
                    sb.AppendLine($"  {(c.IsNative ? "[NATIVE]" : "[LEGACY]")} {c.FriendlyName} (id={c.InstanceId})");
                    sb.AppendLine($"    driver={c.BoundDriver}  inf={c.InfName}  provider={c.DriverProvider}  version={c.BoundDriverVersion}  class={c.DeviceClass}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("DRIVER CANDIDATES / RANK EVIDENCE");
            sb.AppendLine(controllerAudit.RenderDriverCandidateEvidence());
        }
        catch (Exception ex) { sb.AppendLine($"  Per-controller audit failed: {ex.Message}"); }

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

    private static string FormatNullableBool(bool? value) => value switch
    {
        true => "Yes",
        false => "No",
        _ => "Unknown"
    };

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
