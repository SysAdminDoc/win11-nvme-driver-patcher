using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// 24H2 26100.9550 ships the SafeBoot GUID keys owned by TrustedInstaller with "NvmeDisk" as the
/// default value. Even SYSTEM can't write them, so an apply that tried failed partway through the
/// batch. Apply now leaves them out of the write set and counts them, and status credits them once
/// the patch's flags are set.
/// </summary>
public sealed class WindowsOwnedSafeBootTests
{
    [Fact]
    public void Apply_LeavesWindowsOwnedSafeBootKeysOutOfTheWriteSetAndStillAddsUp()
    {
        var mutations = PatchService.BuildRequiredRegistryMutations(PatchProfile.Safe, includeServer: false, ["ControlSet002"]);
        var asked = new List<string>();

        var (writes, leftToWindows) = PatchService.SplitWindowsOwnedSafeBootWrites(mutations, path =>
        {
            asked.Add(path);
            return path.EndsWith(AppConfig.SafeBootGuid, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Equal(mutations.Count, writes.Count + leftToWindows.Count);
        // Both GUID keys, in CurrentControlSet and in the mirrored control set.
        Assert.Equal(4, leftToWindows.Count);
        Assert.All(leftToWindows, mutation => Assert.EndsWith(AppConfig.SafeBootGuid, mutation.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(writes, mutation => mutation.Path == AppConfig.RegistrySubKey);
        Assert.Contains(writes, mutation => mutation.Path == AppConfig.SafeBootMinimalServicePath);
        // Ownership is only asked about SafeBoot keys.
        Assert.NotEmpty(asked);
        Assert.All(asked, path => Assert.Contains(@"\Control\SafeBoot\", path, StringComparison.OrdinalIgnoreCase));

        // Apply succeeds only when committed plus covered components reach the profile total.
        int counted = writes.Count(m => m.CountsTowardPatchTotal) + leftToWindows.Count(m => m.CountsTowardPatchTotal);
        Assert.Equal(AppConfig.GetTotalComponents(PatchProfile.Safe, includeServerKey: false), counted);
        Assert.Equal(2, leftToWindows.Count(m => m.CountsTowardPatchTotal));
    }

    [Fact]
    public void Apply_WritesEverythingWhenNoKeyIsWindowsOwned()
    {
        var mutations = PatchService.BuildRequiredRegistryMutations(PatchProfile.Full, includeServer: true, ["ControlSet002"]);

        var (writes, leftToWindows) = PatchService.SplitWindowsOwnedSafeBootWrites(mutations, _ => false);

        Assert.Empty(leftToWindows);
        Assert.Equal(mutations, writes);
    }

    [Theory]
    // A stock machine: Windows' keys alone never make the patch look applied.
    [InlineData(false, false, false, true, true, false, false, 0)]
    // Flags set and Windows owns both keys: both count.
    [InlineData(true, false, false, true, true, true, true, 2)]
    // This tool's value in one key, Windows owns the other.
    [InlineData(true, true, false, false, true, true, true, 1)]
    // Flags set, keys neither ours nor Windows-owned: still missing.
    [InlineData(true, false, false, false, false, false, false, 0)]
    // Already ours: no double count.
    [InlineData(true, true, true, true, true, true, true, 0)]
    public void Status_CreditsWindowsOwnedKeysOnlyOnceAFlagIsSet(
        bool anyFlagSet, bool ourMin, bool ourNet, bool windowsMin, bool windowsNet,
        bool expectedMin, bool expectedNet, int expectedAdded)
    {
        var credit = RegistryService.CreditWindowsOwnedSafeBoot(anyFlagSet, ourMin, ourNet, windowsMin, windowsNet);

        Assert.Equal((expectedMin, expectedNet, expectedAdded), credit);
    }

    [Fact]
    public void Status_AfterApplyOnAWindowsOwnedBuild_IsACleanSafeInstall()
    {
        var patched = RegistryService.CreditWindowsOwnedSafeBoot(true, false, false, true, true);
        var applied = RegistryService.ClassifyPatchState(true, false, false, patched.Minimal, patched.Network, 1 + patched.Added);
        Assert.Equal(PatchAppliedProfile.Safe, applied.Profile);
        Assert.True(applied.Applied);
        Assert.False(applied.Partial);
        Assert.Equal(applied.ExpectedTotal, 1 + patched.Added);

        var stock = RegistryService.CreditWindowsOwnedSafeBoot(false, false, false, true, true);
        var untouched = RegistryService.ClassifyPatchState(false, false, false, stock.Minimal, stock.Network, stock.Added);
        Assert.Equal(PatchAppliedProfile.None, untouched.Profile);
        Assert.False(untouched.Applied);
        Assert.False(untouched.Partial);
    }
}
