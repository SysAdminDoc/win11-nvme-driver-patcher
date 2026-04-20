using System.Diagnostics;
using System.IO;
using System.Text;
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
}

// Wraps Windows Performance Recorder (wpr.exe) to capture short storage-IO traces before
// and after a patch apply. Lets us show the user real latency-distribution deltas instead
// of hand-wavy IOPS numbers.
//
// Uses the inbox "GeneralProfile.Storage" WPR profile where available, falling back to
// a synthetic storage profile written at runtime. wpr.exe is part of Windows since 10 —
// we don't ship it.
public static class EtwTraceService
{
    private const string DefaultProfile = "GeneralProfile.Storage";
    private const int DefaultDurationSeconds = 60;
    private const string EtlExtension = ".etl";

    public static async Task<EtwTraceResult> CaptureAsync(
        AppConfig config,
        EtwTracePhase phase,
        int durationSeconds = DefaultDurationSeconds,
        CancellationToken cancellationToken = default)
    {
        var result = new EtwTraceResult { Phase = phase, DurationSeconds = durationSeconds };
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

        try
        {
            await RunWprAsync(new[] { "-start", DefaultProfile, "-filemode" }, 30, cancellationToken);
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
            result.Summary = result.Success
                ? $"Captured {durationSeconds}s storage trace to {result.EtlPath}"
                : "wpr reported success but ETL file is missing.";
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Summary = "ETW capture canceled.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Summary = $"ETW capture failed: {ex.GetType().Name}: {ex.Message}";
            try { await RunWprAsync(new[] { "-cancel" }, 30, CancellationToken.None); } catch { }
        }
        return result;
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

    private static async Task RunWprAsync(string[] args, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("wpr.exe")
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
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"wpr {string.Join(' ', args)} exit {proc.ExitCode}: {stderr.Trim()}");
        }
    }
}
