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
    public void FullProfile_Plans_TwoWrites_FourSafeBootCreates_WithoutStandaloneFuture()
    {
        // #19: Full alone no longer writes 156965516.
        var config = new AppConfig { PatchProfile = PatchProfile.Full, IncludeServerKey = false };
        var report = DryRunService.PlanInstall(config, null, [], CleanMachine);

        Assert.Equal(2, report.TotalWrites);
        Assert.Equal(4, report.TotalCreates);
        Assert.Contains(report.Items, i => i.Action == "WRITE" && i.ValueName == AppConfig.PrimaryFeatureID);
        Assert.Contains(report.Items, i => i.Action == "WRITE" && i.ValueName == "1853569164");
        Assert.DoesNotContain(report.Items, i => i.ValueName == AppConfig.StandaloneFutureFeatureID);
        Assert.DoesNotContain("156965516", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FullProfile_WithStandaloneFutureOptIn_Plans_ThreeWrites()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Full, IncludeStandaloneFuture = true };
        var report = DryRunService.PlanInstall(config, null, [], CleanMachine);

        Assert.Equal(3, report.TotalWrites);
        foreach (var id in AppConfig.FeatureIDs)
            Assert.Contains(report.Items, i => i.Action == "WRITE" && i.ValueName == id);
        Assert.Contains("+ 156965516", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeProfile_IgnoresAStaleStandaloneFutureOptIn()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe, IncludeStandaloneFuture = true };
        var report = DryRunService.PlanInstall(config, null, [], CleanMachine);

        Assert.False(report.IncludeStandaloneFuture);
        Assert.Equal(1, report.TotalWrites);
        Assert.DoesNotContain(report.Items, i => i.ValueName == AppConfig.StandaloneFutureFeatureID);
    }

    [Theory]
    [InlineData(PatchProfile.Safe, false, false)]
    [InlineData(PatchProfile.Full, true, false)]
    [InlineData(PatchProfile.Full, true, true)]
    public void Preview_ListsExactlyWhatApplyWrites(PatchProfile profile, bool includeServer, bool standaloneFuture)
    {
        // The preview used to hardcode two SafeBoot rows while apply wrote four. Pin it to the
        // same list apply commits, mirrors included.
        string[] mirrors = ["ControlSet002"];
        var config = new AppConfig { PatchProfile = profile, IncludeServerKey = includeServer, IncludeStandaloneFuture = standaloneFuture };
        var report = DryRunService.PlanInstall(config, null, mirrors, CleanMachine);
        var applies = PatchService.BuildRequiredRegistryMutations(profile, includeServer, mirrors, standaloneFuture);

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
        // The old uninstall preview listed these keys as whole-key deletes (the #13 pattern).
        Assert.DoesNotContain(report.Items, i => i.Action == "DELETE" && i.Target.Contains(@"\SafeBoot\", StringComparison.OrdinalIgnoreCase));
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
        // The opt-in puts all three override IDs in the plan, so the assessment covers each one.
        var config = new AppConfig { PatchProfile = PatchProfile.Full, IncludeStandaloneFuture = true };
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

    // A machine an earlier Full apply of this tool patched: its three values set in every Overrides
    // key, with a reusable ledger baseline that had them absent.
    private static DryRunService.CurrentRegistryValue AfterFullApply(string path, string valueName) =>
        valueName is AppConfig.PrimaryFeatureID or "1853569164" or AppConfig.StandaloneFutureFeatureID &&
        path.EndsWith(@"FeatureManagement\Overrides", StringComparison.OrdinalIgnoreCase)
            ? new DryRunService.CurrentRegistryValue(true, 1)
            : new DryRunService.CurrentRegistryValue(false, null);

    private static List<RegistryValueBaseline> AbsentBaseline(string[] mirrors) =>
        MutationLedgerService.FeatureOverrideSubKeys(mirrors)
            .SelectMany(subKey => AppConfig.OwnedOverrideValueNames.Select(id =>
                new RegistryValueBaseline { KeyPath = subKey, ValueName = id, Existed = false }))
            .ToList();

    [Theory]
    [InlineData(PatchProfile.Safe, false, new[] { "1853569164", "156965516" })]   // Safe after Full
    [InlineData(PatchProfile.Safe, true, new[] { "1853569164", "156965516" })]    // the opt-in only counts with Full
    [InlineData(PatchProfile.Full, false, new[] { "156965516" })]                 // #19
    public void ValuesAnEarlierApplyWrote_ThisApplyDoesNotWrite_AreDeleteRows(PatchProfile profile, bool optIn, string[] cleared)
    {
        string[] mirrors = ["ControlSet002"];
        var config = new AppConfig { PatchProfile = profile, IncludeStandaloneFuture = optIn };
        var report = DryRunService.PlanInstall(config, null, mirrors, AfterFullApply, AbsentBaseline(mirrors));

        var deletes = report.Items.Where(i => i.Action == "DELETE").ToList();
        Assert.Equal(
            MutationLedgerService.FeatureOverrideSubKeys(mirrors)
                .SelectMany(subKey => cleared.Select(id => $@"HKEY_LOCAL_MACHINE\{subKey}|{id}")).Order(),
            deletes.Select(i => $"{i.Target}|{i.ValueName}").Order());
        Assert.All(deletes, row =>
        {
            Assert.Equal("1", row.Before);
            Assert.Equal("(absent)", row.After);
        });
        int expected = cleared.Length * 2;
        Assert.Equal(expected, report.TotalDeletes);
        Assert.Contains($"{expected} leftover value(s) cleared", report.Summary, StringComparison.Ordinal);
        Assert.Contains("DELETE", DryRunService.RenderMarkdown(report), StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesAnOlderVersionWrote_AreDeleteRowsThatSaySo()
    {
        // A v5.1.0 baseline captured over a v5.0.0 Full patch holds all three flags.
        string[] mirrors = ["ControlSet002"];
        var baseline = AbsentBaseline(mirrors);
        foreach (var entry in baseline.Where(b => AppConfig.FeatureIDs.Contains(b.ValueName)))
            entry.Existed = true;
        var config = new AppConfig { PatchProfile = PatchProfile.Safe };
        var report = DryRunService.PlanInstall(config, null, mirrors, AfterFullApply, baseline);

        var deletes = report.Items.Where(i => i.Action == "DELETE").ToList();
        Assert.Equal(4, deletes.Count);   // 1853569164 and 156965516 in both Overrides keys
        Assert.All(deletes, row => Assert.Contains("version before 5.1.0", row.Note, StringComparison.Ordinal));
        Assert.Equal(4, report.TotalDeletes);
        Assert.DoesNotContain(report.Items, i => i.Action == "KEEP" && i.Target.Contains("Overrides", StringComparison.Ordinal));
    }

    [Fact]
    public void LedgerLocked_PreviewWarnsInsteadOfGuessingWhoWroteTheLeftovers()
    {
        var config = new AppConfig { PatchProfile = PatchProfile.Safe };
        var report = DryRunService.PlanInstall(config, null, ["ControlSet002"], AfterFullApply, priorBaseline: null, ledgerBusy: true);

        Assert.Equal(0, report.TotalDeletes);
        Assert.DoesNotContain(report.Items, i => i.Action == "KEEP" && i.Target.Contains("Overrides", StringComparison.Ordinal));
        Assert.Contains(DryRunService.LedgerBusyWarning, report.PreflightWarnings);
        Assert.Contains("1 warning(s)", report.Summary, StringComparison.Ordinal);
        Assert.Contains("mutation ledger is in use", DryRunService.RenderMarkdown(report), StringComparison.Ordinal);
        Assert.True(DryRunService.LedgerLockTimeout <= TimeSpan.FromSeconds(5), "a preview on the UI thread can't wait longer than that");
    }

    [Fact]
    public void ValuesThatPredateTheFirstApply_AreKeepRowsNotDeletes()
    {
        // No ledger to reuse: apply captures a fresh baseline, so whatever is set now stays.
        var config = new AppConfig { PatchProfile = PatchProfile.Safe };
        var report = DryRunService.PlanInstall(config, null, ["ControlSet002"], AfterFullApply, priorBaseline: null);

        Assert.Equal(0, report.TotalDeletes);
        Assert.DoesNotContain("leftover", report.Summary, StringComparison.Ordinal);
        var keeps = report.Items.Where(i => i.Action == "KEEP" && i.Target.Contains("Overrides", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, keeps.Count);   // 1853569164 and 156965516 in both Overrides keys
        Assert.All(keeps, row =>
        {
            Assert.Equal("1", row.After);
            Assert.Contains("before this tool's first apply", row.Note, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void FullWithTheOptIn_WritesInsteadOfClearing()
    {
        string[] mirrors = ["ControlSet002"];
        var config = new AppConfig { PatchProfile = PatchProfile.Full, IncludeStandaloneFuture = true };
        var report = DryRunService.PlanInstall(config, null, mirrors, AfterFullApply, AbsentBaseline(mirrors));

        Assert.DoesNotContain(report.Items, i => i.Action is "DELETE" or "KEEP" && i.Target.Contains("Overrides", StringComparison.Ordinal));
        Assert.Equal(0, report.TotalDeletes);
        Assert.Contains(report.Items, i => i.ValueName == AppConfig.StandaloneFutureFeatureID && i.Before == "1");
    }

    [Fact]
    public void CleanMachine_HasNoDeleteRows()
    {
        string[] mirrors = ["ControlSet002"];
        var report = DryRunService.PlanInstall(new AppConfig { PatchProfile = PatchProfile.Full }, null, mirrors, CleanMachine, AbsentBaseline(mirrors));
        Assert.Equal(0, report.TotalDeletes);
        Assert.DoesNotContain("leftover", report.Summary, StringComparison.Ordinal);
    }
}
