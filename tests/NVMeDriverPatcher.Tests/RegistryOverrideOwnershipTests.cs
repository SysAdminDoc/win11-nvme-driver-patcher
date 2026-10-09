using Microsoft.Win32;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The FeatureManagement Overrides key is shared: Known Issue Rollback policies and other tools
/// write values there too. Only this tool's IDs may count as removal residue, or a removal on such
/// a machine stays PARTIAL forever and the recovery steps point users at values that aren't ours.
/// The tree is built under a scratch HKCU key (no admin), passed in place of HKLM.
/// </summary>
public sealed class RegistryOverrideOwnershipTests : IDisposable
{
    private readonly string _root = $@"Software\NVMeDriverPatcherTests\{Guid.NewGuid():N}";

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false); } catch { }
    }

    private RegistryKey BuildOverrides(params string[] valueNames)
    {
        var root = Registry.CurrentUser.CreateSubKey(_root, writable: true)!;
        using var overrides = root.CreateSubKey(AppConfig.RegistrySubKey, writable: true)!;
        foreach (var name in valueNames)
            overrides.SetValue(name, 0, RegistryValueKind.DWord);
        return root;
    }

    [Fact]
    public void ForeignOverrideValue_IsReportedForInformationButIsNotResidue()
    {
        using var root = BuildOverrides("4182523519");

        var report = PatchService.InspectRegistryOverrideOwnership(root);
        var residue = PatchService.ProbeRemovalResidue(root, workingDir: null, log: null);

        Assert.True(report.Readable);
        Assert.Empty(report.RemainingValueNames);
        Assert.False(report.HasBlockingResidue);
        Assert.Equal(["4182523519"], report.ForeignValueNames);
        Assert.Contains("4182523519", report.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(residue, r => r.StartsWith("Feature override value", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnedOverrideValue_IsStillResidueNextToForeignOnes()
    {
        using var root = BuildOverrides(AppConfig.PrimaryFeatureID, AppConfig.ServerFeatureID, "4182523519");

        var report = PatchService.InspectRegistryOverrideOwnership(root);
        var residue = PatchService.ProbeRemovalResidue(root, workingDir: null, log: null);

        Assert.Equal(
            new[] { AppConfig.ServerFeatureID, AppConfig.PrimaryFeatureID }.OrderBy(n => n, StringComparer.Ordinal),
            report.RemainingValueNames);
        Assert.Equal(["4182523519"], report.ForeignValueNames);
        Assert.Contains(residue, r => r.StartsWith($"Feature override value {AppConfig.PrimaryFeatureID} remains", StringComparison.Ordinal));
        Assert.Contains(residue, r => r.StartsWith($"Feature override value {AppConfig.ServerFeatureID} remains", StringComparison.Ordinal));
        Assert.DoesNotContain(residue, r => r.Contains("4182523519", StringComparison.Ordinal));
    }

    [Fact]
    public void ResidueEntries_AreFormattedAsValueNameAndOwnerAccess()
    {
        // The live-HKLM probe test is vacuous on a clean host; this pins the entry shape instead.
        using var root = BuildOverrides(AppConfig.PrimaryFeatureID);

        var residue = PatchService.ProbeRemovalResidue(root, workingDir: null, log: null);

        var entry = Assert.Single(residue.Where(r => r.StartsWith("Feature override value", StringComparison.Ordinal)));
        Assert.Matches(
            $@"^Feature override value {AppConfig.PrimaryFeatureID} remains \(owner .+; current user can rewrite\)$",
            entry);
    }

    [Fact]
    public void AbsentOverridesKey_LeavesNoFeatureOverrideResidue()
    {
        using var root = Registry.CurrentUser.CreateSubKey(_root, writable: true)!;

        var residue = PatchService.ProbeRemovalResidue(root, workingDir: null, log: null);

        Assert.DoesNotContain(residue, r => r.StartsWith("Feature override", StringComparison.Ordinal));
        Assert.DoesNotContain(residue, r => r.Contains("unverifiable", StringComparison.OrdinalIgnoreCase)
            && r.StartsWith("Feature overrides key", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnedValueNames_MatchWhatRemovalAndTheRecoveryKitDelete()
    {
        Assert.Equal(AppConfig.FeatureIDs.Append(AppConfig.ServerFeatureID), AppConfig.OwnedOverrideValueNames);
        Assert.True(AppConfig.IsOwnedOverrideValueName(AppConfig.PrimaryFeatureID));
        Assert.False(AppConfig.IsOwnedOverrideValueName(string.Empty));
        Assert.False(AppConfig.IsOwnedOverrideValueName("4182523519"));
    }

    [Fact]
    public void RecoveryKitAndDocs_SayForeignValuesStayInPlace()
    {
        var recovery = DocsService.Render("recovery");
        var uninstall = DocsService.Render("uninstall");

        Assert.Contains("Known Issue Rollback", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("enumerates every value", uninstall, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("this tool's values", recovery, StringComparison.OrdinalIgnoreCase);
    }
}
