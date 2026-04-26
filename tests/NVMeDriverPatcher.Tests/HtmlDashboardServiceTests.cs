using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class HtmlDashboardServiceTests
{
    [Fact]
    public void Render_ProducesValidHtmlSkeleton()
    {
        var html = HtmlDashboardService.Render(
            config: new AppConfig { PatchProfile = PatchProfile.Safe },
            preflight: new PreflightResult(),
            verification: null,
            watchdog: null,
            reliability: null,
            minidump: null,
            guardrails: null,
            controllers: null);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<title>NVMe Driver Patcher", html);
        Assert.Contains("diagnostics snapshot", html);
    }

    [Fact]
    public void Render_UsesAdaptiveThemeCss()
    {
        var html = HtmlDashboardService.Render(
            config: new AppConfig(),
            preflight: new PreflightResult(),
            verification: null,
            watchdog: null,
            reliability: null,
            minidump: null,
            guardrails: null,
            controllers: null);

        Assert.Contains("color-scheme:dark light", html);
        Assert.Contains("prefers-color-scheme:light", html);
        Assert.Contains("prefers-contrast:more", html);
    }

    [Fact]
    public void Render_IncludesWatchdogTableWhenPresent()
    {
        var watchdog = new WatchdogReport
        {
            Verdict = WatchdogVerdict.Warning,
            Summary = "elevated",
            Counts = new List<WatchdogEventCount>
            {
                new() { Source = "storport", Id = 129, Description = "timeout", Count = 3, LatestOccurrence = DateTime.UtcNow }
            }
        };
        var html = HtmlDashboardService.Render(
            new AppConfig(), new PreflightResult(),
            null, watchdog, null, null, null, null);
        Assert.Contains("storport", html);
        Assert.Contains("timeout", html);
    }

    [Fact]
    public void Render_EscapesHtmlHostileCharsInSummaries()
    {
        // A maliciously-crafted summary string shouldn't break the dashboard rendering.
        var verification = new VerificationReport
        {
            Outcome = VerificationOutcome.Confirmed,
            Summary = "ok <script>alert(1)</script>",
            Detail = "detail & stuff"
        };
        var html = HtmlDashboardService.Render(
            new AppConfig(), new PreflightResult(),
            verification, null, null, null, null, null);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("detail &amp; stuff", html);
    }
}
