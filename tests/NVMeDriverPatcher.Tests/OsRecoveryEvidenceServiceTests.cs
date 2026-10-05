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

        Assert.Equal(
            "Point-in-Time Restore is on. The newest restore point is 2 hours old. " +
            "Quick Machine Recovery is off. Its automatic remediation is on.",
            evidence.Summary);
    }

    [Fact]
    public void UnsupportedBuildIsReportedAsAdvisoryUnavailable()
    {
        var evidence = new OsRecoveryEvidence();

        Assert.Equal("This Windows build doesn't offer Point-in-Time Restore.", evidence.PointInTimeRestoreSummary);
        Assert.Equal("This Windows build doesn't offer Quick Machine Recovery.", evidence.QuickMachineRecoverySummary);
        Assert.Equal($"{evidence.PointInTimeRestoreSummary} {evidence.QuickMachineRecoverySummary}", evidence.Summary);
    }

    [Fact]
    public void Summary_ReadsAsPlainSentencesInEveryState()
    {
        // It used to read "OS-native recovery advisory. Point-in-Time Restore: enabled; ...".
        var failures = new List<string>();
        foreach (bool? pitr in new bool?[] { true, false, null })
        foreach (bool querySucceeded in new[] { true, false })
        foreach (var hoursAgo in new double?[] { null, -1, 0.2, 1, 5, 24, 72 })
        foreach (bool? qmr in new bool?[] { true, false, null })
        foreach (bool? auto in new bool?[] { true, false, null })
        foreach (bool qmrQuery in new[] { true, false })
        {
            var summary = new OsRecoveryEvidence
            {
                PointInTimeRestoreSupported = true,
                PointInTimeRestoreEnabled = pitr,
                RestorePointQuerySucceeded = querySucceeded,
                NewestRestorePointUtc = hoursAgo is { } h ? DateTimeOffset.UtcNow.AddHours(-h) : null,
                QuickMachineRecoverySupported = true,
                QuickMachineRecoveryEnabled = qmr,
                QuickMachineRecoveryAutoRemediationEnabled = auto,
                QuickMachineRecoveryQuerySucceeded = qmrQuery,
            }.Summary;

            if (summary.Contains(';') || summary.Contains(':') || summary.Contains("(s)", StringComparison.Ordinal) ||
                summary.Contains("advisory", StringComparison.OrdinalIgnoreCase) ||
                !SentenceRun.IsMatch(summary))
                failures.Add(summary);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct()));
        // Positive control: the old wording must trip the check.
        Assert.DoesNotMatch(SentenceRun, "OS-native recovery advisory. Point-in-Time Restore: enabled; no restore point observed.");
    }

    // Two or more sentences, each capitalized and ending in a period.
    private static readonly System.Text.RegularExpressions.Regex SentenceRun =
        new(@"^[A-Z][^.;:]*[a-z]\.( [A-Z][^.;:]*[a-z]\.)+$");

    [Theory]
    [InlineData(0.5, "30 minutes old")]
    [InlineData(1.01, "1 hour old")]
    [InlineData(25, "1 day old")]
    [InlineData(72.5, "3 days old")]
    public void RestorePointAge_IsPluralizedForPeople(double hoursAgo, string expected)
    {
        var evidence = new OsRecoveryEvidence
        {
            PointInTimeRestoreSupported = true,
            PointInTimeRestoreEnabled = true,
            RestorePointQuerySucceeded = true,
            NewestRestorePointUtc = DateTimeOffset.UtcNow.AddHours(-hoursAgo),
        };

        Assert.Contains($"The newest restore point is {expected}.", evidence.PointInTimeRestoreSummary, StringComparison.Ordinal);
    }
}
