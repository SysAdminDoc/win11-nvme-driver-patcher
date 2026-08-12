namespace NVMeDriverPatcher.Models;

/// <summary>
/// Read-only evidence for the FeatureManagement override key after a removal attempt.
/// TrustedInstaller-owned keys can leave an administrator unable to use ViVeTool's
/// <c>/fullreset</c>, so removal must surface both the remaining values and the write check.
/// </summary>
public sealed record RegistryOverrideOwnershipReport(
    bool KeyExists,
    bool Readable,
    string Owner,
    bool CurrentUserCanWrite,
    IReadOnlyList<string> RemainingValueNames,
    string Summary)
{
    public bool HasRemainingValues => RemainingValueNames.Count > 0;

    /// <summary>True when clean removal cannot be proven or a remaining value is not writable.</summary>
    public bool HasBlockingResidue => !Readable || (HasRemainingValues && !CurrentUserCanWrite);
}
