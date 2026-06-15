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
