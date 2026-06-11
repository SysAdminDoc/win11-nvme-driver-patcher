using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class RecoveryProofGateServiceTests
{
    [Fact]
    public void Evaluate_ReturnsExpectedItemCount()
    {
        var config = new AppConfig();
        var report = RecoveryProofGateService.Evaluate(config);
        Assert.Equal(4, report.TotalCount);
    }

    [Fact]
    public void Evaluate_AllItemsHaveLabelsAndDetails()
    {
        var config = new AppConfig();
        var report = RecoveryProofGateService.Evaluate(config);
        foreach (var item in report.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Label), "item label missing");
            Assert.False(string.IsNullOrWhiteSpace(item.Detail), "item detail missing");
        }
    }

    [Fact]
    public void Evaluate_MissingRecoveryKit_FailsRecoveryKitCheck()
    {
        var config = new AppConfig { LastRecoveryKitPath = null };
        var report = RecoveryProofGateService.Evaluate(config);
        var kitItem = report.Items.First(i => i.Label == "Recovery kit");
        Assert.False(kitItem.Passed);
        Assert.Contains("No recovery kit", kitItem.Detail);
    }

    [Fact]
    public void Evaluate_NonexistentKitPath_FailsRecoveryKitCheck()
    {
        var config = new AppConfig { LastRecoveryKitPath = @"C:\NONEXISTENT_PATH_99999" };
        var report = RecoveryProofGateService.Evaluate(config);
        var kitItem = report.Items.First(i => i.Label == "Recovery kit");
        Assert.False(kitItem.Passed);
    }

    [Fact]
    public void Summary_ContainsPassedAndTotal()
    {
        var config = new AppConfig();
        var report = RecoveryProofGateService.Evaluate(config);
        Assert.Contains($"{report.PassedCount}/{report.TotalCount}", report.Summary);
    }

    [Fact]
    public void AllPassed_IsFalse_WhenAnyItemFails()
    {
        var report = new RecoveryProofReport();
        report.Items.Add(new() { Label = "A", Passed = true, Detail = "ok" });
        report.Items.Add(new() { Label = "B", Passed = false, Detail = "fail" });
        Assert.False(report.AllPassed);
        Assert.Equal(1, report.PassedCount);
    }

    [Fact]
    public void AllPassed_IsTrue_WhenAllItemsPass()
    {
        var report = new RecoveryProofReport();
        report.Items.Add(new() { Label = "A", Passed = true, Detail = "ok" });
        report.Items.Add(new() { Label = "B", Passed = true, Detail = "ok" });
        Assert.True(report.AllPassed);
    }

    [Fact]
    public void Summary_WhenAllPassed_DoesNotContainNotReady()
    {
        var report = new RecoveryProofReport();
        report.Items.Add(new() { Label = "A", Passed = true, Detail = "ok" });
        Assert.DoesNotContain("not ready", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_WhenFailing_ListsFailingLabels()
    {
        var report = new RecoveryProofReport();
        report.Items.Add(new() { Label = "Recovery kit", Passed = false, Detail = "missing" });
        report.Items.Add(new() { Label = "SafeBoot", Passed = true, Detail = "ok" });
        Assert.Contains("Recovery kit", report.Summary);
    }

    [Fact]
    public void EvaluateSafeBootEntries_DoesNotThrow()
    {
        var item = RecoveryProofGateService.EvaluateSafeBootEntries();
        Assert.NotNull(item);
        Assert.Equal("SafeBoot entries", item.Label);
        Assert.False(string.IsNullOrWhiteSpace(item.Detail));
    }
}
