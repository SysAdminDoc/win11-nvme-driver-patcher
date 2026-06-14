using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class CompatTelemetryServiceTests
{
    // --- CPU sanitizer: strip stepping/revision entropy as the privacy docs promise ---

    [Fact]
    public void SanitizeCpu_StripsStepping()
    {
        Assert.Equal(
            "Intel64 Family 6 Model 154, GenuineIntel",
            CompatTelemetryService.SanitizeCpu("Intel64 Family 6 Model 154 Stepping 3, GenuineIntel"));
    }

    [Fact]
    public void SanitizeCpu_StripsStepping_Amd()
    {
        Assert.Equal(
            "AMD64 Family 25 Model 33, AuthenticAMD",
            CompatTelemetryService.SanitizeCpu("AMD64 Family 25 Model 33 Stepping 0, AuthenticAMD"));
    }

    [Theory]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    // No stepping token — passes through unchanged (trimmed).
    [InlineData("Intel64 Family 6 Model 154, GenuineIntel", "Intel64 Family 6 Model 154, GenuineIntel")]
    public void SanitizeCpu_HandlesEdgeCases(string input, string expected)
    {
        Assert.Equal(expected, CompatTelemetryService.SanitizeCpu(input));
    }

    [Fact]
    public void SanitizeCpu_ResultNeverContainsStepping()
    {
        var sanitized = CompatTelemetryService.SanitizeCpu("Intel64 Family 6 Model 154 Stepping 12, GenuineIntel");
        Assert.DoesNotContain("Stepping", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    // --- Endpoint validation: HTTPS-only remote, loopback HTTP allowed for dev ---

    [Theory]
    [InlineData("https://telemetry.example.com/nvme/compat")]
    [InlineData("https://localhost/nvme/compat")]
    public void TryValidateEndpoint_AllowsHttps(string endpoint)
    {
        Assert.True(CompatTelemetryService.TryValidateEndpoint(endpoint, out var uri, out var error));
        Assert.NotNull(uri);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("http://localhost:8080/nvme/compat")]
    [InlineData("http://127.0.0.1/nvme/compat")]
    [InlineData("http://[::1]/nvme/compat")]
    public void TryValidateEndpoint_AllowsLoopbackHttp(string endpoint)
    {
        Assert.True(CompatTelemetryService.TryValidateEndpoint(endpoint, out _, out _));
    }

    [Fact]
    public void TryValidateEndpoint_RejectsRemoteHttp()
    {
        Assert.False(CompatTelemetryService.TryValidateEndpoint(
            "http://telemetry.example.com/nvme/compat", out var uri, out var error));
        Assert.Null(uri);
        Assert.Contains("HTTP", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/x")]
    [InlineData("/relative/path")]
    public void TryValidateEndpoint_RejectsEmptyOrMalformedOrNonHttp(string? endpoint)
    {
        Assert.False(CompatTelemetryService.TryValidateEndpoint(endpoint, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
