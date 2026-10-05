using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// DryRunService.PlanInstall is a pure(-ish) projection: the only impure read is a registry
// probe for current values, which gracefully returns null when denied (under xUnit we don't
// run elevated). These tests pin the profile→item mapping that drives the user-facing
// "what will change" summary — a regression here would silently under-/over-count writes.
public sealed class DryRunServiceTests
{
    // A clean machine: no override values and none of the SafeBoot keys.
    private static readonly Func<string, string, DryRunService.CurrentRegistryValue> CleanMachine =
        (_, _) => new DryRunService.CurrentRegistryValue(false, null);

    [Fact]
    public void SafeProfile_Plans_OnePrimaryWrite_FourSafeBootCreates()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe, IncludeServerKey = false };
        var report = DryRunService.PlanInstall(config, null, [], CleanMachine);

        Assert.Equal(PatchProfile.Safe, report.Profile);
        Assert.False(report.IncludeServerKey);
        Assert.Equal(1, report.TotalWrites);
        Assert.Equal(SafeBootStateService.ManagedKeys.Count, report.TotalCreates);
        Assert.Contains(report.Items, i => i.Action == "WRITE" && i.ValueName == AppConfig.PrimaryFeatureID);
        Assert.Equal(4, report.Items.Count(i => i.Action == "CREATE"));
        Assert.Contains("machine-wide", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("per-drive exclusions are not enforced", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FullProfile_Plans_ThreeWrites_FourSafeBootCreates()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Full, IncludeServerKey = false };
        var report = DryRunService.PlanInstall(config, null, [], CleanMachine);

        Assert.Equal(3, report.TotalWrites);
        Assert.Equal(4, report.TotalCreates);
        foreach (var id in AppConfig.FeatureIDs)
            Assert.Contains(report.Items, i => i.Action == "WRITE" && i.ValueName == id);
    }

    [Theory]
    [InlineData(PatchProfile.Safe, false)]
    [InlineData(PatchProfile.Full, true)]
    public void Preview_ListsExactlyWhatApplyWrites(PatchProfile profile, bool includeServer)
    {
        // The preview used to hardcode two SafeBoot rows while apply wrote four. Pin it to the
        // same list apply commits, mirrors included.
        string[] mirrors = ["ControlSet002"];
        var config = new AppConfig { PatchProfile = profile, IncludeServerKey = includeServer };
        var report = DryRunService.PlanInstall(config, null, mirrors, CleanMachine);
        var applies = PatchService.BuildRequiredRegistryMutations(profile, includeServer, mirrors);

        Assert.Equal(
            applies.Select(m => $@"HKEY_LOCAL_MACHINE\{m.Path}|{(m.ValueName.Length == 0 ? "(default)" : m.ValueName)}"),
            report.Items.Select(i => $"{i.Target}|{i.ValueName}"));
    }

    [Fact]
    public void WindowsOwnedSafeBootKeys_AreKeptAsTheyAre()
    {
        // 24H2 26100.9550 ships the GUID keys owned by TrustedInstaller with a "NvmeDisk" default
        // value. Apply leaves them alone, so the preview must too.
        var config = new AppConfig { PatchProfile = PatchProfile.Safe, IncludeServerKey = false };
        var report = DryRunService.PlanInstall(config, null, [], (path, _) =>
            path.EndsWith(AppConfig.SafeBootGuid, StringComparison.OrdinalIgnoreCase)
                ? new DryRunService.CurrentRegistryValue(true, "NvmeDisk", WindowsOwned: true)
                : new DryRunService.CurrentRegistryValue(false, null));

        foreach (var path in new[] { AppConfig.SafeBootMinimalPath, AppConfig.SafeBootNetworkPath })
        {
            var row = Assert.Single(report.Items, i => i.Target.EndsWith(path, StringComparison.Ordinal));
            Assert.Equal("KEEP", row.Action);
            Assert.Equal("NvmeDisk", row.Before);
            Assert.Equal("NvmeDisk", row.After);
            Assert.Contains("Windows owns and write-protects this key", row.Note, StringComparison.Ordinal);
        }
        Assert.Equal(1, report.TotalWrites);   // the override only
        Assert.Equal(2, report.TotalCreates);  // the two nvmedisk service-name keys
    }

    [Fact]
    public void WritableSafeBootKeyHoldingWindowsValue_IsAWriteOverItsExistingValue()
    {
        // The same "NvmeDisk" default in a key this tool may write (not TrustedInstaller-owned).
        var config = new AppConfig { PatchProfile = PatchProfile.Safe, IncludeServerKey = false };
        var report = DryRunService.PlanInstall(config, null, [], (path, _) =>
            path.EndsWith(AppConfig.SafeBootGuid, StringComparison.OrdinalIgnoreCase)
                ? new DryRunService.CurrentRegistryValue(true, "NvmeDisk")
                : new DryRunService.CurrentRegistryValue(false, null));

        var minimal = Assert.Single(report.Items, i => i.Target.EndsWith(AppConfig.SafeBootMinimalPath, StringComparison.Ordinal));
        Assert.Equal("WRITE", minimal.Action);
        Assert.Equal("NvmeDisk", minimal.Before);
        Assert.Equal(AppConfig.SafeBootValue, minimal.After);
        Assert.Contains("removal puts back", minimal.Note, StringComparison.Ordinal);
        Assert.Equal(3, report.TotalWrites);   // the override plus both existing GUID keys
        Assert.Equal(2, report.TotalCreates);  // the two nvmedisk service-name keys
    }

    [Fact]
    public void ServerKey_AddsOneMoreWrite()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe, IncludeServerKey = true };
        var report = DryRunService.PlanInstall(config, null, [], CleanMachine);
        Assert.Equal(2, report.TotalWrites);
        Assert.Contains(report.Items, i => i.ValueName == AppConfig.ServerFeatureID);
    }

    [Fact]
    public void VeraCryptBlocker_PropagatesAsPreflightBlocker()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe };
        var preflight = new PreflightResult { VeraCryptDetected = true };
        var report = DryRunService.PlanInstall(config, preflight);
        Assert.Single(report.PreflightBlockers);
        Assert.Contains("VeraCrypt", report.PreflightBlockers[0]);
    }

    [Fact]
    public void UnknownCriticalProbe_PropagatesAsPreflightBlocker()
    {
        var preflight = new PreflightResult();
        preflight.CriticalProbes.Items.Add(new CriticalProbeResult
        {
            Id = "IntelStorage",
            Label = "Intel RST/VMD",
            Verdict = CriticalProbeVerdict.Unknown,
            ReasonCode = CriticalProbeReasonCode.Timeout,
            Detail = "Driver query timed out.",
            ObservedAtUtc = DateTimeOffset.UtcNow
        });

        var report = DryRunService.PlanInstall(new AppConfig(), preflight);

        Assert.Single(report.PreflightBlockers);
        Assert.Contains("Unknown [Timeout]", report.PreflightBlockers[0]);
    }

    [Fact]
    public void Markdown_IncludesAllRegistryItems()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe };
        var report = DryRunService.PlanInstall(config, preflight: null);
        var md = DryRunService.RenderMarkdown(report);
        Assert.Contains("| Action | Target | Value |", md);
        foreach (var item in report.Items)
            Assert.Contains(item.ValueName, md);
    }

    [Fact]
    public void Preview_ListsRegistryOverrideIdsBesideTheBranchFeatureStoreIds()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Full };
        var preflight = new PreflightResult
        {
            BuildDetails = new WindowsBuildDetails { BuildNumber = 26404, UBR = 5000 }
        };

        var report = DryRunService.PlanInstall(config, preflight);
        Assert.NotNull(report.RegistryOverrideAssessment);
        var assessment = report.RegistryOverrideAssessment!;

        Assert.Equal(3, assessment.Features.Count);
        Assert.Contains("Registry Override IDs", DryRunService.RenderMarkdown(report));
        Assert.DoesNotContain("MISMATCH", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MISMATCH", DryRunService.RenderMarkdown(report), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("55369237", DryRunService.RenderMarkdown(report));
    }
}
