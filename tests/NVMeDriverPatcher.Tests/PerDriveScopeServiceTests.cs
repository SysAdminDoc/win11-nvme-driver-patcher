using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PerDriveScopeServiceTests
{
    [Fact]
    public void ScopeDisabled_ReportsEveryDriveInGlobalScope()
    {
        var decisions = PerDriveScopeService.Decide(
            new[] { ("SN-1", "Samsung 990 PRO"), ("SN-2", "WD SN850X") },
            new PerDriveScopeConfig { Enabled = false });

        Assert.All(decisions, decision => Assert.True(decision.Include));
        Assert.All(decisions, decision => Assert.Contains("Global", decision.Reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LegacySerialExclusion_IsDetectedButNeverEnforced()
    {
        var scope = new PerDriveScopeConfig { Enabled = true, ExcludedSerials = { "SN-1" } };

        var decisions = PerDriveScopeService.Decide(
            new[] { ("SN-1", "Samsung 990 PRO"), ("SN-2", "WD SN850X") },
            scope);

        Assert.All(decisions, decision => Assert.True(decision.Include));
        Assert.True(decisions[0].LegacyExclusionRequested);
        Assert.Contains("never enforced", decisions[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(decisions[1].LegacyExclusionRequested);
    }

    [Fact]
    public void LegacyModelExclusion_IsDetectedButNeverEnforced()
    {
        var scope = new PerDriveScopeConfig { Enabled = true, ExcludedModelPatterns = { "WD" } };

        var decisions = PerDriveScopeService.Decide(
            new[] { ("SN-1", "Samsung 990 PRO"), ("SN-2", "WD_BLACK SN850X") },
            scope);

        Assert.All(decisions, decision => Assert.True(decision.Include));
        Assert.True(decisions[1].LegacyExclusionRequested);
        Assert.Contains("model pattern", decisions[1].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerialLegacyIntent_RemainsTheMoreSpecificExplanation()
    {
        var scope = new PerDriveScopeConfig
        {
            Enabled = true,
            ExcludedSerials = { "SN-2" },
            ExcludedModelPatterns = { "WD" }
        };

        var decision = Assert.Single(PerDriveScopeService.Decide(new[] { ("SN-2", "WD SN850X") }, scope));

        Assert.True(decision.Include);
        Assert.True(decision.LegacyExclusionRequested);
        Assert.Contains("serial", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_WarnsThatRequestedExclusionsRemainGlobal()
    {
        var scope = new PerDriveScopeConfig { Enabled = true, ExcludedSerials = { "SN-1" } };
        var decisions = PerDriveScopeService.Decide(
            new[] { ("SN-1", "A"), ("SN-2", "B"), ("SN-3", "C") },
            scope);

        var summary = PerDriveScopeService.Summarize(decisions, scope);

        Assert.Contains("never enforced", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("machine-wide", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("all 3", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("will swap", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingLegacyFile_IsReportedEvenWhenItRequestedNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMePatcher.ScopeTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "drive_scope.json"), "{ \"Enabled\": false }");
            var config = new AppConfig { WorkingDir = dir };

            var scope = PerDriveScopeService.Load(config);
            var summary = PerDriveScopeService.Summarize([], scope);

            Assert.True(scope.LegacyFilePresent);
            Assert.Contains("legacy drive_scope.json was detected", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not enforced", summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UnreadableLegacyFile_IsReportedAndNeverNarrowsScope()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMePatcher.ScopeTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "drive_scope.json"), "{ invalid json");
            var config = new AppConfig { WorkingDir = dir };

            var scope = PerDriveScopeService.Load(config);
            var decisions = PerDriveScopeService.Decide(new[] { ("SN-1", "Samsung 990 PRO") }, scope);
            var summary = PerDriveScopeService.Summarize(decisions, scope);

            Assert.True(scope.LegacyFilePresent);
            Assert.False(string.IsNullOrWhiteSpace(scope.LoadError));
            Assert.True(Assert.Single(decisions).Include);
            Assert.Contains("could not be read", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("machine-wide", summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
