using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class BenchmarkService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<string?> InstallDiskSpdAsync(string workingDir, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(workingDir))
            return null;

        var diskSpdDir = Path.Combine(workingDir, "DiskSpd");
        var diskSpdExe = Path.Combine(diskSpdDir, "diskspd.exe");
        if (File.Exists(diskSpdExe)) return diskSpdExe;

        log?.Invoke("Downloading Microsoft DiskSpd benchmark tool...");
        try
        {
            Directory.CreateDirectory(diskSpdDir);
            var zipUrl = "https://github.com/microsoft/diskspd/releases/latest/download/DiskSpd.ZIP";
            var zipPath = Path.Combine(diskSpdDir, "DiskSpd.zip");

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"NVMeDriverPatcher/{Models.AppConfig.AppVersion}");
            var bytes = await client.GetByteArrayAsync(zipUrl);
            await File.WriteAllBytesAsync(zipPath, bytes);

            ZipFile.ExtractToDirectory(zipPath, diskSpdDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { }

            // Prefer amd64 (we're a 64-bit process), but fall back to anything we can find.
            var allExes = Directory.GetFiles(diskSpdDir, "diskspd.exe", SearchOption.AllDirectories);
            var found = allExes.FirstOrDefault(f => f.Contains("amd64", StringComparison.OrdinalIgnoreCase))
                ?? allExes.FirstOrDefault();

            if (found is not null)
            {
                if (!string.Equals(found, diskSpdExe, StringComparison.OrdinalIgnoreCase))
                    File.Copy(found, diskSpdExe, overwrite: true);
                log?.Invoke("DiskSpd downloaded successfully");
                return diskSpdExe;
            }

            log?.Invoke("[ERROR] DiskSpd exe not found in archive");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Failed to download DiskSpd: {ex.Message}");
        }
        return null;
    }

    public static async Task<BenchmarkResult?> RunBenchmarkAsync(
        string workingDir,
        string label,
        Action<string>? log = null,
        Action<int, string>? progress = null)
    {
        var exe = await InstallDiskSpdAsync(workingDir, log);
        if (exe is null) return null;

        // Find a partition on an NVMe drive to benchmark
        string benchDir = workingDir;
        try
        {
            var nvmeDiskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var diskSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId FROM MSFT_PhysicalDisk WHERE BusType=17"))
            using (var diskCollection = diskSearch.Get())
            {
                foreach (var raw in diskCollection)
                {
                    if (raw is not ManagementObject d) continue;
                    using (d)
                    {
                        var id = d["DeviceId"]?.ToString();
                        if (!string.IsNullOrEmpty(id)) nvmeDiskIds.Add(id);
                    }
                }
            }

            if (nvmeDiskIds.Count > 0)
            {
                using var partSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                    "SELECT DiskNumber, DriveLetter FROM MSFT_Partition WHERE DriveLetter!=0");
                using var partCollection = partSearch.Get();
                foreach (var raw in partCollection)
                {
                    if (raw is not ManagementObject part) continue;
                    using (part)
                    {
                        var diskNum = part["DiskNumber"]?.ToString();
                        if (string.IsNullOrEmpty(diskNum) || !nvmeDiskIds.Contains(diskNum)) continue;

                        var letter = part["DriveLetter"];
                        if (letter is char ch && char.IsLetter(ch))
                        {
                            var nvmeRoot = $"{ch}:\\";
                            var nvmeTempDir = Path.Combine(nvmeRoot, "NVMePatcher_Bench");
                            try
                            {
                                Directory.CreateDirectory(nvmeTempDir);
                                benchDir = nvmeTempDir;
                                log?.Invoke($"Benchmarking NVMe drive: {nvmeRoot} (Disk {diskNum})");
                            }
                            catch (Exception dirEx)
                            {
                                log?.Invoke($"[WARNING] Could not stage benchmark directory on {nvmeRoot}: {dirEx.Message}. Falling back to working folder.");
                            }
                            break;
                        }
                    }
                }
            }
        }
        catch { }

        var testFile = Path.Combine(benchDir, "diskspd_test.dat");
        var result = new BenchmarkResult { Label = label, Timestamp = DateTime.Now.ToString("o") };

        try
        {
            log?.Invoke("Running 4K random read benchmark (30s)...");
            progress?.Invoke(20, "Benchmarking reads...");
            var readOutput = await RunDiskSpd(exe, $"-c128M -d30 -w0 -t4 -o16 -b4K -r -Sh -L \"{testFile}\"");
            result.Read = ParseDiskSpdOutput(readOutput);

            log?.Invoke("Running 4K random write benchmark (30s)...");
            progress?.Invoke(60, "Benchmarking writes...");
            var writeOutput = await RunDiskSpd(exe, $"-c128M -d30 -w100 -t4 -o16 -b4K -r -Sh -L \"{testFile}\"");
            result.Write = ParseDiskSpdOutput(writeOutput);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Benchmark error: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(testFile)) File.Delete(testFile); } catch { }
            // Only blow away the staging directory we created — never the user's working folder.
            if (!string.Equals(benchDir, workingDir, StringComparison.OrdinalIgnoreCase) &&
                benchDir.EndsWith("NVMePatcher_Bench", StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(benchDir, true); } catch { }
            }
            progress?.Invoke(0, "");
        }

        return result;
    }

    private static async Task<string> RunDiskSpd(string exePath, string args)
    {
        var psi = new ProcessStartInfo(exePath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start DiskSpd process");

        // Read stdout AND stderr in parallel — diskspd will deadlock if stderr fills its pipe
        // buffer while we're only reading stdout.
        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();
        // 90s ceiling: 30s read + overhead + 30s write + overhead. If diskspd hangs, kill it.
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new InvalidOperationException("DiskSpd timed out after 120 seconds.");
        }
        return await outputTask;
    }

    public static BenchmarkMetrics ParseDiskSpdOutput(string rawOutput)
    {
        var metrics = new BenchmarkMetrics();
        try
        {
            foreach (var line in rawOutput.Split('\n'))
            {
                if (!Regex.IsMatch(line, @"^\s*total:")) continue;

                var parts = line.Split('|').Select(p => p.Trim()).ToArray();
                if (parts.Length < 4) continue;

                var culture = CultureInfo.InvariantCulture;
                var throughputStr = Regex.Replace(parts[2], @"[^\d.,]", "").Replace(',', '.');
                var iopsStr = Regex.Replace(parts[3], @"[^\d.,]", "").Replace(',', '.');

                if (double.TryParse(throughputStr, NumberStyles.Float, culture, out var throughput))
                    metrics.ThroughputMBs = Math.Round(throughput, 2);
                if (double.TryParse(iopsStr, NumberStyles.Float, culture, out var iops))
                    metrics.IOPS = Math.Round(iops, 0);

                if (parts.Length >= 5)
                {
                    var latStr = Regex.Replace(parts[4], @"[^\d.,]", "").Replace(',', '.');
                    if (double.TryParse(latStr, NumberStyles.Float, culture, out var lat))
                        metrics.AvgLatencyMs = Math.Round(lat, 3);
                }
                break;
            }
        }
        catch { }
        return metrics;
    }

    public static void SaveResults(string workingDir, BenchmarkResult result)
    {
        if (string.IsNullOrEmpty(workingDir) || result is null) return;

        var benchFile = Path.Combine(workingDir, "benchmark_results.json");
        try
        {
            var existing = new List<BenchmarkResult>();
            if (File.Exists(benchFile))
            {
                try
                {
                    var json = File.ReadAllText(benchFile);
                    var parsed = JsonSerializer.Deserialize<List<BenchmarkResult>>(json, JsonOptions);
                    if (parsed is not null) existing = parsed;
                }
                catch
                {
                    // Corrupt file — preserve it for forensics, then start fresh.
                    try { File.Move(benchFile, benchFile + ".corrupt", overwrite: true); } catch { }
                }
            }
            existing.Add(result);
            if (existing.Count > 10) existing = existing.Skip(existing.Count - 10).ToList();

            // Atomic write so a crash mid-save doesn't truncate the history file.
            var tempFile = benchFile + ".tmp";
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                sw.Write(JsonSerializer.Serialize(existing, JsonOptions));
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempFile, benchFile, overwrite: true);
        }
        catch { }

        try
        {
            if (result.Read.IOPS > 0 || result.Write.IOPS > 0)
                DataService.SaveBenchmark(result);
        }
        catch { }
    }

    public static List<BenchmarkResult> GetHistory(string workingDir)
    {
        if (string.IsNullOrEmpty(workingDir)) return [];
        var benchFile = Path.Combine(workingDir, "benchmark_results.json");
        if (!File.Exists(benchFile)) return [];
        try
        {
            var json = File.ReadAllText(benchFile);
            var parsed = JsonSerializer.Deserialize<List<BenchmarkResult>>(json, JsonOptions);
            // Guard against null entries from a manually-edited JSON file.
            return parsed?.Where(r => r is not null).ToList() ?? [];
        }
        catch { return []; }
    }
}
