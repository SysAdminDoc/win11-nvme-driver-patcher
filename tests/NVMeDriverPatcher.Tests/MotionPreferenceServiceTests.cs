using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
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

    [Fact]
    public void SharedTheme_ProvidesFailSafeKeyboardFocusVisual()
    {
        var document = XDocument.Load(ThemePath());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = document.Descendants(presentation + "Style")
            .Single(element => (string?)element.Attribute(x + "Key") == "KeyboardFocusVisual");
        var border = Assert.Single(style.Descendants(presentation + "Border"));

        Assert.Equal("{DynamicResource Focus}", (string?)border.Attribute("BorderBrush"));
        Assert.Equal("1", (string?)border.Attribute("BorderThickness"));
        Assert.DoesNotContain("FocusHidden", File.ReadAllText(ThemePath()), StringComparison.Ordinal);
    }

    [Fact]
    public void IndeterminateProgress_UsesTrackRelativeSweepAndStableReducedState()
    {
        var document = XDocument.Load(ThemePath());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var template = document.Descendants(presentation + "ControlTemplate")
            .Single(element => (string?)element.Attribute(x + "Key") == "RoundProgressIndeterminate");
        var slider = template.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(x + "Name") == "Slider");
        var animation = template.Descendants(presentation + "DoubleAnimation")
            .Single(element => (string?)element.Attribute("Storyboard.TargetName") == "SliderTranslate");

        Assert.Null(slider.Attribute("Width"));
        Assert.NotNull(slider.Element(presentation + "Border.OpacityMask")?
            .Descendants(presentation + "LinearGradientBrush.RelativeTransform").SingleOrDefault());
        Assert.Equal("-1", (string?)animation.Attribute("From"));
        Assert.Equal("1", (string?)animation.Attribute("To"));

        var reducedTrigger = template.Descendants(presentation + "DataTrigger")
            .Single(element => (string?)element.Attribute("Value") == "False");
        Assert.Contains(
            reducedTrigger.Descendants(presentation + "Setter"),
            setter => (string?)setter.Attribute("TargetName") == "Slider" &&
                      (string?)setter.Attribute("Property") == "OpacityMask" &&
                      (string?)setter.Attribute("Value") == "{x:Null}");
    }

    private static string ThemePath() =>
        Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Themes", "DarkTheme.xaml");

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
