using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// StorPort's own switches, as other tools write them: the global
/// <c>Control\StorPort\DisableNativeNVMeStack</c> and a controller's
/// <c>Device Parameters\StorPort\EnableNVMeInterface</c>. Each is covered missing, 0 and 1.
/// </summary>
public sealed class StorPortOverrideTests
{
    private const string Instance = @"PCI\VEN_144D&DEV_A80C&SUBSYS_A801144D&REV_00\4&1a2b3c4d&0&0008";

    private static StorPortOverrideSnapshot Snapshot(int? killSwitch, int? controllerValue) => new()
    {
        DisableNativeNVMeStack = killSwitch,
        Controllers = [new StorPortControllerOverride(Instance, "Standard NVM Express Controller", controllerValue)]
    };

    [Fact]
    public void NothingSet_RaisesNoReadinessWarningAndNoVerdictNote()
    {
        var snapshot = Snapshot(null, null);

        Assert.Null(StorPortOverrideService.Classify(snapshot));
        Assert.Null(StorPortOverrideService.DescribeForVerdict(snapshot, nativeActive: false));
        Assert.Null(StorPortOverrideService.DescribeForVerdict(snapshot, nativeActive: true));
        Assert.Empty(StorPortOverrideService.DescribeAfterRemoval(snapshot));
    }

    [Fact]
    public void KillSwitchZero_IsNoBlock()
    {
        var snapshot = Snapshot(0, null);

        Assert.Null(StorPortOverrideService.Classify(snapshot));
        Assert.Null(StorPortOverrideService.DescribeForVerdict(snapshot, nativeActive: false));
    }

    [Fact]
    public void KillSwitchOne_NamesTheValueAndPathInReadinessAndTheUnboundVerdict()
    {
        var snapshot = Snapshot(1, null);

        var check = StorPortOverrideService.Classify(snapshot);
        Assert.NotNull(check);
        Assert.Equal(CheckStatus.Warning, check.Status);
        Assert.Contains("DisableNativeNVMeStack is 1", check.Message, StringComparison.Ordinal);
        Assert.Contains(@"HKLM\SYSTEM\CurrentControlSet\Control\StorPort", check.Message, StringComparison.Ordinal);

        var verdict = StorPortOverrideService.DescribeForVerdict(snapshot, nativeActive: false);
        Assert.NotNull(verdict);
        Assert.Contains("DisableNativeNVMeStack is 1", verdict, StringComparison.Ordinal);
        Assert.Contains(@"HKLM\SYSTEM\CurrentControlSet\Control\StorPort", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerOne_NamesTheControllerInReadinessTheBoundVerdictAndRemove()
    {
        var snapshot = Snapshot(null, 1);
        string key = $@"HKLM\SYSTEM\CurrentControlSet\Enum\{Instance}\Device Parameters\StorPort";

        var check = StorPortOverrideService.Classify(snapshot);
        Assert.NotNull(check);
        Assert.Contains("Standard NVM Express Controller has EnableNVMeInterface=1", check.Message, StringComparison.Ordinal);
        Assert.Contains(key, check.Message, StringComparison.Ordinal);
        Assert.Contains("Remove doesn't take it off", check.Message, StringComparison.Ordinal);

        var verdict = StorPortOverrideService.DescribeForVerdict(snapshot, nativeActive: true);
        Assert.NotNull(verdict);
        Assert.Contains("EnableNVMeInterface=1", verdict, StringComparison.Ordinal);
        Assert.Contains(key, verdict, StringComparison.Ordinal);

        var removal = Assert.Single(StorPortOverrideService.DescribeAfterRemoval(snapshot));
        Assert.Contains("Standard NVM Express Controller has EnableNVMeInterface=1", removal, StringComparison.Ordinal);
        Assert.Contains(key, removal, StringComparison.Ordinal);
        Assert.Contains("nvmedisk stays bound on that controller", removal, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerZero_ExplainsTheLegacyHoldAndAddsNothingToRemove()
    {
        var snapshot = Snapshot(null, 0);

        var check = StorPortOverrideService.Classify(snapshot);
        Assert.NotNull(check);
        Assert.Contains("EnableNVMeInterface=0", check.Message, StringComparison.Ordinal);
        Assert.Contains("keeps this controller on the legacy driver", check.Message, StringComparison.Ordinal);

        var verdict = StorPortOverrideService.DescribeForVerdict(snapshot, nativeActive: false);
        Assert.NotNull(verdict);
        Assert.Contains("EnableNVMeInterface=0", verdict, StringComparison.Ordinal);

        // Bound elsewhere: the held controller is still on stornvme, and a Confirmed verdict says so.
        Assert.Contains("stays on stornvme.sys", StorPortOverrideService.DescribeForVerdict(snapshot, nativeActive: true), StringComparison.Ordinal);
        Assert.Empty(StorPortOverrideService.DescribeAfterRemoval(snapshot));
    }

    [Fact]
    public void KillSwitchOutranksAControllerOne()
    {
        var snapshot = Snapshot(1, 1);

        var check = StorPortOverrideService.Classify(snapshot);
        Assert.NotNull(check);
        Assert.Contains("but DisableNativeNVMeStack wins over it", check.Message, StringComparison.Ordinal);
        Assert.Null(StorPortOverrideService.DescribeForVerdict(snapshot, nativeActive: true));
        Assert.Empty(StorPortOverrideService.DescribeAfterRemoval(snapshot));
    }

    [Fact]
    public void Report_ListsBothValuesWhetherSetOrNot()
    {
        var unset = StorPortOverrideService.FormatForReport(Snapshot(null, null));
        Assert.Equal(@"DisableNativeNVMeStack (HKLM\SYSTEM\CurrentControlSet\Control\StorPort): not set", unset[0]);
        Assert.Equal($"Standard NVM Express Controller [{Instance}]: EnableNVMeInterface not set", unset[1]);

        var set = StorPortOverrideService.FormatForReport(Snapshot(0, 1));
        Assert.EndsWith(": 0", set[0], StringComparison.Ordinal);
        Assert.EndsWith("EnableNVMeInterface 1", set[1], StringComparison.Ordinal);

        var none = StorPortOverrideService.FormatForReport(new StorPortOverrideSnapshot());
        Assert.Equal(2, none.Count);
        Assert.Contains("No stornvme controllers", none[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerWithoutAFriendlyName_FallsBackToItsInstanceId()
    {
        var snapshot = new StorPortOverrideSnapshot
        {
            Controllers = [new StorPortControllerOverride(Instance, null, 1)]
        };

        var removal = Assert.Single(StorPortOverrideService.DescribeAfterRemoval(snapshot));
        Assert.StartsWith($"{Instance} has EnableNVMeInterface=1", removal, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyHold_IsTheKillSwitchOrEveryControllerAtZero_AndNothingElse()
    {
        Assert.Contains("DisableNativeNVMeStack is 1", StorPortOverrideService.DescribeLegacyHold(Snapshot(1, null)), StringComparison.Ordinal);
        Assert.Contains("EnableNVMeInterface=0", StorPortOverrideService.DescribeLegacyHold(Snapshot(null, 0)), StringComparison.Ordinal);
        Assert.Null(StorPortOverrideService.DescribeLegacyHold(Snapshot(null, null)));
        Assert.Null(StorPortOverrideService.DescribeLegacyHold(Snapshot(0, 1)));
        Assert.Null(StorPortOverrideService.DescribeLegacyHold(new StorPortOverrideSnapshot()));

        // One controller held at 0, another left to the feature route: not a whole-machine hold.
        var mixed = new StorPortOverrideSnapshot
        {
            Controllers =
            [
                new StorPortControllerOverride(Instance, "Held", 0),
                new StorPortControllerOverride(Instance + "2", "Free", null)
            ]
        };
        Assert.Null(StorPortOverrideService.DescribeLegacyHold(mixed));
    }

    [Fact]
    public void BoundVerdict_NamesAControllerStillHeldAtZero()
    {
        var mixed = new StorPortOverrideSnapshot
        {
            Controllers =
            [
                new StorPortControllerOverride(Instance, "Held", 0),
                new StorPortControllerOverride(Instance + "2", "Free", null)
            ]
        };

        var verdict = StorPortOverrideService.DescribeForVerdict(mixed, nativeActive: true);

        Assert.NotNull(verdict);
        Assert.Contains("Held has EnableNVMeInterface=0", verdict, StringComparison.Ordinal);
        Assert.Contains("stays on stornvme.sys while the others use nvmedisk", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ForcedNative_IsReportedForActivationAndNotForAKillSwitch()
    {
        Assert.True(StorPortOverrideService.ForcesNativeAnywhere(Snapshot(null, 1)));
        Assert.False(StorPortOverrideService.ForcesNativeAnywhere(Snapshot(1, 1)));
        Assert.False(StorPortOverrideService.ForcesNativeAnywhere(Snapshot(null, 0)));
    }

    [Fact]
    public void ValueOfAnotherType_IsNamedInReadinessAndTheReport_NotCalledUnset()
    {
        var snapshot = new StorPortOverrideSnapshot
        {
            DisableNativeNVMeStackOtherKind = "REG_SZ \"1\"",
            Controllers = [new StorPortControllerOverride(Instance, "Standard NVM Express Controller", null, "REG_QWORD 1")]
        };

        var check = StorPortOverrideService.Classify(snapshot);
        Assert.NotNull(check);
        Assert.Contains("DisableNativeNVMeStack is set under", check.Message, StringComparison.Ordinal);
        Assert.Contains("as REG_SZ \"1\", not a DWORD", check.Message, StringComparison.Ordinal);
        Assert.Contains("EnableNVMeInterface set as REG_QWORD 1, not a DWORD", check.Message, StringComparison.Ordinal);

        var report = StorPortOverrideService.FormatForReport(snapshot);
        Assert.EndsWith("present as REG_SZ \"1\" (not a DWORD)", report[0], StringComparison.Ordinal);
        Assert.EndsWith("present as REG_QWORD 1 (not a DWORD)", report[1], StringComparison.Ordinal);

        // Unknown effect, so no hold is claimed, but the unbound verdict still names it.
        Assert.Null(StorPortOverrideService.DescribeLegacyHold(snapshot));
        Assert.Contains("not a DWORD", StorPortOverrideService.DescribeForVerdict(snapshot, nativeActive: false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Microsoft.Win32.RegistryValueKind.String, "1", "REG_SZ \"1\"")]
    [InlineData(Microsoft.Win32.RegistryValueKind.QWord, 1L, "REG_QWORD 1")]
    public void OtherKinds_DescribeTheirTypeAndValue(Microsoft.Win32.RegistryValueKind kind, object raw, string expected)
    {
        Assert.Equal(expected, StorPortOverrideService.DescribeOtherKind(kind, raw));
    }

    [Fact]
    public void LiveSnapshot_ReadsWithoutThrowing()
    {
        // Read-only probe of this machine; the values are almost always unset, so only shape is checked.
        var snapshot = StorPortOverrideService.ReadSnapshot();

        Assert.NotNull(snapshot.Controllers);
        Assert.All(snapshot.Controllers, c => Assert.False(string.IsNullOrWhiteSpace(c.InstanceId)));
        Assert.NotEmpty(StorPortOverrideService.FormatForReport(snapshot));
    }
}
