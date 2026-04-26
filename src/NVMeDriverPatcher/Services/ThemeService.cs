using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum AppTheme
{
    Light,
    Dark,
    HighContrast
}

public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";
    private const string DarkThemePath = "Themes/DarkTheme.xaml";
    private const string LightThemePath = "Themes/LightTheme.xaml";
    private const string HighContrastThemePath = "Themes/HighContrastTheme.xaml";

    private static bool _listeningForSystemChanges;

    public static AppThemeMode CurrentMode { get; private set; } = AppThemeMode.System;
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;
    public static event EventHandler? ThemeChanged;

    public static void ApplySystemTheme() => ApplyMode(AppThemeMode.System);

    public static void ToggleTheme()
    {
        ApplyMode(CurrentTheme == AppTheme.Dark || CurrentTheme == AppTheme.HighContrast
            ? AppThemeMode.Light
            : AppThemeMode.Dark);
    }

    public static void ApplyTheme(AppTheme theme)
    {
        ApplyMode(theme switch
        {
            AppTheme.Light => AppThemeMode.Light,
            AppTheme.Dark => AppThemeMode.Dark,
            AppTheme.HighContrast => AppThemeMode.HighContrast,
            _ => AppThemeMode.System
        });
    }

    public static void ApplyMode(AppThemeMode mode)
    {
        EnsureSystemPreferenceWatcher();

        mode = NormalizeMode(mode);
        var theme = ResolveTheme(mode);
        CurrentMode = mode;
        ApplyResolvedTheme(theme);
    }

    public static AppThemeMode NormalizeMode(AppThemeMode mode) =>
        Enum.IsDefined(typeof(AppThemeMode), mode) ? mode : AppThemeMode.System;

    public static string GetModeLabel(AppThemeMode mode) =>
        NormalizeMode(mode) switch
        {
            AppThemeMode.Light => "Light",
            AppThemeMode.Dark => "Dark",
            AppThemeMode.HighContrast => "High Contrast",
            _ => "System"
        };

    public static string GetModeDescription(AppThemeMode mode)
    {
        mode = NormalizeMode(mode);
        if (mode == AppThemeMode.System)
        {
            string effective = CurrentTheme == AppTheme.HighContrast
                ? "accessible contrast"
                : CurrentTheme == AppTheme.Dark ? "dark" : "light";
            return $"Follows Windows. Current effective theme: {effective}.";
        }

        return mode switch
        {
            AppThemeMode.Light => "Uses a clean light palette with strong foreground contrast.",
            AppThemeMode.Dark => "Uses the low-glare dark palette.",
            AppThemeMode.HighContrast => "Uses maximum contrast surfaces, borders, and focus states.",
            _ => "Follows Windows."
        };
    }

    private static void ApplyResolvedTheme(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null)
        {
            CurrentTheme = theme;
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ApplyResolvedTheme(theme));
            return;
        }

        var source = LoadTheme(theme);

        // Existing controls may still hold StaticResource brush instances from the
        // previously active dictionary. Recolor those instances first, then replace
        // the dictionary so DynamicResource consumers and future controls use the
        // correct source of truth.
        ApplyBrushColors(app.Resources, source);
        ApplyShadowEffects(app.Resources, source);
        ReplaceActiveThemeDictionary(app.Resources, source);

        CurrentTheme = theme;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static AppTheme ResolveTheme(AppThemeMode mode)
    {
        return NormalizeMode(mode) switch
        {
            AppThemeMode.Light => AppTheme.Light,
            AppThemeMode.Dark => AppTheme.Dark,
            AppThemeMode.HighContrast => AppTheme.HighContrast,
            _ => IsSystemHighContrast() ? AppTheme.HighContrast : IsSystemLightTheme() ? AppTheme.Light : AppTheme.Dark
        };
    }

    private static bool IsSystemHighContrast()
    {
        try { return SystemParameters.HighContrast; }
        catch { return false; }
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            object? rawValue = key?.GetValue(AppsUseLightTheme);
            return rawValue switch
            {
                int value => value != 0,
                long value => value != 0,
                string value when int.TryParse(value, out int parsed) => parsed != 0,
                _ => true
            };
        }
        catch
        {
            return true;
        }
    }

    private static void EnsureSystemPreferenceWatcher()
    {
        if (_listeningForSystemChanges)
            return;

        try
        {
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            _listeningForSystemChanges = true;
        }
        catch
        {
            // Some hardened or service-like contexts disallow SystemEvents. Startup
            // still respects the system preference; live OS theme changes just won't.
        }
    }

    private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (CurrentMode != AppThemeMode.System)
            return;

        if (e.Category is not (UserPreferenceCategory.Accessibility
            or UserPreferenceCategory.Color
            or UserPreferenceCategory.General
            or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            ApplyMode(AppThemeMode.System);
            return;
        }

        if (app.Dispatcher.CheckAccess())
            ApplyMode(AppThemeMode.System);
        else
            app.Dispatcher.BeginInvoke(() => ApplyMode(AppThemeMode.System));
    }

    private static ResourceDictionary LoadTheme(AppTheme theme)
    {
        string themeName = theme switch
        {
            AppTheme.Light => "LightTheme",
            AppTheme.HighContrast => "HighContrastTheme",
            _ => "DarkTheme"
        };

        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{themeName}.xaml", UriKind.Absolute)
        };
    }

    private static void ReplaceActiveThemeDictionary(ResourceDictionary resources, ResourceDictionary themeDictionary)
    {
        var dictionaries = resources.MergedDictionaries;
        for (int index = 0; index < dictionaries.Count; index++)
        {
            if (!IsThemeDictionary(dictionaries[index]))
                continue;

            dictionaries[index] = themeDictionary;
            return;
        }

        dictionaries.Insert(0, themeDictionary);
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        string? source = dictionary.Source?.OriginalString?.Replace('\\', '/');
        return source is not null &&
               (source.EndsWith(DarkThemePath, StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith(LightThemePath, StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith(HighContrastThemePath, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyBrushColors(ResourceDictionary targetRoot, ResourceDictionary sourceRoot)
    {
        var sourceBrushes = CollectBrushes(sourceRoot);
        var targetBrushes = CollectBrushes(targetRoot);

        foreach (var (key, source) in sourceBrushes)
        {
            if (targetBrushes.TryGetValue(key, out var target) && !target.IsFrozen)
                target.Color = source.Color;
        }
    }

    private static void ApplyShadowEffects(ResourceDictionary targetRoot, ResourceDictionary sourceRoot)
    {
        var sourceEffects = CollectEffects(sourceRoot);
        var targetEffects = CollectEffects(targetRoot);

        foreach (var (key, source) in sourceEffects)
        {
            if (!targetEffects.TryGetValue(key, out var target) || target.IsFrozen)
                continue;

            target.BlurRadius = source.BlurRadius;
            target.Color = source.Color;
            target.Direction = source.Direction;
            target.Opacity = source.Opacity;
            target.RenderingBias = source.RenderingBias;
            target.ShadowDepth = source.ShadowDepth;
        }
    }

    private static Dictionary<string, SolidColorBrush> CollectBrushes(ResourceDictionary dictionary)
    {
        var brushes = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);
        CollectResources(dictionary, brushes, null);
        return brushes;
    }

    private static Dictionary<string, DropShadowEffect> CollectEffects(ResourceDictionary dictionary)
    {
        var effects = new Dictionary<string, DropShadowEffect>(StringComparer.Ordinal);
        CollectResources(dictionary, null, effects);
        return effects;
    }

    private static void CollectResources(
        ResourceDictionary dictionary,
        Dictionary<string, SolidColorBrush>? brushes,
        Dictionary<string, DropShadowEffect>? effects)
    {
        foreach (var merged in dictionary.MergedDictionaries)
            CollectResources(merged, brushes, effects);

        foreach (object key in dictionary.Keys)
        {
            if (key is not string stringKey)
                continue;

            if (brushes is not null && dictionary[key] is SolidColorBrush brush)
                brushes[stringKey] = brush;

            if (effects is not null && dictionary[key] is DropShadowEffect effect)
                effects[stringKey] = effect;
        }
    }
}
