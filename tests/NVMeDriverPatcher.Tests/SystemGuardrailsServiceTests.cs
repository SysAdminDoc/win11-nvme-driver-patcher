using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class SystemGuardrailsServiceTests
{
    private static SystemGuardrailsReport ReportWith(params GuardrailSeverity[] severities)
    {
        var r = new SystemGuardrailsReport();
        foreach (var s in severities)
            r.Findings.Add(new GuardrailFinding { Name = s.ToString(), Severity = s, Detail = "x" });
        return r;
    }

    [Fact]
    public void HasBlocker_TrueOnlyWhenAFindingIsBlocker()
    {
        Assert.False(ReportWith(GuardrailSeverity.Info, GuardrailSeverity.Warning).HasBlocker);
        Assert.True(ReportWith(GuardrailSeverity.Warning, GuardrailSeverity.Blocker).HasBlocker);
    }

    [Fact]
    public void BuildSummary_BlockersTakePrecedenceOverWarnings()
    {
        var summary = SystemGuardrailsService.BuildSummary(ReportWith(GuardrailSeverity.Blocker, GuardrailSeverity.Warning));
        Assert.Contains("1 blocker", summary);
        Assert.Contains("1 warning", summary);
    }

    [Fact]
    public void BuildSummary_WarningsOnly()
    {
        var summary = SystemGuardrailsService.BuildSummary(ReportWith(GuardrailSeverity.Warning, GuardrailSeverity.Warning));
        Assert.Contains("2 guardrail warning", summary);
        Assert.DoesNotContain("blocker", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSummary_CleanWhenNoIssues()
    {
        var summary = SystemGuardrailsService.BuildSummary(ReportWith(GuardrailSeverity.Info, GuardrailSeverity.Info));
        Assert.Contains("No guardrail issues", summary);
    }

    [Fact]
    public void Evaluate_ReturnsLabeledFindings_DoesNotThrow()
    {
        var report = SystemGuardrailsService.Evaluate();
        Assert.NotNull(report);
        Assert.All(report.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Name)));
        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
    }
}
