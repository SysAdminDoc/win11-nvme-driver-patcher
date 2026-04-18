using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Exhaustive tests for <c>RegistryService.ClassifyPatchState</c>. This is the v4.3.1
/// logic that fixed "Safe Mode reports PARTIAL" — the surface is small but the
/// correctness matters, because every readout the user sees (GUI status card, CLI
/// exit code, diagnostics bundle) builds on it.
/// </summary>
public sealed class RegistryServiceClassifyTests
{
    [Fact]
    public void EmptyRegistry_ClassifiesAsNone()
    {
        // No keys present at all → None, not applied, not partial.
        var c = RegistryService.ClassifyPatchState(
            primarySet: false, extendedA: false, extendedB: false,
            safeBootMin: false, safeBootNet: false, count: 0);

        Assert.Equal(PatchAppliedProfile.None, c.Profile);
        Assert.False(c.Applied);
        Assert.False(c.Partial);
    }

    [Fact]
    public void CleanSafeInstall_ClassifiesAsSafeApplied()
    {
        // Safe Mode writes: primary flag + both SafeBoot keys. The reason for extracting this
        // classifier — a Safe install had previously been mis-reported as PARTIAL.
        var c = RegistryService.ClassifyPatchState(
            primarySet: true, extendedA: false, extendedB: false,
            safeBootMin: true, safeBootNet: true, count: 3);

        Assert.Equal(PatchAppliedProfile.Safe, c.Profile);
        Assert.True(c.Applied);
        Assert.False(c.Partial);
    }

    [Fact]
    public void CleanFullInstall_ClassifiesAsFullApplied()
    {
        // Full Mode writes all three feature flags + both SafeBoot keys.
        var c = RegistryService.ClassifyPatchState(
            primarySet: true, extendedA: true, extendedB: true,
            safeBootMin: true, safeBootNet: true, count: 5);

        Assert.Equal(PatchAppliedProfile.Full, c.Profile);
        Assert.True(c.Applied);
        Assert.False(c.Partial);
    }

    [Fact]
    public void PrimaryOnly_NoSafeBoot_ClassifiesAsMixed()
    {
        // Primary flag written but BOTH SafeBoot keys missing — not a recognized profile.
        // Typical cause: a manual reg-add by a user who didn't know about SafeBoot entries.
        var c = RegistryService.ClassifyPatchState(
            primarySet: true, extendedA: false, extendedB: false,
            safeBootMin: false, safeBootNet: false, count: 1);

        Assert.Equal(PatchAppliedProfile.Mixed, c.Profile);
        Assert.False(c.Applied);
        Assert.True(c.Partial);
    }

    [Fact]
    public void SafeProfileMissingOneSafeBootKey_ClassifiesAsMixed()
    {
        // Regression guard: both SafeBoot keys are required for clean Safe; missing Network
        // demotes to Mixed even though Minimal + primary look otherwise correct.
        var c = RegistryService.ClassifyPatchState(
            primarySet: true, extendedA: false, extendedB: false,
            safeBootMin: true, safeBootNet: false, count: 2);

        Assert.Equal(PatchAppliedProfile.Mixed, c.Profile);
        Assert.False(c.Applied);
        Assert.True(c.Partial);
    }

    [Fact]
    public void FullProfileMissingOneExtendedFlag_ClassifiesAsMixed()
    {
        // Windows Update (or a failed rollback) dropped one of the two extended flags. Not
        // a clean Safe (because one extended IS still set), not a clean Full (because one is
        // missing) → Mixed.
        var c = RegistryService.ClassifyPatchState(
            primarySet: true, extendedA: true, extendedB: false,
            safeBootMin: true, safeBootNet: true, count: 4);

        Assert.Equal(PatchAppliedProfile.Mixed, c.Profile);
        Assert.False(c.Applied);
        Assert.True(c.Partial);
    }

    [Fact]
    public void NoPrimaryButExtendedSet_ClassifiesAsMixed()
    {
        // The primary flag is the one that actually swaps the driver. If only extended flags
        // are set (e.g. via a third-party tool that picked them for unrelated reasons), the
        // state is obviously not Applied — but it's definitely not clean either.
        var c = RegistryService.ClassifyPatchState(
            primarySet: false, extendedA: true, extendedB: true,
            safeBootMin: true, safeBootNet: true, count: 4);

        Assert.Equal(PatchAppliedProfile.Mixed, c.Profile);
        Assert.False(c.Applied);
        Assert.True(c.Partial);
    }

    [Fact]
    public void OnlySafeBootKeys_NoFeatureFlags_ClassifiesAsMixed()
    {
        // SafeBoot entries present but no feature flags at all — an incomplete prior install
        // whose rollback cleaned the overrides but left the SafeBoot subtrees. Must still
        // report as Partial so the user is prompted to re-apply or remove.
        var c = RegistryService.ClassifyPatchState(
            primarySet: false, extendedA: false, extendedB: false,
            safeBootMin: true, safeBootNet: true, count: 2);

        Assert.Equal(PatchAppliedProfile.Mixed, c.Profile);
        Assert.False(c.Applied);
        Assert.True(c.Partial);
    }

    [Fact]
    public void ZeroCountOverridesAllBooleans()
    {
        // Defensive: if the caller somehow reports count=0 while flags look set (shouldn't
        // happen in practice but the helper shouldn't care), count wins and we report None.
        var c = RegistryService.ClassifyPatchState(
            primarySet: true, extendedA: true, extendedB: true,
            safeBootMin: true, safeBootNet: true, count: 0);

        Assert.Equal(PatchAppliedProfile.None, c.Profile);
        Assert.False(c.Applied);
        Assert.False(c.Partial);
    }

    [Fact]
    public void NegativeCountIsTreatedAsEmpty()
    {
        // Defensive: callers occasionally hand back a negative sentinel on error paths.
        // The helper should not crash or lie about the state.
        var c = RegistryService.ClassifyPatchState(
            primarySet: true, extendedA: false, extendedB: false,
            safeBootMin: true, safeBootNet: true, count: -1);

        Assert.Equal(PatchAppliedProfile.None, c.Profile);
        Assert.False(c.Applied);
        Assert.False(c.Partial);
    }

    [Theory]
    // Every possible combination of the 5 boolean inputs with a plausible count (count
    // matters for the edge cases handled in the dedicated tests above, but most
    // combinations yield Mixed regardless). This is a cheap sanity net: the two CLEAN
    // profiles each have exactly ONE row, everything else must be Mixed or None.
    [InlineData(false, false, false, false, true,  1, PatchAppliedProfile.Mixed)]
    [InlineData(false, false, false, true,  true,  2, PatchAppliedProfile.Mixed)]
    [InlineData(true,  false, false, false, true,  2, PatchAppliedProfile.Mixed)]
    [InlineData(true,  false, false, true,  true,  3, PatchAppliedProfile.Safe)]  // clean Safe
    [InlineData(true,  true,  false, true,  true,  4, PatchAppliedProfile.Mixed)] // partial Full
    [InlineData(true,  true,  true,  true,  true,  5, PatchAppliedProfile.Full)]  // clean Full
    [InlineData(true,  true,  true,  true,  false, 4, PatchAppliedProfile.Mixed)] // Full minus one SafeBoot
    [InlineData(true,  true,  true,  false, true,  4, PatchAppliedProfile.Mixed)] // Full minus other SafeBoot
    [InlineData(false, true,  true,  true,  true,  4, PatchAppliedProfile.Mixed)] // missing primary
    public void ProfileClassification_IsStable(
        bool primary, bool extA, bool extB, bool safeMin, bool safeNet, int count,
        PatchAppliedProfile expected)
    {
        var c = RegistryService.ClassifyPatchState(primary, extA, extB, safeMin, safeNet, count);
        Assert.Equal(expected, c.Profile);
    }
}
