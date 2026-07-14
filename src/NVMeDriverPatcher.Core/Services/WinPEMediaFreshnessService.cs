using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public sealed class WinPEFileFingerprint
{
    public bool Available { get; set; }
    public long? ByteLength { get; set; }
    public string? Sha256 { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class WinPEMediaBuildReport
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string RecoveryKitManifestSha256 { get; set; } = string.Empty;
    public string RollbackScriptSha256 { get; set; } = string.Empty;
    public WinPEFileFingerprint WinReImage { get; set; } = new();
    public List<BootStorageController> Controllers { get; set; } = [];
}

public sealed class WinPESourceSnapshot
{
    public string ToolVersion { get; set; } = string.Empty;
    public string RecoveryKitManifestSha256 { get; set; } = string.Empty;
    public string RollbackScriptSha256 { get; set; } = string.Empty;
    public WinPEFileFingerprint WinReImage { get; set; } = new();
    public bool ControllerProbeSucceeded { get; set; }
    public string? ControllerProbeError { get; set; }
    public List<BootStorageController> Controllers { get; set; } = [];
}

public enum WinPEMediaFreshness
{
    Missing,
    Fresh,
    Stale,
    Unknown
}

public sealed class WinPEMediaFreshnessReport
{
    public WinPEMediaFreshness State { get; set; }
    public string MediaRoot { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = [];
    public string Summary => State switch
    {
        WinPEMediaFreshness.Fresh => "WinPE recovery media matches the current app, rollback kit, WinRE image, and boot-storage controllers.",
        WinPEMediaFreshness.Missing => "WinPE recovery media is missing.",
        WinPEMediaFreshness.Stale => "WinPE recovery media is stale: " + string.Join("; ", Reasons),
        _ => "WinPE recovery media freshness is unknown: " + string.Join("; ", Reasons)
    };
}

public static class WinPEMediaFreshnessService
{
    public const string ControllerDirectoryName = "NVMe_Controller_Drivers";
    public const string ReportFileName = "CONTROLLER-REPORT.json";
    public const int CurrentSchemaVersion = 1;
    private const int MaxReportBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<WinPESourceSnapshot> CaptureCurrentSourcesAsync(
        string recoveryKitDirectory,
        BootStorageControllerInventory inventory,
        CancellationToken cancellationToken = default)
    {
        var winRePath = ResolveCurrentWinReImagePath();
        return await CaptureSourcesAsync(
            recoveryKitDirectory,
            inventory,
            winRePath,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<WinPESourceSnapshot> CaptureSourcesAsync(
        string recoveryKitDirectory,
        BootStorageControllerInventory inventory,
        string? winReImagePath,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(recoveryKitDirectory, GeneratedArtifactManifestService.ManifestFileName);
        var kitIntegrity = GeneratedArtifactManifestService.VerifyDirectory(recoveryKitDirectory);
        if (!kitIntegrity.Success)
            throw new InvalidDataException("Recovery Kit integrity failed: " +
                string.Join("; ", kitIntegrity.Issues.Select(i => $"{i.RelativePath}: {i.Detail}")));

        return new WinPESourceSnapshot
        {
            ToolVersion = AppConfig.AppVersion,
            RecoveryKitManifestSha256 = await ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false),
            RollbackScriptSha256 = ComputeCurrentRollbackScriptSha256(),
            WinReImage = await CaptureFileFingerprintAsync(winReImagePath, cancellationToken).ConfigureAwait(false),
            ControllerProbeSucceeded = inventory.ProbeSucceeded,
            ControllerProbeError = inventory.ProbeError,
            Controllers = inventory.Controllers.Select(CloneController).ToList()
        };
    }

    public static WinPEMediaBuildReport CreateBuildReport(
        WinPESourceSnapshot source,
        IEnumerable<BootStorageController> finalControllers) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        ToolVersion = source.ToolVersion,
        GeneratedAtUtc = DateTimeOffset.UtcNow,
        RecoveryKitManifestSha256 = source.RecoveryKitManifestSha256,
        RollbackScriptSha256 = source.RollbackScriptSha256,
        WinReImage = CloneFileFingerprint(source.WinReImage),
        Controllers = finalControllers.Select(CloneController).ToList()
    };

    public static string PublishBuildReport(string mediaRoot, WinPEMediaBuildReport report)
    {
        var reportDirectory = Path.Combine(mediaRoot, ControllerDirectoryName);
        Directory.CreateDirectory(reportDirectory);
        var finalPath = Path.Combine(reportDirectory, ReportFileName);
        var tempPath = finalPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, report, JsonOptions);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            _ = ReadBuildReportFile(tempPath);
            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static WinPEMediaBuildReport ReadBuildReport(string mediaRoot) =>
        ReadBuildReportFile(Path.Combine(mediaRoot, ControllerDirectoryName, ReportFileName));

    public static async Task<WinPEMediaFreshnessReport> EvaluateAsync(
        string mediaPath,
        string recoveryKitDirectory,
        CancellationToken cancellationToken = default)
    {
        var mediaRoot = ResolveMediaRoot(mediaPath);
        if (mediaRoot is null)
        {
            return new WinPEMediaFreshnessReport
            {
                State = WinPEMediaFreshness.Missing,
                MediaRoot = mediaPath ?? string.Empty
            };
        }

        var integrity = GeneratedArtifactManifestService.VerifyDirectory(mediaRoot);
        if (!integrity.Success)
        {
            return new WinPEMediaFreshnessReport
            {
                State = WinPEMediaFreshness.Stale,
                MediaRoot = mediaRoot,
                Reasons = integrity.Issues.Select(i => $"{i.RelativePath}: {i.Detail}").ToList()
            };
        }

        WinPEMediaBuildReport build;
        try { build = ReadBuildReport(mediaRoot); }
        catch (Exception ex)
        {
            return new WinPEMediaFreshnessReport
            {
                State = WinPEMediaFreshness.Stale,
                MediaRoot = mediaRoot,
                Reasons = [$"Controller report is invalid: {ex.Message}"]
            };
        }

        var inventory = BootStorageControllerService.Inventory();
        WinPESourceSnapshot current;
        try
        {
            current = await CaptureCurrentSourcesAsync(
                recoveryKitDirectory,
                inventory,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new WinPEMediaFreshnessReport
            {
                State = WinPEMediaFreshness.Unknown,
                MediaRoot = mediaRoot,
                Reasons = [$"Could not fingerprint current recovery sources: {ex.Message}"]
            };
        }
        var report = Compare(build, current);
        report.MediaRoot = mediaRoot;
        return report;
    }

    internal static WinPEMediaFreshnessReport Compare(
        WinPEMediaBuildReport build,
        WinPESourceSnapshot current)
    {
        var stale = new List<string>();
        var unknown = new List<string>();

        if (build.SchemaVersion != CurrentSchemaVersion)
            stale.Add($"controller report schema {build.SchemaVersion} is unsupported");
        if (!build.ToolVersion.Equals(current.ToolVersion, StringComparison.Ordinal))
            stale.Add($"app version changed from {build.ToolVersion} to {current.ToolVersion}");
        if (!build.RecoveryKitManifestSha256.Equals(current.RecoveryKitManifestSha256, StringComparison.OrdinalIgnoreCase))
            stale.Add("Recovery Kit manifest changed");
        if (!build.RollbackScriptSha256.Equals(current.RollbackScriptSha256, StringComparison.OrdinalIgnoreCase))
            stale.Add("rollback script changed");

        if (build.WinReImage.Available != current.WinReImage.Available)
            stale.Add("WinRE image availability changed");
        else if (build.WinReImage.Available &&
                 (!string.Equals(build.WinReImage.Sha256, current.WinReImage.Sha256, StringComparison.OrdinalIgnoreCase) ||
                  build.WinReImage.ByteLength != current.WinReImage.ByteLength))
            stale.Add("WinRE image content changed");
        else if (!build.WinReImage.Available)
            unknown.Add("WinRE image could not be fingerprinted at build time or now");

        if (!current.ControllerProbeSucceeded)
            unknown.Add("current boot-storage controller inventory is unavailable: " +
                        (current.ControllerProbeError ?? "unknown error"));
        else
        {
            var builtControllers = build.Controllers.ToDictionary(c => c.InstanceId, StringComparer.OrdinalIgnoreCase);
            var currentControllers = current.Controllers.ToDictionary(c => c.InstanceId, StringComparer.OrdinalIgnoreCase);
            foreach (var id in builtControllers.Keys.Except(currentControllers.Keys, StringComparer.OrdinalIgnoreCase))
                stale.Add($"controller removed: {builtControllers[id].FriendlyName}");
            foreach (var id in currentControllers.Keys.Except(builtControllers.Keys, StringComparer.OrdinalIgnoreCase))
                stale.Add($"controller added: {currentControllers[id].FriendlyName}");
            foreach (var id in builtControllers.Keys.Intersect(currentControllers.Keys, StringComparer.OrdinalIgnoreCase))
            {
                if (!builtControllers[id].SourceFingerprint.Equals(
                        currentControllers[id].SourceFingerprint, StringComparison.Ordinal))
                    stale.Add($"controller driver changed: {currentControllers[id].FriendlyName}");
            }
        }

        foreach (var controller in build.Controllers.Where(c =>
                     c.Coverage is WinPEControllerCoverage.Missing or WinPEControllerCoverage.PendingInjection))
            stale.Add($"controller was not covered: {controller.FriendlyName}");

        return new WinPEMediaFreshnessReport
        {
            State = stale.Count > 0
                ? WinPEMediaFreshness.Stale
                : unknown.Count > 0 ? WinPEMediaFreshness.Unknown : WinPEMediaFreshness.Fresh,
            Reasons = stale.Count > 0 ? stale : unknown
        };
    }

    internal static string? ResolveMediaRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        var full = Path.GetFullPath(path);
        if (File.Exists(Path.Combine(full, GeneratedArtifactManifestService.ManifestFileName))) return full;
        var child = Path.Combine(full, "media");
        return File.Exists(Path.Combine(child, GeneratedArtifactManifestService.ManifestFileName)) ? child : null;
    }

    internal static string? ResolveCurrentWinReImagePath()
    {
        try
        {
            var info = WinReBcdPrepService.Probe();
            var candidates = new List<string?> { info.ImagePath };
            if (!string.IsNullOrWhiteSpace(info.WinReLocation))
                candidates.Add(Path.Combine(info.WinReLocation.TrimEnd('\\'), "winre.wim"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "Recovery", "winre.wim"));
            return candidates.Where(p => !string.IsNullOrWhiteSpace(p)).FirstOrDefault(File.Exists);
        }
        catch { return null; }
    }

    internal static string ComputeCurrentRollbackScriptSha256() =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(RecoveryKitService.BuildBatContent())))
            .ToLowerInvariant();

    private static async Task<WinPEFileFingerprint> CaptureFileFingerprintAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new WinPEFileFingerprint { Available = false, Detail = "WinRE image path was not available." };
        try
        {
            var info = new FileInfo(path);
            return new WinPEFileFingerprint
            {
                Available = true,
                ByteLength = info.Length,
                Sha256 = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false),
                Detail = "winre.wim fingerprint captured."
            };
        }
        catch (Exception ex)
        {
            return new WinPEFileFingerprint
            {
                Available = false,
                Detail = $"WinRE image fingerprint failed: {ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static WinPEMediaBuildReport ReadBuildReportFile(string path)
    {
        var reportInfo = new FileInfo(path);
        if (!reportInfo.Exists)
            throw new FileNotFoundException("Controller report is missing.", path);
        if (reportInfo.Length <= 0 || reportInfo.Length > MaxReportBytes)
            throw new InvalidDataException(
                $"Controller report length {reportInfo.Length} is outside the supported range.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var report = JsonSerializer.Deserialize<WinPEMediaBuildReport>(stream, JsonOptions)
                     ?? throw new InvalidDataException("Controller report is empty.");
        if (report.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported controller report schema {report.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(report.ToolVersion) || report.Controllers is null)
            throw new InvalidDataException("Controller report is missing required fields.");
        if (report.GeneratedAtUtc == default ||
            !IsSha256(report.RecoveryKitManifestSha256) ||
            !IsSha256(report.RollbackScriptSha256))
            throw new InvalidDataException("Controller report has invalid source fingerprints.");
        if (report.WinReImage is null ||
            (report.WinReImage.Available &&
             (report.WinReImage.ByteLength is null or <= 0 || !IsSha256(report.WinReImage.Sha256))))
            throw new InvalidDataException("Controller report has an invalid WinRE fingerprint.");

        var instanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var controller in report.Controllers)
        {
            if (controller is null ||
                string.IsNullOrWhiteSpace(controller.InstanceId) ||
                string.IsNullOrWhiteSpace(controller.FriendlyName) ||
                string.IsNullOrWhiteSpace(controller.InfName) ||
                string.IsNullOrWhiteSpace(controller.DriverVersion) ||
                string.IsNullOrWhiteSpace(controller.ServiceName))
                throw new InvalidDataException("Controller report contains an incomplete controller record.");
            if (!instanceIds.Add(controller.InstanceId))
                throw new InvalidDataException(
                    $"Controller report contains duplicate instance ID '{controller.InstanceId}'.");
            if (controller.Coverage == WinPEControllerCoverage.Injected &&
                (string.IsNullOrWhiteSpace(controller.PackageRelativeInfPath) ||
                 !IsSha256(controller.PackageSha256)))
                throw new InvalidDataException(
                    $"Injected controller '{controller.FriendlyName}' is missing its retained package fingerprint.");
        }
        return report;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static BootStorageController CloneController(BootStorageController source) => new()
    {
        InstanceId = source.InstanceId,
        FriendlyName = source.FriendlyName,
        DeviceClass = source.DeviceClass,
        ServiceName = source.ServiceName,
        InfName = source.InfName,
        DriverProvider = source.DriverProvider,
        DriverVersion = source.DriverVersion,
        Coverage = source.Coverage,
        PackageRelativeInfPath = source.PackageRelativeInfPath,
        PackageSha256 = source.PackageSha256,
        Detail = source.Detail
    };

    private static WinPEFileFingerprint CloneFileFingerprint(WinPEFileFingerprint source) => new()
    {
        Available = source.Available,
        ByteLength = source.ByteLength,
        Sha256 = source.Sha256,
        Detail = source.Detail
    };
}
