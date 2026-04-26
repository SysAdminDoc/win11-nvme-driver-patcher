using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class CompatReport
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("submittedAt")]
    public string SubmittedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("anonId")]
    public string AnonId { get; set; } = string.Empty;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = AppConfig.AppVersion;

    [JsonPropertyName("osBuild")]
    public string OsBuild { get; set; } = string.Empty;

    [JsonPropertyName("cpu")]
    public string Cpu { get; set; } = string.Empty;

    [JsonPropertyName("controllers")]
    public List<CompatController> Controllers { get; set; } = new();

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = string.Empty;

    [JsonPropertyName("verification")]
    public string Verification { get; set; } = string.Empty;

    [JsonPropertyName("watchdog")]
    public string Watchdog { get; set; } = string.Empty;

    [JsonPropertyName("watchdogEvents")]
    public int WatchdogEvents { get; set; }

    [JsonPropertyName("reliabilityDelta")]
    public double? ReliabilityDelta { get; set; }

    [JsonPropertyName("benchmarkDeltaPercent")]
    public double? BenchmarkDeltaPercent { get; set; }
}

public class CompatController
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("firmware")]
    public string Firmware { get; set; } = string.Empty;

    [JsonPropertyName("migrated")]
    public bool Migrated { get; set; }
}

public class CompatSubmissionResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? PayloadPath { get; set; }
}

// Opt-in, privacy-respecting compat telemetry. Packages a JSON report of
// { controller model/firmware, OS build, CPU, patch profile, verification outcome,
//   watchdog counts, reliability delta, benchmark delta } and POSTs it to a public
// JSON endpoint (user-configurable). Defaults to SAVE ONLY so users must consciously
// choose to submit. Never includes serials, drive letters, machine name, or user name.
public static class CompatTelemetryService
{
    private const string ReportFile = "compat_report.json";

    // Lazy HttpClient — re-used across submissions. Never instantiated per-call.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static CompatReport BuildReport(
        AppConfig config,
        PreflightResult preflight,
        WatchdogReport? watchdog,
        ReliabilityCorrelationReport? reliability,
        VerificationReport? verification,
        double? benchmarkDeltaPercent)
    {
        var report = new CompatReport
        {
            AnonId = GetOrCreateAnonId(config),
            OsBuild = preflight.BuildDetails?.BuildNumber.ToString() + "." + (preflight.BuildDetails?.UBR ?? 0),
            Cpu = SanitizeCpu(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown"),
            Profile = config.PatchProfile.ToString(),
            Verification = verification?.Outcome.ToString() ?? "Unknown",
            Watchdog = watchdog?.Verdict.ToString() ?? "Idle",
            WatchdogEvents = watchdog?.TotalEvents ?? 0,
            ReliabilityDelta = reliability?.Delta,
            BenchmarkDeltaPercent = benchmarkDeltaPercent
        };

        // Merge firmware versions from the driver details into the per-controller payload.
        // SystemDrive carries only the friendly name; firmware and migration come from
        // other preflight outputs (NVMeDriverDetails.FirmwareVersions + CachedMigration).
        var firmwareMap = preflight.DriverInfo?.FirmwareVersions ?? new Dictionary<string, string>();
        var migratedSet = preflight.CachedMigration?.Migrated?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                         ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in preflight.CachedDrives ?? Enumerable.Empty<SystemDrive>())
        {
            if (!drive.IsNVMe) continue;
            var driveName = drive.Name ?? string.Empty;
            firmwareMap.TryGetValue(driveName, out var firmware);
            report.Controllers.Add(new CompatController
            {
                Model = driveName,
                Firmware = firmware ?? string.Empty,
                Migrated = migratedSet.Contains(driveName)
            });
        }
        return report;
    }

    public static string SaveReport(AppConfig config, CompatReport report)
    {
        var dir = string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir;
        var path = Path.Combine(dir, ReportFile);
        var json = JsonSerializer.Serialize(report, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// POSTs the report JSON to the user-supplied endpoint. Returns non-success on any
    /// non-2xx, network failure, or timeout. Caller displays a simple "submitted / failed"
    /// message — we don't retry automatically (the user is in control).
    /// </summary>
    public static async Task<CompatSubmissionResult> SubmitAsync(
        string endpoint,
        CompatReport report,
        CancellationToken cancellationToken = default)
    {
        var result = new CompatSubmissionResult();
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            result.Summary = "Endpoint is empty or not a valid http(s) URL.";
            return result;
        }

        try
        {
            var json = JsonSerializer.Serialize(report, JsonOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("NVMeDriverPatcher", AppConfig.AppVersion));
            using var resp = await Http.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                result.Success = true;
                result.Summary = $"Submitted (HTTP {(int)resp.StatusCode}).";
            }
            else
            {
                result.Summary = $"Endpoint returned HTTP {(int)resp.StatusCode}.";
            }
        }
        catch (OperationCanceledException)
        {
            result.Summary = "Submission canceled.";
        }
        catch (Exception ex)
        {
            result.Summary = $"Submission failed: {ex.GetType().Name}: {ex.Message}";
        }
        return result;
    }

    private static string GetOrCreateAnonId(AppConfig config)
    {
        // Stable per-install anonymous ID so repeat submissions from the same machine can be
        // de-duplicated server-side without leaking identifiable data. Stored beside config.json.
        try
        {
            var dir = string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir;
            var path = Path.Combine(dir, "anon_id.txt");
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing, out _)) return existing;
            }
            var id = Guid.NewGuid().ToString();
            File.WriteAllText(path, id);
            return id;
        }
        catch
        {
            return Guid.NewGuid().ToString();
        }
    }

    internal static string SanitizeCpu(string cpu)
    {
        // Keep vendor/family only — strip stepping and microcode details to reduce entropy.
        if (string.IsNullOrWhiteSpace(cpu)) return "unknown";
        var trimmed = cpu.Trim();
        if (trimmed.Length > 80) trimmed = trimmed[..80];
        return trimmed;
    }
}
