using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PatchVerificationServiceTests
{
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
}
