namespace NVMeDriverPatcher.Models;

/// <summary>
/// Read-only evidence for the FeatureManagement override key after a removal attempt.
/// TrustedInstaller-owned keys can leave an administrator unable to use ViVeTool's
/// <c>/fullreset</c>, so removal must surface both the remaining values and the write check.
/// <see cref="RemainingValueNames"/> holds only this tool's values (the residue);
/// <see cref="ForeignValueNames"/> holds everything else under the key, for information.
/// </summary>
public sealed record RegistryOverrideOwnershipReport(
    bool KeyExists,
    bool Readable,
    string Owner,
    bool CurrentUserCanWrite,
    IReadOnlyList<string> RemainingValueNames,
    string Summary)
{
    /// <summary>Values under the key that this tool never writes (Known Issue Rollback policies,
    /// other tools). Never residue and never something the recovery steps delete.</summary>
    public IReadOnlyList<string> ForeignValueNames { get; init; } = Array.Empty<string>();

    public bool HasRemainingValues => RemainingValueNames.Count > 0;

    /// <summary>True when clean removal cannot be proven or a remaining value is not writable.</summary>
    public bool HasBlockingResidue => !Readable || (HasRemainingValues && !CurrentUserCanWrite);
}
