using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum EtwTracePhase
{
    PrePatch,
    PostPatch
}

public class EtwTraceResult
{
    public bool Success { get; set; }
    public string EtlPath { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public EtwTracePhase Phase { get; set; }
    public int DurationSeconds { get; set; }
    public bool NativeStackProbeSucceeded { get; set; }
    public bool NativeStackBound { get; set; }
    public bool NvmeDiskProviderRequested { get; set; }
    public bool NvmeDiskProviderPresent { get; set; }
    public string NvmeDiskProviderStatus { get; set; } = string.Empty;
    public string EvidencePath { get; set; } = string.Empty;
}

public sealed class EtwTraceProviderEvidence
{
    public EtwTracePhase Phase { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public string TraceFileName { get; set; } = string.Empty;
    public bool NativeStackProbeSucceeded { get; set; }
    public bool NativeStackBound { get; set; }
    public string ProviderName { get; set; } = EtwTraceService.NvmeDiskProviderName;
    public string ProviderGuid { get; set; } = EtwTraceService.NvmeDiskProviderGuid;
    public bool ProviderRequested { get; set; }
    public bool ProviderPresent { get; set; }
    public string ProviderStatus { get; set; } = string.Empty;
}

// Wraps Windows Performance Recorder (wpr.exe) to capture short storage-IO traces before
// and after a patch apply. Lets us show the user real latency-distribution deltas instead
// of hand-wavy IOPS numbers.
//
// Uses the inbox "GeneralProfile.Storage" WPR profile where available. Post-patch captures
// add a small custom profile for Microsoft's own nvmedisk ETW provider when the native stack
// is actually bound. wpr.exe is part of Windows since 10 — we don't ship it.
public static class EtwTraceService
{
    private const string DefaultProfile = "GeneralProfile.Storage";
    private const int DefaultDurationSeconds = 60;
    private const string EtlExtension = ".etl";
    private const string ProviderProfileName = "NvmeDiskWatch";
    internal const string EvidenceFileSuffix = ".provider.json";

    public const string NvmeDiskProviderName = "Microsoft-Windows-NvmeDisk";
    public const string NvmeDiskProviderGuid = "{9799276c-fb04-47e8-845e-36946045c218}";

    public static async Task<EtwTraceResult> CaptureAsync(
        AppConfig config,
        EtwTracePhase phase,
        int durationSeconds = DefaultDurationSeconds,
        CancellationToken cancellationToken = default)
    {
        var result = new EtwTraceResult { Phase = phase, DurationSeconds = durationSeconds };
        if (phase == EtwTracePhase.PostPatch)
        {
            var native = ProbeNativeStack();
            result.NativeStackProbeSucceeded = native.Succeeded;
            result.NativeStackBound = native.IsBound;
            result.NvmeDiskProviderRequested = ShouldRequestNvmeDiskProvider(phase, native.Succeeded && native.IsBound);
            result.NvmeDiskProviderStatus = native.Succeeded
                ? native.IsBound
                    ? "requested; WPR session status is pending"
                    : "not requested because the native NVMe stack is not bound"
                : "not requested because native NVMe stack status is unavailable";
        }
        else
        {
            result.NativeStackProbeSucceeded = true;
            result.NvmeDiskProviderStatus = "not applicable to the pre-patch capture";
        }

        var dir = Path.Combine(
            string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir,
            "etl");
        try { Directory.CreateDirectory(dir); } catch { }

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var filename = phase == EtwTracePhase.PrePatch ? $"pre_{stamp}{EtlExtension}" : $"post_{stamp}{EtlExtension}";
        result.EtlPath = Path.Combine(dir, filename);

        if (!IsWprAvailable())
        {
            result.Success = false;
            result.Summary = "wpr.exe not available on this SKU — skipping ETW capture.";
            return result;
        }

        string? profilePath = null;
        try
        {
            // Clear any stale kernel session left by a prior capture that was killed between -start
            // and -stop (crash/taskkill/reset). Without this, -start fails with "already recording"
            // and the session lingers until reboot. Best-effort — a no-op when nothing is recording.
            try { await RunWprAsync(new[] { "-cancel" }, 15, CancellationToken.None); } catch { }

            var startArguments = new List<string> { "-start", DefaultProfile };
            if (result.NvmeDiskProviderRequested)
            {
                profilePath = Path.Combine(Path.GetTempPath(), $"NVMePatcher_{Guid.NewGuid():N}.wprp");
                File.WriteAllText(profilePath, BuildNvmeDiskProviderProfile(), new UTF8Encoding(false));
                startArguments.Add("-start");
                startArguments.Add($"{profilePath}!{ProviderProfileName}.Verbose.File");
            }
            startArguments.Add("-filemode");
            await RunWprAsync(startArguments.ToArray(), 30, cancellationToken);

            if (result.NvmeDiskProviderRequested)
            {
                try
                {
                    var status = await RunWprAsync(new[] { "-status", "collectors" }, 30, cancellationToken);
                    result.NvmeDiskProviderPresent = ContainsNvmeDiskProvider(status.StdOut, status.StdErr);
                    result.NvmeDiskProviderStatus = result.NvmeDiskProviderPresent
                        ? "present in the active WPR session"
                        : "requested but not listed by WPR session status";
                }
                catch (Exception ex)
                {
                    result.NvmeDiskProviderStatus = $"requested; WPR session status unavailable ({ex.GetType().Name})";
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, durationSeconds)), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { await RunWprAsync(new[] { "-cancel" }, 30, CancellationToken.None); } catch { }
                throw;
            }
            await RunWprAsync(new[] { "-stop", result.EtlPath }, 90, cancellationToken);
            result.Success = File.Exists(result.EtlPath);
            if (result.Success)
                PersistProviderEvidence(result);
            result.Summary = result.Success
                ? $"Captured {durationSeconds}s storage trace to {result.EtlPath}. {FormatProviderSummary(result)}"
                : "wpr reported success but ETL file is missing.";
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Summary = "ETW capture canceled.";
            try { await RunWprAsync(new[] { "-cancel" }, 30, CancellationToken.None); } catch { }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Summary = $"ETW capture failed: {ex.GetType().Name}: {ex.Message}";
            try { await RunWprAsync(new[] { "-cancel" }, 30, CancellationToken.None); } catch { }
        }
        finally
        {
            if (profilePath is not null)
            {
                try { File.Delete(profilePath); } catch { }
            }
        }
        return result;
    }

    internal static string BuildNvmeDiskProviderProfile() => """
        <?xml version="1.0" encoding="utf-8"?>
        <WindowsPerformanceRecorder Version="1.0">
          <Profiles>
            <EventCollector Id="NvmeDiskCollector" Name="NVMe Driver Patcher ETW Collector">
              <BufferSize Value="64" />
              <Buffers Value="64" />
            </EventCollector>
            <EventProvider Id="NvmeDiskProvider"
                           Name="9799276c-fb04-47e8-845e-36946045c218"
                           Level="5"
                           Strict="false">
              <Keywords>
                <Keyword Value="0x0" />
              </Keywords>
            </EventProvider>
            <Profile Id="NvmeDiskWatch.Verbose.File"
                     Name="NvmeDiskWatch"
                     DetailLevel="Verbose"
                     LoggingMode="File"
                     Description="Microsoft-Windows-NvmeDisk first-party NVMe stack evidence">
              <Collectors>
                <EventCollectorId Value="NvmeDiskCollector">
                  <EventProviders>
                    <EventProviderId Value="NvmeDiskProvider" />
                  </EventProviders>
                </EventCollectorId>
              </Collectors>
            </Profile>
            <Profile Id="NvmeDiskWatch.Verbose.Memory"
                     Name="NvmeDiskWatch"
                     DetailLevel="Verbose"
                     LoggingMode="Memory"
                     Description="Microsoft-Windows-NvmeDisk first-party NVMe stack evidence">
              <Collectors>
                <EventCollectorId Value="NvmeDiskCollector">
                  <EventProviders>
                    <EventProviderId Value="NvmeDiskProvider" />
                  </EventProviders>
                </EventCollectorId>
              </Collectors>
            </Profile>
          </Profiles>
        </WindowsPerformanceRecorder>
        """;

    internal static bool ContainsNvmeDiskProvider(string stdout, string stderr)
    {
        var output = $"{stdout}\n{stderr}";
        return output.Contains(NvmeDiskProviderName, StringComparison.OrdinalIgnoreCase) ||
               output.Contains(NvmeDiskProviderGuid, StringComparison.OrdinalIgnoreCase) ||
               output.Contains(NvmeDiskProviderGuid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldRequestNvmeDiskProvider(EtwTracePhase phase, bool nativeStackBound) =>
        phase == EtwTracePhase.PostPatch && nativeStackBound;

    internal static EtwTraceProviderEvidence? GetLatestProviderEvidence(string workingDir)
    {
        try
        {
            var dir = Path.Combine(workingDir, "etl");
            if (!Directory.Exists(dir)) return null;

            foreach (var path in Directory.GetFiles(dir, $"*{EvidenceFileSuffix}", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var evidence = JsonSerializer.Deserialize<EtwTraceProviderEvidence>(File.ReadAllText(path));
                    if (evidence is not null) return evidence;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Compare two ETL captures by their file metadata (size, sample density). A real WPA
    /// analysis requires shipping the Windows Performance Analyzer — out of scope here.
    /// This gives the user a first-order "did we capture something reasonable on both sides"
    /// signal alongside the existing DiskSpd before/after pair.
    /// </summary>
    public static string Compare(string prePath, string postPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ETW storage trace comparison");
        sb.AppendLine();
        foreach (var (label, path) in new[] { ("Pre-patch", prePath), ("Post-patch", postPath) })
        {
            try
            {
                if (!File.Exists(path))
                {
                    sb.AppendLine($"- {label}: (missing)");
                    continue;
                }
                var fi = new FileInfo(path);
                sb.AppendLine($"- {label}: {fi.Length / 1024.0 / 1024.0:F1} MB ({fi.LastWriteTimeUtc:u})");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"- {label}: probe failed ({ex.GetType().Name})");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Open the ETL files in Windows Performance Analyzer (wpa.exe, installable via the Windows ADK)");
        sb.AppendLine("and compare the 'Storage' graph between the two captures.");
        return sb.ToString();
    }

    internal static bool IsWprAvailable()
    {
        try
        {
            var sysDir = Environment.SystemDirectory;
            if (string.IsNullOrEmpty(sysDir)) return false;
            return File.Exists(Path.Combine(sysDir, "wpr.exe"));
        }
        catch { return false; }
    }

    private static (bool Succeeded, bool IsBound, string Detail) ProbeNativeStack()
    {
        try
        {
            var status = DriveService.TestNativeNVMeActive();
            var unavailable = status.Details.StartsWith("Unable to determine", StringComparison.OrdinalIgnoreCase);
            return (!unavailable, status.IsActive, status.Details);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    private static string FormatProviderSummary(EtwTraceResult result)
    {
        return $"{NvmeDiskProviderName} provider: {result.NvmeDiskProviderStatus}.";
    }

    private static void PersistProviderEvidence(EtwTraceResult result)
    {
        var evidence = new EtwTraceProviderEvidence
        {
            Phase = result.Phase,
            CapturedAtUtc = DateTime.UtcNow,
            TraceFileName = Path.GetFileName(result.EtlPath),
            NativeStackProbeSucceeded = result.NativeStackProbeSucceeded,
            NativeStackBound = result.NativeStackBound,
            ProviderRequested = result.NvmeDiskProviderRequested,
            ProviderPresent = result.NvmeDiskProviderPresent,
            ProviderStatus = result.NvmeDiskProviderStatus
        };
        var path = result.EtlPath + EvidenceFileSuffix;
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tempPath, path, overwrite: true);
        result.EvidencePath = path;
    }

    private sealed record WprCommandResult(string StdOut, string StdErr, int ExitCode);

    private static async Task<WprCommandResult> RunWprAsync(string[] args, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(SystemToolPathService.Resolve("wpr.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("wpr.exe did not start.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

        // Drain stdout/stderr BEFORE WaitForExitAsync. wpr -stop and wpr -start both emit
        // progress text that can fill the ~4 KB pipe buffer on a long capture, at which
        // point the child blocks on write and WaitForExitAsync hangs until the timeout.
        // Reading concurrently keeps both pipes draining. Use the linked token so a cancel
        // also tears down the reads.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            // Let the reader tasks observe the canceled token so their resources are released
            // rather than dangling. Swallow their exceptions — we're already unwinding.
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
            throw;
        }

        string stdout = string.Empty, stderr = string.Empty;
        try { stdout = await stdoutTask; } catch { }
        try { stderr = await stderrTask; } catch { }

        if (proc.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            throw new InvalidOperationException($"wpr {string.Join(' ', args)} exit {proc.ExitCode}: {detail}");
        }

        return new WprCommandResult(stdout, stderr, proc.ExitCode);
    }
}
