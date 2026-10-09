using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Registry shapes from third-party NVMe scripts. FR33THY "Ultimate" applies 735209102,
/// 3244671118, 1853569164 and 156965516 plus "Storage disks" SafeBoot entries; its revert deletes
/// the whole Policies\Microsoft tree. Windows' own SafeBoot entry on current builds says "NvmeDisk".
/// </summary>
public sealed class ThirdPartyResidueTests
{
    private static readonly string[] Fr33thySet = ["735209102", "3244671118", "1853569164", "156965516"];

    [Fact]
    public void WipedPoliciesTreeWithSafeBootLeftBehind_NamesTheOrphansAndTheWipe()
    {
        var check = ThirdPartyResidueService.Classify(new ThirdPartyResidueSnapshot
        {
            OverrideValueNames = null,
            PoliciesMicrosoftExists = false,
            SafeBootMinimalDefault = "Storage disks",
            SafeBootNetworkDefault = "Storage disks"
        });

        Assert.NotNull(check);
        Assert.Equal(CheckStatus.Warning, check.Status);
        Assert.False(check.Critical);
        Assert.Contains("Safe Boot Minimal and Network entries", check.Message, StringComparison.Ordinal);
        Assert.Contains("no NVMe override is", check.Message, StringComparison.Ordinal);
        Assert.Contains("Remove clears them", check.Message, StringComparison.Ordinal);
        Assert.Contains("run the Safe Boot upgrade first", check.Message, StringComparison.Ordinal);
        Assert.Contains(@"Policies\Microsoft registry tree is gone", check.Message, StringComparison.Ordinal);
        Assert.Contains("gpupdate /force", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverridesClearedButPolicyTreeIntact_NamesTheOrphanWithoutTheWipeNote()
    {
        var check = ThirdPartyResidueService.Classify(new ThirdPartyResidueSnapshot
        {
            OverrideValueNames = [],
            PoliciesMicrosoftExists = true,
            SafeBootMinimalDefault = "Storage Disks",
            SafeBootNetworkDefault = null
        });

        Assert.NotNull(check);
        Assert.Contains("Safe Boot Minimal entry", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Policies", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fr33thyAppliedState_NamesTheForeignOverrideOnly()
    {
        var check = ThirdPartyResidueService.Classify(new ThirdPartyResidueSnapshot
        {
            OverrideValueNames = Fr33thySet,
            PoliciesMicrosoftExists = true,
            SafeBootMinimalDefault = "Storage disks",
            SafeBootNetworkDefault = "Storage disks"
        });

        Assert.NotNull(check);
        Assert.Contains("Override 3244671118 is set", check.Message, StringComparison.Ordinal);
        Assert.Contains("Remove leaves it in place", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SafeBoot", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignOverrideAloneNextToWindowsOwnSafeBootEntry_IsNamed()
    {
        var check = ThirdPartyResidueService.Classify(new ThirdPartyResidueSnapshot
        {
            OverrideValueNames = ["3244671118"],
            PoliciesMicrosoftExists = true,
            SafeBootMinimalDefault = "NvmeDisk",
            SafeBootNetworkDefault = "NvmeDisk"
        });

        Assert.NotNull(check);
        Assert.Contains("3244671118", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SafeBoot", check.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string[]?, bool, string?, string?> CleanShapes => new()
    {
        // Stock 25H2 (seen on 26200.9457): no Policies\Microsoft, Windows' own "NvmeDisk" entry.
        { null, false, "NvmeDisk", "NvmeDisk" },
        // Stock 24H2 before Windows shipped the entry.
        { null, false, null, null },
        // This tool's Safe profile, applied.
        { ["735209102"], true, "Storage Disks", "Storage Disks" },
        // Known Issue Rollback policies only. Unknown values under Overrides aren't NVMe residue.
        { ["3058630794", "4182523519"], true, "NvmeDisk", null },
    };

    [Theory]
    [MemberData(nameof(CleanShapes))]
    public void StockAndOwnStates_RaiseNothing(string[]? overrides, bool policiesExist, string? safeMin, string? safeNet)
    {
        var snapshot = new ThirdPartyResidueSnapshot
        {
            OverrideValueNames = overrides,
            PoliciesMicrosoftExists = policiesExist,
            SafeBootMinimalDefault = safeMin,
            SafeBootNetworkDefault = safeNet
        };

        Assert.Null(ThirdPartyResidueService.Classify(snapshot));
        Assert.Empty(ThirdPartyResidueService.FindOrphanedSafeBootStores(snapshot));
    }

    [Theory]
    [InlineData("Storage Disks", true)]
    [InlineData("Storage disks", true)]
    [InlineData("STORAGE DISKS", true)]
    [InlineData("Storage Disks ", false)]
    [InlineData("NvmeDisk", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PatchSafeBootDefault_MatchesScriptSpellingsButNeverWindowsOwnValue(string? value, bool expected)
    {
        Assert.Equal(expected, AppConfig.IsPatchSafeBootDefault(value));
    }

    [Fact]
    public void ForeignOverrideDescription_LabelsKnownScriptValuesOnly()
    {
        Assert.Equal("3244671118 (set by third-party NVMe scripts such as FR33THY Ultimate)",
            AppConfig.DescribeForeignOverrideValue("3244671118"));
        Assert.Equal("4182523519", AppConfig.DescribeForeignOverrideValue("4182523519"));
        Assert.Equal("(Default)", AppConfig.DescribeForeignOverrideValue(""));
        Assert.False(AppConfig.IsOwnedOverrideValueName("3244671118"));
    }
}
