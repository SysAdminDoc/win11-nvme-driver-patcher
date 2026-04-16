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
        var diskSpdDir = Path.Combine(workingDir, "DiskSpd");
        var diskSpdExe = Path.Combine(diskSpdDir, "diskspd.exe");
        if (File.Exists(diskSpdExe)) return diskSpdExe;

        log?.Invoke("Downloading Microsoft DiskSpd benchmark tool...");
        try
        {
            Directory.CreateDirectory(diskSpdDir);
            var zipUrl = "https://github.com/microsoft/diskspd/releases/latest/download/DiskSpd.ZIP";
            var zipPath = Path.Combine(diskSpdDir, "DiskSpd.zip");

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var bytes = await client.GetByteArrayAsync(zipUrl);
            await File.WriteAllBytesAsync(zipPath, bytes);

            ZipFile.ExtractToDirectory(zipPath, diskSpdDir, overwriteFiles: true);
            File.Delete(zipPath);

            // Find amd64 version
            var found = Directory.GetFiles(diskSpdDir, "diskspd.exe", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.Contains("amd64", StringComparison.OrdinalIgnoreCase))
                ?? Directory.GetFiles(diskSpdDir, "diskspd.exe", SearchOption.AllDirectories).FirstOrDefault();

            if (found is not null)
            {
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

        // Find NVMe drive to benchmark
        string benchDir = workingDir;
        try
        {
            using var search = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT * FROM MSFT_Partition WHERE DriveLetter!=0");
            using var diskSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT * FROM MSFT_PhysicalDisk WHERE BusType=17");
            var nvmeDisk = diskSearch.Get().Cast<ManagementObject>().FirstOrDefault();
            if (nvmeDisk is not null)
            {
                // Get first partition with a drive letter on any NVMe disk
                foreach (ManagementObject part in search.Get())
                {
                    var letter = part["DriveLetter"];
                    if (letter is not null and not (char)'\0')
                    {
                        var nvmeRoot = $"{(char)letter}:\\";
                        var nvmeTempDir = Path.Combine(nvmeRoot, "NVMePatcher_Bench");
                        Directory.CreateDirectory(nvmeTempDir);
                        benchDir = nvmeTempDir;
                        log?.Invoke($"Benchmarking NVMe drive: {nvmeRoot}");
                        break;
                    }
                }
            }
        }
        catch { }

        var testFile = Path.Combine(benchDir, "diskspd_test.dat");
        var result = new BenchmarkResult { Label = label, Timestamp = DateTime.Now.ToString("o") };

        try
        {
            // Read test
            log?.Invoke("Running 4K random read benchmark (30s)...");
            progress?.Invoke(20, "Benchmarking reads...");
            var readOutput = await RunDiskSpd(exe, $"-c128M -d30 -w0 -t4 -o16 -b4K -r -Sh -L \"{testFile}\"");
            result.Read = ParseDiskSpdOutput(readOutput);

            // Write test
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
            try { File.Delete(testFile); } catch { }
            if (benchDir != workingDir)
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
        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return output;
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
        var benchFile = Path.Combine(workingDir, "benchmark_results.json");
        try
        {
            var existing = new List<BenchmarkResult>();
            if (File.Exists(benchFile))
            {
                var json = File.ReadAllText(benchFile);
                var parsed = JsonSerializer.Deserialize<List<BenchmarkResult>>(json, JsonOptions);
                if (parsed is not null) existing = parsed;
            }
            existing.Add(result);
            if (existing.Count > 10) existing = existing.Skip(existing.Count - 10).ToList();
            File.WriteAllText(benchFile, JsonSerializer.Serialize(existing, JsonOptions));
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
        var benchFile = Path.Combine(workingDir, "benchmark_results.json");
        if (!File.Exists(benchFile)) return [];
        try
        {
            var json = File.ReadAllText(benchFile);
            return JsonSerializer.Deserialize<List<BenchmarkResult>>(json, JsonOptions) ?? [];
        }
        catch { return []; }
    }
}
