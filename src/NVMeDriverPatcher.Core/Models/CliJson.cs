using System.Text.Json;
using System.Text.Json.Serialization;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Models;

// Versioned JSON output contract for fleet automation. The PowerShell module (and any other
// consumer) reads these stable, camelCase shapes instead of regex-parsing human prose. Bump
// SchemaVersion only on a breaking field change; additive fields keep the same version.
public static class CliJson
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(string command, object data) =>
        JsonSerializer.Serialize(new CliEnvelope { Command = command, Data = data }, Options);

    public static StatusJson BuildStatus(
        PatchStatus status, NativeNVMeStatus? native, EnablementSource source, WindowsBuildRule? rule) => new()
    {
        Status = status.Applied ? "applied" : status.Partial ? "partial" : "not-applied",
        Applied = status.Applied,
        Partial = status.Partial,
        ComponentsApplied = status.Count,
        ComponentsTotal = status.Total,
        AppliedKeys = status.Keys,
        NativeActive = native?.IsActive ?? false,
        ActiveDriver = native?.ActiveDriver,
        EnablementSource = source.ToString(),
        BuildRuleId = rule?.Id,
        BuildRuleSource = rule?.SourceUrl,
        BuildRuleConfidence = rule?.Confidence,
        BuildRuleLastReviewed = rule?.LastReviewed,
    };

    public static WatchdogJson BuildWatchdog(WatchdogReport report) => new()
    {
        Verdict = report.Verdict.ToString(),
        TotalEvents = report.TotalEvents,
        BugChecks = report.BugChecks,
        Summary = report.Summary,
        EventCounts = report.Counts.Select(c => new WatchdogEventCountJson
        {
            Source = c.Source,
            Id = c.Id,
            Description = c.Description,
            Count = c.Count,
        }).ToList(),
    };

    public static RecoveryProofJson BuildRecoveryProof(RecoveryProofReport report) => new()
    {
        AllPassed = report.AllPassed,
        PassedCount = report.PassedCount,
        TotalCount = report.TotalCount,
        Items = report.Items.Select(i => new RecoveryProofItemJson
        {
            Label = i.Label,
            Passed = i.Passed,
            Detail = i.Detail,
        }).ToList(),
    };

    public static BypassIoJson BuildBypassIo(BypassIOResult result) => new()
    {
        Supported = result.Supported,
        StorageType = result.StorageType,
        DriverCompat = result.DriverCompat,
        BlockedBy = result.BlockedBy,
        Warning = result.Warning,
        GamingImpact = result.GamingImpact,
    };

    public static ControllersJson BuildControllers(PerControllerAuditReport report) => new()
    {
        NativeCount = report.NativeCount,
        LegacyCount = report.LegacyCount,
        Controllers = report.Controllers.Select(c => new ControllerJson
        {
            IsNative = c.IsNative,
            FriendlyName = c.FriendlyName,
            BoundDriver = c.BoundDriver,
            InstanceId = c.InstanceId,
            InfName = c.InfName,
            DriverProvider = c.DriverProvider,
            DeviceClass = c.DeviceClass,
            HardwareId = c.HardwareId,
            CompatibleId = c.CompatibleId,
        }).ToList(),
    };

    public static ReliabilityJson BuildReliability(ReliabilityCorrelationReport report) => new()
    {
        DataAvailable = report.DataAvailable,
        PrePatchAverage = report.PrePatchAverage,
        PostPatchAverage = report.PostPatchAverage,
        Delta = report.Delta,
        Summary = report.Summary,
        Series = report.Series.Select(p => new ReliabilityPointJson
        {
            Timestamp = p.Timestamp.ToString("o"),
            Index = p.Index,
        }).ToList(),
    };

    public static MinidumpJson BuildMinidump(MinidumpTriageReport report) => new()
    {
        TotalFound = report.TotalFound,
        NewerThanPatch = report.NewerThanPatch,
        NVMeRelated = report.NVMeRelated,
        ScanCompleted = report.ScanCompleted,
        Summary = report.Summary,
        Dumps = report.Dumps.Select(d => new MinidumpEntryJson
        {
            FilePath = d.FilePath,
            SizeBytes = d.SizeBytes,
            CreatedUtc = d.CreatedUtc.ToString("o"),
            MentionsNVMeStack = d.MentionsNVMeStack,
            MatchedModules = d.MatchedModules,
        }).ToList(),
    };

    public static FirmwareCompatJson BuildFirmwareCompat(FirmwareCompatDatabase db) => new()
    {
        SchemaVersion = db.SchemaVersion,
        Updated = db.Updated,
        EntryCount = db.Entries.Count,
        Entries = db.Entries.Select(e => new FirmwareCompatEntryJson
        {
            Controller = e.Controller,
            Firmware = e.Firmware,
            Level = e.Level.ToString(),
            Note = e.Note,
            PowerLossRisk = e.PowerLossRisk,
            Confidence = e.Confidence ?? string.Empty,
        }).ToList(),
    };

    public static FeatureStoreJson BuildFeatureStore(
        bool hasFallbackEvidence,
        IReadOnlyList<FeatureConfigState> configurations) => new()
    {
        HasFallbackEvidence = hasFallbackEvidence,
        Configurations = configurations.Select(s => new FeatureStoreConfigJson
        {
            FeatureId = s.FeatureId,
            Store = s.Store,
            Found = s.Found,
            EnabledState = s.EnabledState,
            Priority = s.Priority,
            IsEnabled = s.IsEnabled,
        }).ToList(),
    };
}

public sealed class CliEnvelope
{
    public int SchemaVersion { get; set; } = CliJson.SchemaVersion;
    public string Command { get; set; } = string.Empty;
    public object? Data { get; set; }
}

public sealed class StatusJson
{
    public string Status { get; set; } = string.Empty;
    public bool Applied { get; set; }
    public bool Partial { get; set; }
    public int ComponentsApplied { get; set; }
    public int ComponentsTotal { get; set; }
    public List<string> AppliedKeys { get; set; } = new();
    public bool NativeActive { get; set; }
    public string? ActiveDriver { get; set; }
    public string EnablementSource { get; set; } = string.Empty;
    public string? BuildRuleId { get; set; }
    public string? BuildRuleSource { get; set; }
    public string? BuildRuleConfidence { get; set; }
    public string? BuildRuleLastReviewed { get; set; }
}

public sealed class WatchdogJson
{
    public string Verdict { get; set; } = string.Empty;
    public int TotalEvents { get; set; }
    public int BugChecks { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<WatchdogEventCountJson> EventCounts { get; set; } = new();
}

public sealed class WatchdogEventCountJson
{
    public string Source { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class ControllersJson
{
    public int NativeCount { get; set; }
    public int LegacyCount { get; set; }
    public List<ControllerJson> Controllers { get; set; } = new();
}

public sealed class RecoveryProofJson
{
    public bool AllPassed { get; set; }
    public int PassedCount { get; set; }
    public int TotalCount { get; set; }
    public List<RecoveryProofItemJson> Items { get; set; } = new();
}

public sealed class RecoveryProofItemJson
{
    public string Label { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class BypassIoJson
{
    public bool Supported { get; set; }
    public string StorageType { get; set; } = string.Empty;
    public string DriverCompat { get; set; } = string.Empty;
    public string BlockedBy { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
    public string GamingImpact { get; set; } = string.Empty;
}

public sealed class ControllerJson
{
    public bool IsNative { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string BoundDriver { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string InfName { get; set; } = string.Empty;
    public string DriverProvider { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;
    public string CompatibleId { get; set; } = string.Empty;
}

public sealed class ReliabilityJson
{
    public bool DataAvailable { get; set; }
    public double? PrePatchAverage { get; set; }
    public double? PostPatchAverage { get; set; }
    public double? Delta { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<ReliabilityPointJson> Series { get; set; } = new();
}

public sealed class ReliabilityPointJson
{
    public string Timestamp { get; set; } = string.Empty;
    public double Index { get; set; }
}

public sealed class MinidumpJson
{
    public int TotalFound { get; set; }
    public int NewerThanPatch { get; set; }
    public int NVMeRelated { get; set; }
    public bool ScanCompleted { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<MinidumpEntryJson> Dumps { get; set; } = new();
}

public sealed class MinidumpEntryJson
{
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string CreatedUtc { get; set; } = string.Empty;
    public bool MentionsNVMeStack { get; set; }
    public List<string> MatchedModules { get; set; } = new();
}

public sealed class FirmwareCompatJson
{
    public int SchemaVersion { get; set; }
    public string Updated { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public List<FirmwareCompatEntryJson> Entries { get; set; } = new();
}

public sealed class FirmwareCompatEntryJson
{
    public string Controller { get; set; } = string.Empty;
    public string Firmware { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public bool PowerLossRisk { get; set; }
    public string Confidence { get; set; } = string.Empty;
}

public sealed class FeatureStoreJson
{
    public bool HasFallbackEvidence { get; set; }
    public List<FeatureStoreConfigJson> Configurations { get; set; } = new();
}

public sealed class FeatureStoreConfigJson
{
    public int FeatureId { get; set; }
    public string Store { get; set; } = string.Empty;
    public bool Found { get; set; }
    public int EnabledState { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; }
}
