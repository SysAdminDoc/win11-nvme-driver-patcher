using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PatchVerificationServiceTests
{
    [Fact]
    public void Evaluate_NoPendingTimestamp_ReturnsNone()
    {
        // Default AppConfig has a null PendingVerificationSince — the common case where the
        // user has never applied a patch (or has already verified the most recent one).
        // Must short-circuit before touching WMI / registry so this path is safe to run
        // in any environment, including CI agents without admin.
        var config = new AppConfig { PendingVerificationSince = null };

        var report = PatchVerificationService.Evaluate(config);

        Assert.Equal(VerificationOutcome.None, report.Outcome);
        Assert.Null(report.PatchAppliedAt);
        Assert.Null(report.LastBootAt);
    }

    [Fact]
    public void Evaluate_WhitespaceOnlyTimestamp_TreatedAsNoPending()
    {
        // Accidental whitespace in the saved config shouldn't crash or trigger the StalePending
        // error message. Treat it identically to a null flag.
        var config = new AppConfig { PendingVerificationSince = "   \t\n  " };

        var report = PatchVerificationService.Evaluate(config);

        Assert.Equal(VerificationOutcome.None, report.Outcome);
    }

    [Fact]
    public void Evaluate_InvalidPendingTimestampIsReportedAsStalePending()
    {
        var config = new AppConfig
        {
            PendingVerificationSince = "definitely-not-a-timestamp"
        };

        var report = PatchVerificationService.Evaluate(config);

        Assert.Equal(VerificationOutcome.StalePending, report.Outcome);
        Assert.Contains("invalid", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clearing", report.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_TimestampOlderThanMaxAge_ReturnsStalePending()
    {
        // 45 days in the past comfortably exceeds the 30-day PendingMaxAge, so Evaluate
        // should short-circuit before touching the registry or WMI. Exercises the time-box
        // branch added in v4.3.1.
        var fortyFiveDaysAgo = DateTime.UtcNow.AddDays(-45).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var config = new AppConfig { PendingVerificationSince = fortyFiveDaysAgo };

        var report = PatchVerificationService.Evaluate(config);

        Assert.Equal(VerificationOutcome.StalePending, report.Outcome);
        Assert.NotNull(report.PatchAppliedAt);
        Assert.Contains("too old", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_TimestampWithinMaxAge_DoesNotShortCircuitAsStale()
    {
        // A fresh pending flag should NOT fall into the StalePending bucket. The rest of the
        // evaluation path depends on WMI/registry state we can't mock cleanly here — but we
        // can assert the stale time-box didn't trigger, which covers the boundary condition.
        var oneHourAgo = DateTime.UtcNow.AddHours(-1).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var config = new AppConfig { PendingVerificationSince = oneHourAgo };

        var report = PatchVerificationService.Evaluate(config);

        Assert.NotEqual(VerificationOutcome.StalePending, report.Outcome);
        // PatchAppliedAt should be populated even if we can't assert the final Outcome
        // (environment-dependent past this point).
        Assert.NotNull(report.PatchAppliedAt);
    }

    [Fact]
    public void Evaluate_LocalTimestampIsNormalizedToUtc()
    {
        // v4.3.1 force-coerces parsed timestamps to UTC so downstream comparisons can't
        // silently mix zones. This test pins that behavior: a round-tripped LOCAL time far
        // enough in the past should still land in the stale bucket regardless of the host
        // machine's time zone offset.
        var local = DateTime.Now.AddDays(-60);
        var localIso = DateTime.SpecifyKind(local, DateTimeKind.Local)
            .ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var config = new AppConfig { PendingVerificationSince = localIso };

        var report = PatchVerificationService.Evaluate(config);

        Assert.Equal(VerificationOutcome.StalePending, report.Outcome);
    }

    [Fact]
    public void MarkPending_SetsIsoTimestampAndClearsLastVerified()
    {
        var config = new AppConfig
        {
            LastVerifiedProfile = "Safe",
            LastVerificationResult = "Confirmed"
        };

        PatchVerificationService.MarkPending(config);

        Assert.False(string.IsNullOrWhiteSpace(config.PendingVerificationSince));
        // Must round-trip via the same ISO-8601 roundtrip format MarkPending uses, so
        // Evaluate can read it back later without a culture/format mismatch.
        Assert.True(DateTime.TryParse(
            config.PendingVerificationSince,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out _));
        Assert.Equal("Safe", config.PendingVerificationProfile);
        Assert.Null(config.LastVerifiedProfile);
        Assert.Null(config.LastVerificationResult);
    }

    [Fact]
    public void Clear_RecordsProfileAndOutcome()
    {
        var config = new AppConfig
        {
            PendingVerificationSince = DateTime.UtcNow.ToString("o"),
            PatchProfile = PatchProfile.Full
        };
        var report = new VerificationReport { Outcome = VerificationOutcome.Confirmed };

        PatchVerificationService.Clear(config, report);

        Assert.Null(config.PendingVerificationSince);
        Assert.Equal("Full", config.LastVerifiedProfile);
        Assert.Equal("Confirmed", config.LastVerificationResult);
    }

    [Fact]
    public void Clear_RecordsPendingProfileEvenIfCurrentSettingChanged()
    {
        var config = new AppConfig
        {
            PendingVerificationSince = DateTime.UtcNow.ToString("o"),
            PendingVerificationProfile = "Safe",
            PatchProfile = PatchProfile.Full
        };
        var report = new VerificationReport { Outcome = VerificationOutcome.Confirmed };

        PatchVerificationService.Clear(config, report);

        Assert.Null(config.PendingVerificationSince);
        Assert.Null(config.PendingVerificationProfile);
        Assert.Equal("Safe", config.LastVerifiedProfile);
        Assert.Equal("Confirmed", config.LastVerificationResult);
    }
    // --- ClassifyPostRebootState truth table (pure, no WMI/registry) ---

    [Theory]
    // Driver bound wins regardless of keys/evidence — Confirmed.
    [InlineData(true, 3, false, VerificationOutcome.Confirmed)]
    [InlineData(true, 0, false, VerificationOutcome.Confirmed)]
    [InlineData(true, 3, true, VerificationOutcome.Confirmed)]
    [InlineData(true, 0, true, VerificationOutcome.Confirmed)]
    // Not bound + fallback evidence — the fallback itself failed (ViVe #164), with or
    // without registry keys still present.
    [InlineData(false, 3, true, VerificationOutcome.FlagsEnabledNotBound)]
    [InlineData(false, 0, true, VerificationOutcome.FlagsEnabledNotBound)]
    // Not bound + no evidence: keys gone → Reverted; keys present → OverrideBlocked.
    [InlineData(false, 0, false, VerificationOutcome.Reverted)]
    [InlineData(false, 3, false, VerificationOutcome.OverrideBlocked)]
    public void ClassifyPostRebootState_TruthTable(
        bool nativeActive, int keyCount, bool fallbackEvidence, VerificationOutcome expected)
    {
        var (outcome, summary, detail) = PatchVerificationService.ClassifyPostRebootState(
            nativeActive, "nvmedisk.sys", keyCount, fallbackEvidence);
        Assert.Equal(expected, outcome);
        Assert.False(string.IsNullOrWhiteSpace(summary));
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Fact]
    public void ClassifyPostRebootState_FlagsEnabledNotBound_DoesNotSuggestFallback()
    {
        // Re-suggesting ViVeTool when the fallback is exactly what failed would be a lie.
        var (_, _, detail) = PatchVerificationService.ClassifyPostRebootState(
            nativeActive: false, activeDriver: "stornvme.sys", overrideKeyCount: 2, fallbackEvidence: true);
        Assert.DoesNotContain("try the ViVeTool fallback", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no working enablement path", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyPostRebootState_ConfirmedWithoutKeys_ExplainsEnablementSource()
    {
        // Fallback-only or official enablement must not read as "Reverted".
        var (outcome, _, detail) = PatchVerificationService.ClassifyPostRebootState(
            nativeActive: true, activeDriver: "nvmedisk.sys", overrideKeyCount: 0, fallbackEvidence: false);
        Assert.Equal(VerificationOutcome.Confirmed, outcome);
        Assert.Contains("No registry override keys", detail);
    }

    // --- Known bind-blocked build gate ---

    [Theory]
    [InlineData(26100, 8655, false)]  // stable 24H2-era — not affected
    [InlineData(26200, 8246, false)]  // 25H2 before the reported UBR — not flagged
    [InlineData(26200, 8524, true)]   // first reported affected build (ViVe #164)
    [InlineData(26200, 9000, true)]   // later 25H2 UBRs
    [InlineData(28020, 1, true)]      // newer Insider trains carry the change forward
    public void IsKnownBindBlockedBuild_MatchesReportedSignature(int build, int ubr, bool expected)
    {
        Assert.Equal(expected, Models.AppConfig.IsKnownBindBlockedBuild(build, ubr));
    }
}
