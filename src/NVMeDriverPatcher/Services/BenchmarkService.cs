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
    private static readonly string[] AllowedAssetHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private const string DiskSpdArchiveUrl = "https://github.com/microsoft/diskspd/releases/latest/download/DiskSpd.ZIP";
    private const long MinArchiveBytes = 32 * 1024;
    private const long MaxArchiveBytes = 64 * 1024 * 1024;
    private const long MinExeBytes = 32 * 1024;
    private static readonly SemaphoreSlim _installLock = new(1, 1);

    public static async Task<string?> InstallDiskSpdAsync(
        string workingDir,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
            return null;

        // Respect the token even before we queue on the install lock — a user who clicked
        // Cancel before the semaphore came free shouldn't have to wait out an unrelated
        // concurrent download.
        if (!await _installLock.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false))
        {
            log?.Invoke("[ERROR] Another DiskSpd install is already in progress. Try again in a moment.");
            return null;
        }

        try
        {
            return await InstallDiskSpdInnerAsync(workingDir, log, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private static async Task<string?> InstallDiskSpdInnerAsync(
        string workingDir,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var diskSpdDir = Path.Combine(workingDir, "DiskSpd");
        var diskSpdExe = Path.Combine(diskSpdDir, "diskspd.exe");
        if (IsInstalled(diskSpdExe)) return diskSpdExe;

        var tempZip = Path.Combine(diskSpdDir, $"DiskSpd-{Guid.NewGuid():N}.zip");
        var stagingDir = Path.Combine(diskSpdDir, $"staging-{Guid.NewGuid():N}");
        log?.Invoke("Downloading Microsoft DiskSpd benchmark tool...");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(diskSpdDir);

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"NVMeDriverPatcher/{Models.AppConfig.AppVersion}");
            await DownloadTrustedAssetAsync(client, new Uri(DiskSpdArchiveUrl), tempZip, cancellationToken)
                .ConfigureAwait(false);

            var actualSize = new FileInfo(tempZip).Length;
            if (actualSize < MinArchiveBytes || actualSize > MaxArchiveBytes)
            {
                throw new InvalidOperationException(
                    $"Downloaded DiskSpd archive size {actualSize} bytes is outside the expected range ({MinArchiveBytes}..{MaxArchiveBytes}).");
            }

            Directory.CreateDirectory(stagingDir);
            var stagingPrefix = Path.GetFullPath(stagingDir);
            if (!stagingPrefix.EndsWith(Path.DirectorySeparatorChar))
                stagingPrefix += Path.DirectorySeparatorChar;

            using (var archive = ZipFile.OpenRead(tempZip))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var targetPath = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName));
                    if (!targetPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        log?.Invoke($"[WARNING] Skipping suspicious DiskSpd archive entry: {entry.FullName}");
                        continue;
                    }

                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir))
                        Directory.CreateDirectory(targetDir);
                    entry.ExtractToFile(targetPath, overwrite: true);
                }
            }

            // Prefer amd64 (we're a 64-bit process), but fall back to anything we can find.
            var allExes = Directory.GetFiles(stagingDir, "diskspd.exe", SearchOption.AllDirectories);
            var found = allExes.FirstOrDefault(f => f.Contains("amd64", StringComparison.OrdinalIgnoreCase))
                ?? allExes.FirstOrDefault();

            if (found is not null)
            {
                var stagedExe = new FileInfo(found);
                if (stagedExe.Length < MinExeBytes)
                    throw new InvalidOperationException("DiskSpd executable looks incomplete after extraction.");

                File.Copy(found, diskSpdExe, overwrite: true);
                log?.Invoke("DiskSpd downloaded successfully");
                return diskSpdExe;
            }

            log?.Invoke("[ERROR] DiskSpd exe not found in archive");
        }
        catch (OperationCanceledException)
        {
            log?.Invoke("[CANCELED] DiskSpd download canceled");
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Failed to download DiskSpd: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
        }
        return null;
    }

    internal static bool IsTrustedAssetUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedAssetHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    private static async Task DownloadTrustedAssetAsync(
        HttpClient client,
        Uri initialUri,
        string destination,
        CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (int redirectCount = 0; redirectCount < 6; redirectCount++)
        {
            if (!IsTrustedAssetUri(current))
                throw new InvalidOperationException($"Refusing untrusted DiskSpd download URL: {current}");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("DiskSpd download redirect did not include a Location header.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!IsTrustedAssetUri(next))
                    throw new InvalidOperationException($"Refusing DiskSpd redirect to untrusted host: {next.Host}");

                current = next;
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } expectedSize &&
                (expectedSize < MinArchiveBytes || expectedSize > MaxArchiveBytes))
            {
                throw new InvalidOperationException(
                    $"DiskSpd archive size {expectedSize} bytes is outside the expected range ({MinArchiveBytes}..{MaxArchiveBytes}).");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;

                total += read;
                if (total > MaxArchiveBytes)
                    throw new InvalidOperationException($"DiskSpd archive exceeded maximum size ({MaxArchiveBytes} bytes).");

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (total < MinArchiveBytes)
                throw new InvalidOperationException($"DiskSpd archive was too small ({total} bytes).");
            return;
        }

        throw new InvalidOperationException("DiskSpd download followed too many redirects.");
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 300 and <= 399;
    }

    public static async Task<BenchmarkResult?> RunBenchmarkAsync(
        string workingDir,
        string label,
        Action<string>? log = null,
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Bail BEFORE downloading DiskSpd if the user has already hit Cancel.
        string? exe;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            exe = await InstallDiskSpdAsync(workingDir, log, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log?.Invoke("[CANCELED] Benchmark canceled by user");
            ReportProgress(progress, 0, "");
            return null;
        }
        if (exe is null) return null;

        // Find a partition on an NVMe drive to benchmark
        string benchDir = workingDir;
        try
        {
            var nvmeDiskNumbers = DriveService.GetSystemDrives()
                .Where(d => d.IsNVMe)
                .Select(d => d.Number)
                .ToHashSet();

            if (nvmeDiskNumbers.Count > 0)
            {
                using var partSearch = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                    "SELECT DiskNumber, DriveLetter FROM MSFT_Partition WHERE DriveLetter!=0");
                using var partCollection = partSearch.Get();
                foreach (var raw in partCollection)
                {
                    if (raw is not ManagementObject part) continue;
                    using (part)
                    {
                        if (!int.TryParse(part["DiskNumber"]?.ToString(), out var diskNumber) ||
                            !nvmeDiskNumbers.Contains(diskNumber))
                        {
                            continue;
                        }

                        var letter = part["DriveLetter"];
                        if (letter is char ch && char.IsLetter(ch))
                        {
                            var nvmeRoot = $"{ch}:\\";
                            var nvmeTempDir = Path.Combine(nvmeRoot, "NVMePatcher_Bench");
                            try
                            {
                                Directory.CreateDirectory(nvmeTempDir);
                                benchDir = nvmeTempDir;
                                log?.Invoke($"Benchmarking NVMe drive: {nvmeRoot} (Disk {diskNumber})");
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
        BenchmarkResult? result = new() { Label = label, Timestamp = DateTime.Now.ToString("o") };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            log?.Invoke("Running 4K random read benchmark (30s)...");
            ReportProgress(progress, 20, "Benchmarking reads...");
            var readOutput = await RunDiskSpd(exe, CreateDiskSpdArguments(writePercent: 0, testFile), cancellationToken);
            result.Read = ParseDiskSpdOutput(readOutput);
            if (!HasAnyMetrics(result.Read))
                throw new InvalidOperationException("DiskSpd read benchmark completed, but no parseable metrics were returned.");

            cancellationToken.ThrowIfCancellationRequested();
            log?.Invoke("Running 4K random write benchmark (30s)...");
            ReportProgress(progress, 60, "Benchmarking writes...");
            var writeOutput = await RunDiskSpd(exe, CreateDiskSpdArguments(writePercent: 100, testFile), cancellationToken);
            result.Write = ParseDiskSpdOutput(writeOutput);
            if (!HasAnyMetrics(result.Write))
                throw new InvalidOperationException("DiskSpd write benchmark completed, but no parseable metrics were returned.");
        }
        catch (OperationCanceledException)
        {
            log?.Invoke("[CANCELED] Benchmark canceled by user");
            result = null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Benchmark error: {ex.Message}");
            result = null;
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
            ReportProgress(progress, 0, "");
        }

        return result;
    }

    internal static void ReportProgress(Action<int, string>? progress, int value, string text)
    {
        try
        {
            progress?.Invoke(value, text);
        }
        catch
        {
            // Progress callbacks are UI niceties; they must never change benchmark outcomes
            // or mask cleanup failures.
        }
    }

    internal static IReadOnlyList<string> CreateDiskSpdArguments(int writePercent, string testFile)
    {
        writePercent = Math.Clamp(writePercent, 0, 100);
        return
        [
            "-c128M",
            "-d30",
            $"-w{writePercent}",
            "-t4",
            "-o16",
            "-b4K",
            "-r",
            "-Sh",
            "-L",
            testFile
        ];
    }

    private static async Task<string> RunDiskSpd(
        string exePath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start DiskSpd process");

        // Read stdout AND stderr in parallel — diskspd will deadlock if stderr fills its pipe
        // buffer while we're only reading stdout.
        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();
        // Generous per-run ceiling: DiskSpd normally finishes in ~30s, but give it extra headroom
        // for heavily loaded or throttled systems before we treat the process as wedged.
        // Linked with the external cancellation token so a user-triggered Cancel kills the
        // process cleanly. When the caller's token fires, CancellationRequested propagates
        // through the linked source and WaitForExitAsync throws OperationCanceledException.
        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            // Let the caller distinguish user-cancel from timeout so the UI message is accurate.
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new InvalidOperationException("DiskSpd timed out after 120 seconds.");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var details = FirstNonEmpty(error, output, "DiskSpd exited without any diagnostic output.");
            throw new InvalidOperationException($"DiskSpd exited with code {proc.ExitCode}: {details}");
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            var details = FirstNonEmpty(error, "DiskSpd produced no output.");
            throw new InvalidOperationException(details);
        }

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
                TryPopulateMetrics(metrics, parts, culture, 2, 3, 4);

                // DiskSpd table layouts can shift by one column depending on the report flavor.
                // If the primary shape produced nonsense (e.g. 0 throughput with non-zero IOPS),
                // retry one column to the right before giving up and surfacing an empty result.
                if ((metrics.ThroughputMBs <= 0 || metrics.IOPS <= 0) && parts.Length >= 6)
                {
                    var shifted = new BenchmarkMetrics();
                    TryPopulateMetrics(shifted, parts, culture, 3, 4, 5);
                    if (shifted.ThroughputMBs > 0 && shifted.IOPS > 0)
                        metrics = shifted;
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
                    existing = SanitizeBenchmarkHistory(parsed);
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
            // Guard against null entries/properties from a manually-edited JSON file.
            return SanitizeBenchmarkHistory(parsed);
        }
        catch { return []; }
    }

    internal static List<BenchmarkResult> SanitizeBenchmarkHistory(IEnumerable<BenchmarkResult?>? parsed)
    {
        if (parsed is null) return [];

        return parsed
            .Where(result => result is not null)
            .Select(result =>
            {
                result!.Label = string.IsNullOrWhiteSpace(result.Label) ? "benchmark" : result.Label;
                result.Timestamp ??= string.Empty;
                result.Read ??= new BenchmarkMetrics();
                result.Write ??= new BenchmarkMetrics();
                return result;
            })
            .ToList();
    }

    private static bool IsInstalled(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return false;
            return new FileInfo(exePath).Length >= MinExeBytes;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasAnyMetrics(BenchmarkMetrics metrics) =>
        metrics.IOPS > 0 || metrics.ThroughputMBs > 0 || metrics.AvgLatencyMs > 0;

    private enum DiskSpdMetricKind
    {
        Throughput,
        Iops,
        Latency
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static void TryPopulateMetrics(
        BenchmarkMetrics metrics,
        string[] parts,
        CultureInfo culture,
        int throughputIndex,
        int iopsIndex,
        int latencyIndex)
    {
        if (TryParseNumericPart(parts, throughputIndex, culture, DiskSpdMetricKind.Throughput, out var throughput))
            metrics.ThroughputMBs = Math.Round(throughput, 2);
        if (TryParseNumericPart(parts, iopsIndex, culture, DiskSpdMetricKind.Iops, out var iops))
            metrics.IOPS = Math.Round(iops, 0);
        if (TryParseNumericPart(parts, latencyIndex, culture, DiskSpdMetricKind.Latency, out var latency))
            metrics.AvgLatencyMs = Math.Round(latency, 3);
    }

    private static bool TryParseNumericPart(
        string[] parts,
        int index,
        CultureInfo culture,
        DiskSpdMetricKind kind,
        out double value)
    {
        value = 0;
        if (index < 0 || index >= parts.Length) return false;

        var numeric = Regex.Replace(parts[index], @"[^\d.,+-]", "");
        if (string.IsNullOrWhiteSpace(numeric)) return false;

        var normalized = NormalizeNumericToken(numeric, kind);
        return double.TryParse(normalized, NumberStyles.Float, culture, out value);
    }

    private static string NormalizeNumericToken(string raw, DiskSpdMetricKind kind)
    {
        int lastDot = raw.LastIndexOf('.');
        int lastComma = raw.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            char decimalSeparator = lastDot > lastComma ? '.' : ',';
            char groupSeparator = decimalSeparator == '.' ? ',' : '.';
            return raw
                .Replace(groupSeparator.ToString(), string.Empty)
                .Replace(decimalSeparator, '.');
        }

        if (lastComma >= 0)
        {
            bool looksLikeThousandsGroup =
                LooksLikeGroupedInteger(raw, ',') &&
                kind != DiskSpdMetricKind.Latency;
            return looksLikeThousandsGroup
                ? raw.Replace(",", string.Empty)
                : raw.Replace(',', '.');
        }

        if (lastDot >= 0 && kind != DiskSpdMetricKind.Latency && LooksLikeGroupedInteger(raw, '.'))
            return raw.Replace(".", string.Empty);

        return raw;
    }

    private static bool LooksLikeGroupedInteger(string raw, char separator)
    {
        var pattern = separator == ','
            ? @"^[+-]?\d{1,3}(,\d{3})+$"
            : @"^[+-]?\d{1,3}(\.\d{3})+$";
        return Regex.IsMatch(raw, pattern);
    }
}
