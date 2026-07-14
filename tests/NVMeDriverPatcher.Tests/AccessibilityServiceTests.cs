using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class AccessibilityServiceTests
{
    [Theory]
    [InlineData(true, null, false)]
    [InlineData(false, null, true)]
    [InlineData(true, "0", false)]
    [InlineData(false, "1", true)]
    [InlineData(null, "0", true)]
    [InlineData(null, "1", false)]
    [InlineData(null, null, false)]
    public void ResolveReducedMotion_PrefersClientAreaAnimationAndFallsBackToLegacyValue(
        bool? clientAreaAnimationsEnabled,
        string? minAnimate,
        bool expectedReducedMotion)
    {
        Assert.Equal(
            expectedReducedMotion,
            AccessibilityService.ResolveReducedMotion(clientAreaAnimationsEnabled, minAnimate));
    }
}
