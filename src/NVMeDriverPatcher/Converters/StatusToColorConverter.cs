using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Converters;

public class StatusToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush PassBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xc5, 0x5e)));
    private static readonly SolidColorBrush WarningBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf5, 0x9e, 0x0b)));
    private static readonly SolidColorBrush FailBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xef, 0x44, 0x44)));
    private static readonly SolidColorBrush InfoBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3b, 0x82, 0xf6)));
    private static readonly SolidColorBrush DefaultBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x71, 0x71, 0x7a)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            CheckStatus.Pass => PassBrush,
            CheckStatus.Warning => WarningBrush,
            CheckStatus.Fail => FailBrush,
            CheckStatus.Info => InfoBrush,
            _ => DefaultBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class SettingsToggleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "- Settings" : "+ Settings";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex); }
            catch { }
        }
        return System.Windows.Media.Colors.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringToBrushConverter : IValueConverter
{
    private static readonly BrushConverter BC = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try { return (Brush)BC.ConvertFromString(hex)!; }
            catch { }
        }
        return System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
