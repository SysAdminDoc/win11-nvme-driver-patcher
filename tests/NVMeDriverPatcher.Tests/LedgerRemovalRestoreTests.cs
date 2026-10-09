using Microsoft.Win32;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The ledger arrived in v5.1.0, so one captured over a v5.0.0 patch records that version's flags
/// as pre-existing. Remove must clear them, not write them back under a REMOVED verdict. The
/// registry is a scratch HKCU tree (no admin), passed in place of HKLM.
/// </summary>
public sealed class LedgerRemovalRestoreTests : IDisposable
{
    private const string ExtendedFlag = "1853569164";
    private readonly string _root = $@"Software\NVMeDriverPatcherTests\{Guid.NewGuid():N}";

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false); } catch { }
    }

    private static RegistryValueBaseline Set(string name, long data = 1, string keyPath = AppConfig.RegistrySubKey) => new()
    {
        KeyPath = keyPath,
        ValueName = name,
        Existed = true,
        Kind = (int)RegistryValueKind.DWord,
        IntegerData = data
    };

    private static RegistryValueBaseline Absent(string name, string keyPath = AppConfig.RegistrySubKey) => new()
    {
        KeyPath = keyPath,
        ValueName = name,
        Existed = false
    };

    // What a v5.0.0 Full patch left before the first ledger was written.
    private static List<RegistryValueBaseline> CapturedOverV500Patch() =>
    [
        Set(AppConfig.PrimaryFeatureID),
        Set(ExtendedFlag),
        Set(AppConfig.StandaloneFutureFeatureID),
        Absent(AppConfig.ServerFeatureID)
    ];

    private RegistryKey LiveTreeWith(params string[] valueNames)
    {
        var root = Registry.CurrentUser.CreateSubKey(_root, writable: true)!;
        using var overrides = root.CreateSubKey(AppConfig.RegistrySubKey, writable: true)!;
        foreach (var name in valueNames)
            overrides.SetValue(name, 1, RegistryValueKind.DWord);
        return root;
    }

    private static string[] OwnedValuesIn(RegistryKey root)
    {
        using var overrides = root.OpenSubKey(AppConfig.RegistrySubKey)!;
        return overrides.GetValueNames().Where(AppConfig.IsOwnedOverrideValueName).ToArray();
    }

    [Fact]
    public void RemovalRestore_OverAV500Baseline_LeavesTheOwnedValuesAbsentAndSaysWhy()
    {
        var baseline = CapturedOverV500Patch();
        using var root = LiveTreeWith(AppConfig.PrimaryFeatureID, ExtendedFlag, AppConfig.StandaloneFutureFeatureID);
        var log = new List<string>();
        var failures = new List<string>();

        var targets = MutationLedgerService.RemovalTargets(baseline, log.Add);
        MutationLedgerService.RestoreRegistryValues(root, targets, failures, log.Add);

        Assert.Empty(failures);
        Assert.Empty(OwnedValuesIn(root));
        Assert.All(targets, t => Assert.False(t.Existed));
        foreach (var id in new[] { AppConfig.PrimaryFeatureID, ExtendedFlag, AppConfig.StandaloneFutureFeatureID })
            Assert.Contains(log, l => l.Contains($"Clearing {id}", StringComparison.Ordinal) &&
                                      l.Contains("presumed to be an older version's", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains($"Clearing {AppConfig.ServerFeatureID}", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactRestore_OverTheSameBaseline_StillWritesItBack()
    {
        // Rollback of a failed apply returns the machine to how it was, older patch included.
        var baseline = CapturedOverV500Patch();
        using var root = LiveTreeWith();
        var failures = new List<string>();

        MutationLedgerService.RestoreRegistryValues(root, baseline, failures, log: null);

        Assert.Empty(failures);
        Assert.Equal(
            new[] { AppConfig.PrimaryFeatureID, ExtendedFlag, AppConfig.StandaloneFutureFeatureID }.Order(StringComparer.Ordinal),
            OwnedValuesIn(root).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RemovalTargets_WithoutThePrimaryFlag_RestoresTheExtrasExactly()
    {
        // Another tool set 156965516 before this tool's first apply; it's theirs and comes back.
        List<RegistryValueBaseline> baseline =
        [
            Absent(AppConfig.PrimaryFeatureID),
            Absent(ExtendedFlag),
            Set(AppConfig.StandaloneFutureFeatureID, data: 2),
            Absent(AppConfig.ServerFeatureID)
        ];
        using var root = LiveTreeWith(AppConfig.PrimaryFeatureID, ExtendedFlag);
        var log = new List<string>();
        var failures = new List<string>();

        var targets = MutationLedgerService.RemovalTargets(baseline, log.Add);
        MutationLedgerService.RestoreRegistryValues(root, targets, failures, log.Add);

        Assert.Same(baseline[2], targets[2]);
        Assert.Empty(failures);
        Assert.DoesNotContain(log, l => l.Contains("Clearing", StringComparison.Ordinal));
        using var overrides = root.OpenSubKey(AppConfig.RegistrySubKey)!;
        Assert.Equal([AppConfig.StandaloneFutureFeatureID], OwnedValuesIn(root));
        Assert.Equal(2, (int)overrides.GetValue(AppConfig.StandaloneFutureFeatureID)!);
    }

    [Fact]
    public void RemovalTargets_DecidePerKey()
    {
        // A mirrored control set whose baseline lacks the primary flag keeps its own values.
        const string mirror = @"SYSTEM\ControlSet002\Policies\Microsoft\FeatureManagement\Overrides";
        List<RegistryValueBaseline> baseline =
        [
            Set(AppConfig.PrimaryFeatureID),
            Set(ExtendedFlag),
            Absent(AppConfig.PrimaryFeatureID, mirror),
            Set(ExtendedFlag, keyPath: mirror)
        ];

        var targets = MutationLedgerService.RemovalTargets(baseline, log: null);

        Assert.False(targets[0].Existed);
        Assert.False(targets[1].Existed);
        Assert.Same(baseline[3], targets[3]);
    }

    [Fact]
    public void OwnedValuesLeftAfterRemoval_CountsThisToolsValuesButNotOnesRestoredAsSomeoneElses()
    {
        List<RegistryValueBaseline> foreignExtra =
        [
            Absent(AppConfig.PrimaryFeatureID),
            Set(AppConfig.StandaloneFutureFeatureID)
        ];
        Assert.Empty(MutationLedgerService.OwnedValuesLeftAfterRemoval([AppConfig.StandaloneFutureFeatureID], foreignExtra));
        Assert.Equal(
            [AppConfig.PrimaryFeatureID],
            MutationLedgerService.OwnedValuesLeftAfterRemoval([AppConfig.PrimaryFeatureID, AppConfig.StandaloneFutureFeatureID], foreignExtra));

        // Over a v5.0.0 baseline every owned value still set is this tool's, so Remove is PARTIAL.
        Assert.Equal(
            [AppConfig.PrimaryFeatureID, ExtendedFlag],
            MutationLedgerService.OwnedValuesLeftAfterRemoval([AppConfig.PrimaryFeatureID, ExtendedFlag], CapturedOverV500Patch()));

        // A value the ledger never recorded can't be shown to be someone else's.
        Assert.Equal(
            [AppConfig.ServerFeatureID],
            MutationLedgerService.OwnedValuesLeftAfterRemoval([AppConfig.ServerFeatureID], foreignExtra));
    }
}
