using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class MotionPreferenceServiceTests
{
    [Fact]
    public void Resolve_AnimationsEnabled_PreservesNormalMotion()
    {
        var state = MotionPreferenceService.Resolve(clientAreaAnimationsEnabled: true);

        Assert.True(state.AnimationsEnabled);
        Assert.Equal(PopupAnimation.Slide, state.PopupAnimation);
    }

    [Fact]
    public void Resolve_ReducedMotion_UsesImmediateStablePresentation()
    {
        var state = MotionPreferenceService.Resolve(clientAreaAnimationsEnabled: false);

        Assert.False(state.AnimationsEnabled);
        Assert.Equal(PopupAnimation.None, state.PopupAnimation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyToResources_PublishesEveryDynamicTemplateValue(bool animationsEnabled)
    {
        var resources = new ResourceDictionary();
        var state = MotionPreferenceService.Resolve(animationsEnabled);

        MotionPreferenceService.ApplyToResources(resources, state);

        Assert.Equal(animationsEnabled, resources[MotionPreferenceService.AnimationsEnabledKey]);
        Assert.Equal(state.PopupAnimation, resources[MotionPreferenceService.PopupAnimationKey]);
    }

    [Fact]
    public void SharedTheme_UsesLiveMotionResourcesForEveryAnimationClass()
    {
        var theme = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Themes", "DarkTheme.xaml"));

        Assert.Contains("{DynamicResource MotionAnimationsEnabled}", theme);
        Assert.Contains("{DynamicResource MotionPopupAnimation}", theme);
        Assert.DoesNotContain("PopupAnimation=\"Slide\"", theme);
        Assert.Contains("StopStoryboard", theme);
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
