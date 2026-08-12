using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class OsRecoveryEvidenceServiceTests
{
    [Theory]
    [InlineData(26100, 8736, false, true)]
    [InlineData(26100, 8737, true, true)]
    [InlineData(26200, 1, true, true)]
    [InlineData(26000, 9999, false, false)]
    public void BuildGatesFollowDocumentedFeatureThresholds(
        int buildNumber,
        int ubr,
        bool expectedPointInTimeRestore,
        bool expectedQuickMachineRecovery)
    {
        var build = new WindowsBuildDetails { BuildNumber = buildNumber, UBR = ubr };

        Assert.Equal(expectedPointInTimeRestore,
            OsRecoveryEvidenceService.IsPointInTimeRestoreSupported(build));
        Assert.Equal(expectedQuickMachineRecovery,
            OsRecoveryEvidenceService.IsQuickMachineRecoverySupported(build));
    }

    [Fact]
    public void ParsePolicyBoolean_HandlesRegistryRepresentations()
    {
        Assert.True(OsRecoveryEvidenceService.ParsePolicyBoolean(1));
        Assert.False(OsRecoveryEvidenceService.ParsePolicyBoolean(0));
        Assert.True(OsRecoveryEvidenceService.ParsePolicyBoolean(true));
        Assert.False(OsRecoveryEvidenceService.ParsePolicyBoolean("0"));
        Assert.True(OsRecoveryEvidenceService.ParsePolicyBoolean(" true "));
        Assert.Null(OsRecoveryEvidenceService.ParsePolicyBoolean(2));
        Assert.Null(OsRecoveryEvidenceService.ParsePolicyBoolean(null));
    }

    [Fact]
    public void ParseRestorePointCreationTime_HandlesDmtfAndIsoValues()
    {
        var dmtf = OsRecoveryEvidenceService.ParseRestorePointCreationTime(
            "20260812120000.000000+000");
        var iso = OsRecoveryEvidenceService.ParseRestorePointCreationTime(
            "2026-08-12T12:00:00Z");

        Assert.Equal(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero), dmtf);
        Assert.Equal(dmtf, iso);
        Assert.Null(OsRecoveryEvidenceService.ParseRestorePointCreationTime("not-a-timestamp"));
    }

    [Fact]
    public void SummaryIncludesBothAdvisoriesAndLeavesUnknownEvidenceExplicit()
    {
        var evidence = new OsRecoveryEvidence
        {
            PointInTimeRestoreSupported = true,
            PointInTimeRestoreEnabled = true,
            RestorePointQuerySucceeded = true,
            NewestRestorePointUtc = DateTimeOffset.UtcNow.AddHours(-2),
            QuickMachineRecoverySupported = true,
            QuickMachineRecoveryEnabled = false,
            QuickMachineRecoveryAutoRemediationEnabled = true,
            QuickMachineRecoveryQuerySucceeded = true,
        };

        Assert.Contains("Point-in-Time Restore: enabled", evidence.Summary);
        Assert.Contains("newest restore point", evidence.Summary);
        Assert.Contains("Quick Machine Recovery: disabled", evidence.Summary);
        Assert.Contains("auto-remediation enabled", evidence.Summary);
        Assert.Contains("OS-native recovery advisory", evidence.Summary);
    }

    [Fact]
    public void UnsupportedBuildIsReportedAsAdvisoryUnavailable()
    {
        var evidence = new OsRecoveryEvidence();

        Assert.Contains("not exposed", evidence.PointInTimeRestoreSummary);
        Assert.Contains("not exposed", evidence.QuickMachineRecoverySummary);
        Assert.Contains("OS-native recovery advisory", evidence.Summary);
    }
}
