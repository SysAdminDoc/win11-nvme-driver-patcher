using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// On the Windows fallback path the readings come back as "N/A". The parser turned that into 0, so
/// the gauges showed a missing temperature in green and missing life remaining in red.
/// </summary>
public sealed class TelemetryMetricColorTests
{
    [Theory]
    [InlineData("N/A", "TextMuted")]
    [InlineData("", "TextMuted")]
    [InlineData(null, "TextMuted")]
    [InlineData("38 °C", "Green")]
    [InlineData("55 °C", "Yellow")]
    [InlineData("71 °C", "Red")]
    public void Temperature_IsGradedOnlyWhenThereIsAReading(string? reading, string expectedBrush) =>
        Assert.Equal(expectedBrush, TelemetryView.TemperatureBrushKey(reading));

    [Theory]
    [InlineData("N/A", "TextMuted")]
    [InlineData("95%", "Green")]
    [InlineData("40%", "Yellow")]
    [InlineData("0%", "Red")]
    public void LifeRemaining_IsGradedOnlyWhenThereIsAReading(string? reading, string expectedBrush) =>
        Assert.Equal(expectedBrush, TelemetryView.LifeRemainingBrushKey(reading));
}
