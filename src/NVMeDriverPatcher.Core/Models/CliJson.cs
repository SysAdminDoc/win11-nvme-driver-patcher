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
        DataAvailable = report.DataAvailable,
        FailureCode = report.FailureCode,
        ObservedVerdict = report.ObservedVerdict?.ToString(),
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
        BitLocker = report.BitLockerRecovery is { } proof
            ? new BitLockerRecoveryJson
            {
                ProbeSucceeded = proof.Volume.ProbeSucceeded,
                Encrypted = proof.Volume.IsEncrypted,
                ReadyForMutation = proof.ReadyForMutation,
                MountPoint = proof.Volume.MountPoint,
                ConversionStatus = proof.Volume.ConversionStatus,
                ProtectionStatus = proof.Volume.ProtectionStatus,
                SuspendCount = proof.Volume.SuspendCount,
                ProtectorIds = proof.Volume.RecoveryProtectorIds.ToList(),
                DirectoryJoin = proof.DirectoryJoin.Kind.ToString(),
                FailureCode = proof.Volume.FailureCode ?? proof.DirectoryJoin.FailureCode,
            }
            : null,
    };

    public static CriticalProbeReportJson BuildCriticalProbes(CriticalProbeReport report) => new()
    {
        Scope = report.Scope.ToString(),
        AllPassed = report.AllPassed,
        HasUnknown = report.HasUnknown,
        ExitCode = report.ExitCode,
        Items = report.Items.Select(item => new CriticalProbeJson
        {
            Id = item.Id,
            Label = item.Label,
            Verdict = item.Verdict.ToString(),
            ReasonCode = item.ReasonCode.ToString(),
            Detail = item.Detail,
            NativeError = item.NativeError,
            Evidence = item.Evidence.ToList(),
            ObservedAtUtc = item.ObservedAtUtc,
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
        CandidateProbeFailureCount = report.CandidateProbeFailureCount,
        ObservedAtUtc = report.ObservedAtUtc,
        Controllers = report.Controllers.Select(c => new ControllerJson
        {
            IsNative = c.IsNative,
            FriendlyName = c.FriendlyName,
            BoundDriver = c.BoundDriver,
            BoundDriverVersion = c.BoundDriverVersion,
            InstanceId = c.InstanceId,
            InfName = c.InfName,
            DriverProvider = c.DriverProvider,
            DeviceClass = c.DeviceClass,
            HardwareId = c.HardwareId,
            CompatibleId = c.CompatibleId,
            DriverCandidateCommand = c.DriverCandidateCommand,
            DriverCandidateProbeSucceeded = c.DriverCandidateProbeSucceeded,
            DriverCandidateProbeError = c.DriverCandidateProbeError,
            DriverCandidates = c.DriverCandidates.Select(candidate => new ControllerDriverCandidateJson
            {
                InfName = candidate.InfName,
                Provider = candidate.Provider,
                ClassName = candidate.ClassName,
                ClassGuid = candidate.ClassGuid,
                DriverVersion = candidate.DriverVersion,
                SignerName = candidate.SignerName,
                MatchingDeviceId = candidate.MatchingDeviceId,
                Rank = candidate.Rank,
                Status = candidate.Status,
                IsBestRanked = candidate.IsBestRanked,
                IsInstalled = candidate.IsInstalled
            }).ToList(),
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
    public bool DataAvailable { get; set; }
    public string? FailureCode { get; set; }
    public string? ObservedVerdict { get; set; }
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
    public int CandidateProbeFailureCount { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public List<ControllerJson> Controllers { get; set; } = new();
}

public sealed class RecoveryProofJson
{
    public bool AllPassed { get; set; }
    public int PassedCount { get; set; }
    public int TotalCount { get; set; }
    public List<RecoveryProofItemJson> Items { get; set; } = new();
    public BitLockerRecoveryJson? BitLocker { get; set; }
}

public sealed class BitLockerRecoveryJson
{
    public bool ProbeSucceeded { get; set; }
    public bool Encrypted { get; set; }
    public bool ReadyForMutation { get; set; }
    public string MountPoint { get; set; } = string.Empty;
    public uint ConversionStatus { get; set; }
    public uint ProtectionStatus { get; set; }
    public uint? SuspendCount { get; set; }
    public List<string> ProtectorIds { get; set; } = new();
    public string DirectoryJoin { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
}

public sealed class RecoveryProofItemJson
{
    public string Label { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class CriticalProbeReportJson
{
    public string Scope { get; set; } = string.Empty;
    public bool AllPassed { get; set; }
    public bool HasUnknown { get; set; }
    public int ExitCode { get; set; }
    public List<CriticalProbeJson> Items { get; set; } = new();
}

public sealed class CriticalProbeJson
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string? NativeError { get; set; }
    public List<string> Evidence { get; set; } = new();
    public DateTimeOffset ObservedAtUtc { get; set; }
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
    public string BoundDriverVersion { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string InfName { get; set; } = string.Empty;
    public string DriverProvider { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;
    public string CompatibleId { get; set; } = string.Empty;
    public string DriverCandidateCommand { get; set; } = string.Empty;
    public bool DriverCandidateProbeSucceeded { get; set; }
    public string DriverCandidateProbeError { get; set; } = string.Empty;
    public List<ControllerDriverCandidateJson> DriverCandidates { get; set; } = [];
}

public sealed class ControllerDriverCandidateJson
{
    public string InfName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string ClassGuid { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public string SignerName { get; set; } = string.Empty;
    public string MatchingDeviceId { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsBestRanked { get; set; }
    public bool IsInstalled { get; set; }
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
