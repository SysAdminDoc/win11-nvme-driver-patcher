using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class DocsServiceTests
{
    [Theory]
    [InlineData("overview")]
    [InlineData("profiles")]
    [InlineData("recovery")]
    [InlineData("watchdog")]
    [InlineData("vivetool")]
    [InlineData("firmware")]
    [InlineData("gpo")]
    [InlineData("portable")]
    [InlineData("telemetry")]
    [InlineData("uninstall")]
    public void EveryDocumentedTopic_HasContent(string topic)
    {
        var text = DocsService.Render(topic);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("# " + topic, text);
    }

    [Fact]
    public void UnknownTopic_FallsBackToIndex()
    {
        var text = DocsService.Render("nonexistent");
        Assert.Contains("Unknown topic", text);
        Assert.Contains("Available topics", text);
    }

    [Fact]
    public void EmptyTopic_RendersIndex()
    {
        var text = DocsService.Render("");
        Assert.Contains("Available topics", text);
    }

    [Fact]
    public void TopicNamesAreCaseInsensitive()
    {
        var upper = DocsService.Render("WATCHDOG");
        Assert.Contains("watchdog", upper, StringComparison.OrdinalIgnoreCase);
    }
}
