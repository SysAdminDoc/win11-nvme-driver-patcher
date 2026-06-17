using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Converters;

public class StatusToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush PassBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A)));
    private static readonly SolidColorBrush WarningBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCA, 0x8A, 0x04)));
    private static readonly SolidColorBrush FailBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)));
    private static readonly SolidColorBrush InfoBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB)));
    internal static readonly SolidColorBrush DefaultBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x71, 0x71, 0x7A)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            CheckStatus.Pass => ResolveBrush("Green", PassBrush),
            CheckStatus.Warning => ResolveBrush("Yellow", WarningBrush),
            CheckStatus.Fail => ResolveBrush("Red", FailBrush),
            CheckStatus.Info => ResolveBrush("Accent", InfoBrush),
            _ => ResolveBrush("TextDim", DefaultBrush)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;

    private static Brush ResolveBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

public class SettingsToggleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "- Settings" : "+ Settings";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

public class StringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string token && !string.IsNullOrWhiteSpace(token))
        {
            if (Application.Current?.TryFindResource(token) is SolidColorBrush resourceBrush)
                return resourceBrush.Color;

            try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(token); }
            catch { }
        }
        return (Application.Current?.TryFindResource("TextDim") as SolidColorBrush)?.Color
               ?? StatusToColorConverter.DefaultBrush.Color;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

public class StringToBrushConverter : IValueConverter
{
    private static readonly BrushConverter BC = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string token && !string.IsNullOrWhiteSpace(token))
        {
            if (Application.Current?.TryFindResource(token) is Brush resourceBrush)
                return resourceBrush;

            try { return (Brush)BC.ConvertFromString(token)!; }
            catch { }
        }
        return Application.Current?.TryFindResource("TextDim") as Brush
               ?? StatusToColorConverter.DefaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
