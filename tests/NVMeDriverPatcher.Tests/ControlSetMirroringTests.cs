using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Issue #15: a boot-recovery promotion of a spare control set drops a patch that only ever wrote
/// CurrentControlSet. These pin the mirroring that closes that gap, and — critically — that the
/// mutation surface and the ledger baseline surface stay identical, because an asymmetry there
/// means uninstall silently leaves boot-critical keys behind.
/// </summary>
public class ControlSetMirroringTests
{
    // --- Target selection (pure) ---

    [Fact]
    public void SelectMirrorTargets_ExcludesTheCurrentControlSet()
    {
        var targets = ControlSetService.SelectMirrorTargets(
            new[] { "ControlSet001", "ControlSet002" }, currentControlSet: 1);
        Assert.Equal(new[] { "ControlSet002" }, targets);
    }

    [Fact]
    public void SelectMirrorTargets_IgnoresNonControlSetSiblings()
    {
        // The SYSTEM hive also holds Select, Setup, MountedDevices, WPA, ...
        var targets = ControlSetService.SelectMirrorTargets(
            new[] { "Select", "Setup", "MountedDevices", "ControlSet003", "ControlSet00X" },
            currentControlSet: 1);
        Assert.Equal(new[] { "ControlSet003" }, targets);
    }

    [Fact]
    public void SelectMirrorTargets_OrdersNumericallyAndDeduplicates()
    {
        var targets = ControlSetService.SelectMirrorTargets(
            new[] { "ControlSet009", "controlset002", "ControlSet002" }, currentControlSet: null);
        Assert.Equal(new[] { "ControlSet002", "ControlSet009" }, targets);
    }

    [Fact]
    public void SelectMirrorTargets_UnknownCurrentSet_MirrorsEverythingRatherThanSkipping()
    {
        // Failing open here writes a duplicate; failing closed would leave the exact gap
        // this feature exists to close.
        var targets = ControlSetService.SelectMirrorTargets(
            new[] { "ControlSet001", "ControlSet002" }, currentControlSet: null);
        Assert.Equal(new[] { "ControlSet001", "ControlSet002" }, targets);
    }

    [Fact]
    public void SelectMirrorTargets_NoControlSets_IsEmptyNotNull()
    {
        Assert.Empty(ControlSetService.SelectMirrorTargets(null, 1));
        Assert.Empty(ControlSetService.SelectMirrorTargets([], 1));
    }

    // --- Path rewriting (pure) ---

    [Fact]
    public void MirrorPath_RewritesOnlyTheControlSetSegment()
    {
        Assert.Equal(
            @"SYSTEM\ControlSet002\Policies\Microsoft\FeatureManagement\Overrides",
            ControlSetService.MirrorPath(AppConfig.RegistrySubKey, "ControlSet002"));
        Assert.Equal(
            @"SYSTEM\ControlSet002\Control\SafeBoot\Minimal\nvmedisk",
            ControlSetService.MirrorPath(AppConfig.SafeBootMinimalServicePath, "ControlSet002"));
    }

    [Fact]
    public void MirrorPath_RefusesPathsThatAreNotControlSetScoped()
    {
        Assert.Null(ControlSetService.MirrorPath(@"SOFTWARE\Microsoft\Windows", "ControlSet002"));
        Assert.Null(ControlSetService.MirrorPath(@"SYSTEM\CurrentControlSet\", "ControlSet002"));
        Assert.Null(ControlSetService.MirrorPath(null, "ControlSet002"));
        Assert.Null(ControlSetService.MirrorPath(AppConfig.RegistrySubKey, ""));
    }

    // --- Mutation surface ---

    [Fact]
    public void BuildRequiredRegistryMutations_WithoutMirrors_IsUnchanged()
    {
        var plain = PatchService.BuildRequiredRegistryMutations(PatchProfile.Full, includeServer: true);
        var explicitlyEmpty = PatchService.BuildRequiredRegistryMutations(
            PatchProfile.Full, includeServer: true, mirrorControlSets: []);
        Assert.Equal(plain.Count, explicitlyEmpty.Count);
        Assert.All(plain, m => Assert.StartsWith(@"SYSTEM\CurrentControlSet\", m.Path));
    }

    [Fact]
    public void BuildRequiredRegistryMutations_MirrorsEveryWriteIntoEachControlSet()
    {
        var primary = PatchService.BuildRequiredRegistryMutations(PatchProfile.Full, includeServer: true);
        var mirrored = PatchService.BuildRequiredRegistryMutations(
            PatchProfile.Full, includeServer: true, mirrorControlSets: ["ControlSet002", "ControlSet003"]);

        // Bait: if mirroring silently no-ops, this is the assertion that fails.
        Assert.Equal(primary.Count * 3, mirrored.Count);

        foreach (var controlSet in new[] { "ControlSet002", "ControlSet003" })
        {
            foreach (var original in primary)
            {
                var expectedPath = ControlSetService.MirrorPath(original.Path, controlSet);
                var mirror = Assert.Single(mirrored.Where(m =>
                    m.Path == expectedPath && m.ValueName == original.ValueName));
                Assert.Equal(original.ExpectedValue, mirror.ExpectedValue);
                Assert.Equal(original.ValueKind, mirror.ValueKind);
            }
        }
    }

    [Fact]
    public void MirroredMutations_NeverCountTowardThePatchTotal()
    {
        // A machine with more spare control sets must not report a different "applied N of M".
        var primary = PatchService.BuildRequiredRegistryMutations(PatchProfile.Safe, includeServer: false);
        var mirrored = PatchService.BuildRequiredRegistryMutations(
            PatchProfile.Safe, includeServer: false, mirrorControlSets: ["ControlSet002"]);

        int primaryCounted = primary.Count(m => m.CountsTowardPatchTotal);
        int mirroredCounted = mirrored.Count(m => m.CountsTowardPatchTotal);
        Assert.Equal(primaryCounted, mirroredCounted);
        Assert.All(
            mirrored.Where(m => !m.Path.StartsWith(@"SYSTEM\CurrentControlSet\", StringComparison.Ordinal)),
            m => Assert.False(m.CountsTowardPatchTotal));
    }

    [Fact]
    public void MirroredMutations_AreLabelledWithTheirControlSet()
    {
        var mirrored = PatchService.BuildRequiredRegistryMutations(
            PatchProfile.Safe, includeServer: false, mirrorControlSets: ["ControlSet002"]);
        Assert.Contains(mirrored, m => m.Label.EndsWith("[ControlSet002]", StringComparison.Ordinal));
    }

    // --- Baseline symmetry: the property that keeps uninstall exact ---

    [Fact]
    public void EveryMirroredWritePathIsCoveredByTheLedgerBaselineSurface()
    {
        string[] mirrors = ["ControlSet002", "ControlSet004"];
        var mutations = PatchService.BuildRequiredRegistryMutations(
            PatchProfile.Full, includeServer: true, mirrorControlSets: mirrors);

        // Feature-flag writes must be covered by the baseline's override subkeys...
        var baselineOverrideKeys = MutationLedgerService.FeatureOverrideSubKeys(mirrors);
        // ...and SafeBoot writes by the journal's managed key set.
        var baselineSafeBootPaths = SafeBootStateService.ManagedKeysFor(mirrors)
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uncovered = mutations
            .Where(m => !baselineOverrideKeys.Contains(m.Path, StringComparer.OrdinalIgnoreCase))
            .Where(m => !baselineSafeBootPaths.Contains(m.Path))
            .Select(m => m.Path)
            .Distinct()
            .ToArray();

        Assert.Empty(uncovered);
    }

    [Fact]
    public void BaselineSurfaceDoesNotGrowBeyondTheWriteSurface()
    {
        // The converse direction: capturing paths nothing writes would make ProbeBaselineDifferences
        // report drift on keys the patch never touched.
        string[] mirrors = ["ControlSet002"];
        var writtenPaths = PatchService
            .BuildRequiredRegistryMutations(PatchProfile.Full, includeServer: true, mirrorControlSets: mirrors)
            .Select(m => m.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in MutationLedgerService.FeatureOverrideSubKeys(mirrors))
            Assert.Contains(path, writtenPaths);
        foreach (var (path, _) in SafeBootStateService.ManagedKeysFor(mirrors))
            Assert.Contains(path, writtenPaths);
    }

    [Fact]
    public void ManagedKeysFor_WithoutMirrors_IsTheOriginalSet()
    {
        Assert.Equal(SafeBootStateService.ManagedKeys, SafeBootStateService.ManagedKeysFor(null));
        Assert.Equal(SafeBootStateService.ManagedKeys, SafeBootStateService.ManagedKeysFor([]));
    }

    [Fact]
    public void SafeBootJournalCapture_IncludesMirroredKeys()
    {
        var registry = new FakeSafeBootRegistry();
        var journal = SafeBootStateService.CaptureJournal(registry, "2026-08-07T00:00:00Z", ["ControlSet002"]);

        Assert.Equal(SafeBootStateService.ManagedKeys.Count * 2, journal.Entries.Count);
        Assert.Contains(journal.Entries, e =>
            e.Path == @"SYSTEM\ControlSet002\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}");
    }

    private sealed class FakeSafeBootRegistry : ISafeBootRegistry
    {
        public SafeBootKeySnapshot Read(string path) => new() { Existed = false, AccessDenied = false };
        public void ApplyRestore(string path, SafeBootRestorePlan plan) { }
    }
}
