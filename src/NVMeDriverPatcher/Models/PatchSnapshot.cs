namespace NVMeDriverPatcher.Models;

public class PatchSnapshot
{
    public string Timestamp { get; set; } = string.Empty;
    public PatchStatus Status { get; set; } = new();
    public string DriverActive { get; set; } = "Unknown";
    public bool BypassIO { get; set; }
    public Dictionary<string, string> Components { get; set; } = [];
}

public class PatchStatus
{
    public bool Applied { get; set; }
    public bool Partial { get; set; }
    public int Count { get; set; }
    public int Total { get; set; } = AppConfig.TotalComponents;
    public List<string> Keys { get; set; } = [];

    /// <summary>Which install profile the set of keys currently present in the registry matches,
    /// so the UI can say "Safe Mode active" instead of the misleading "3/5 partial" readout.
    /// <para/>
    /// None = nothing applied. Safe = primary flag + both SafeBoot keys (3 components).
    /// Full = all three feature flags + both SafeBoot keys (5 components).
    /// Mixed = some subset of keys that matches neither clean profile — e.g. a third-party
    /// tool wrote one of our flags, a Windows update stripped one, or a prior install's
    /// rollback was incomplete.</summary>
    public PatchAppliedProfile DetectedProfile { get; set; } = PatchAppliedProfile.None;
}

public enum PatchAppliedProfile
{
    None = 0,
    Safe = 1,
    Full = 2,
    Mixed = 3
}
