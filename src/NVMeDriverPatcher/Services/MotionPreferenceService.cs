using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace NVMeDriverPatcher.Services;

internal sealed record MotionPreferenceState(
    bool AnimationsEnabled,
    PopupAnimation PopupAnimation);

/// <summary>
/// Projects Windows' live client-area animation preference into DynamicResource values used by
/// control templates. SystemParameters raises StaticPropertyChanged when the user changes the
/// setting, so an open window switches between normal motion and immediate/stable states.
/// </summary>
public static class MotionPreferenceService
{
    internal const string AnimationsEnabledKey = "MotionAnimationsEnabled";
    internal const string PopupAnimationKey = "MotionPopupAnimation";

    private static bool _initialized;

    public static void Initialize()
    {
        if (!_initialized)
        {
            SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
            _initialized = true;
        }
        Refresh();
    }

    public static void Dispose()
    {
        if (!_initialized) return;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _initialized = false;
    }

    public static void Refresh()
    {
        bool animationsEnabled;
        try { animationsEnabled = SystemParameters.ClientAreaAnimation; }
        catch { animationsEnabled = true; }
        ApplyToApplication(Resolve(animationsEnabled));
    }

    internal static MotionPreferenceState Resolve(bool clientAreaAnimationsEnabled) =>
        clientAreaAnimationsEnabled
            ? new(
                true,
                PopupAnimation.Slide)
            : new(
                false,
                PopupAnimation.None);

    internal static void ApplyToResources(ResourceDictionary resources, MotionPreferenceState state)
    {
        resources[AnimationsEnabledKey] = state.AnimationsEnabled;
        resources[PopupAnimationKey] = state.PopupAnimation;
    }

    private static void ApplyToApplication(MotionPreferenceState state)
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => ApplyToResources(app.Resources, state));
            return;
        }
        ApplyToResources(app.Resources, state);
    }

    private static void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SystemParameters.ClientAreaAnimation), StringComparison.Ordinal))
            Refresh();
    }
}
