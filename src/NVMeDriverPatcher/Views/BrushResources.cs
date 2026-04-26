using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NVMeDriverPatcher.Views;

internal static class BrushResources
{
    private const string GenericFallbackHex = "#FF808080";

    private static readonly IReadOnlyDictionary<string, string> SemanticFallbacks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BgDarkest"] = "#FF0D0F13",
            ["BgDark"] = "#FF111419",
            ["BgMedium"] = "#FF161A20",
            ["BgLight"] = "#FF1C222A",
            ["SurfaceRaised"] = "#FF181D24",
            ["SurfaceInset"] = "#FF101318",
            ["Border"] = "#FF2A3038",
            ["BorderLight"] = "#FF39414B",
            ["BorderStrong"] = "#FF596575",
            ["TextPrimary"] = "#FFF6F9FF",
            ["TextSecondary"] = "#FFD5DEEB",
            ["TextMuted"] = "#FFAAB6C8",
            ["TextDim"] = "#FF8694A8",
            ["TextDimmer"] = "#FF647285",
            ["Accent"] = "#FF2563EB",
            ["AccentHover"] = "#FF1D4ED8",
            ["AccentForeground"] = "#FFFFFFFF",
            ["AccentBg"] = "#FF10243A",
            ["AccentSoft"] = "#FF203A55",
            ["Green"] = "#FF7AD7AE",
            ["GreenBg"] = "#FF123124",
            ["Yellow"] = "#FFE4BD73",
            ["YellowBg"] = "#FF302411",
            ["Red"] = "#FFF0A1A1",
            ["RedBg"] = "#FF351A20",
            ["Focus"] = "#FFB7DCFF",
            ["FocusGlow"] = "#334E9CFF",
            ["WindowCanvasBrush"] = "#FF0D0F13",
            ["HeroSurfaceBrush"] = "#FF141820",
            ["SurfaceCardBrush"] = "#FF14181E",
            ["InsetCardBrush"] = "#FF101318",
            ["DisabledSurfaceBrush"] = "#FF171B22",
            ["OverlayScrimBrush"] = "#CC0D0F13",
            ["AccentLineBrush"] = "#FF3D4A58"
        };

    private static readonly Dictionary<string, Brush> FallbackBrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FallbackBrushLock = new();

    public static Brush Resolve(FrameworkElement owner, string resourceKey)
    {
        return Resolve(owner, resourceKey, GetFallbackHex(resourceKey));
    }

    public static Brush Resolve(FrameworkElement owner, string resourceKey, string fallbackHex)
    {
        if (owner.TryFindResource(resourceKey) is Brush brush)
            return brush;

        return GetFallbackBrush(fallbackHex);
    }

    public static System.Windows.Media.Color ResolveColor(FrameworkElement owner, string resourceKey)
    {
        return ResolveColor(owner, resourceKey, GetFallbackHex(resourceKey));
    }

    public static System.Windows.Media.Color ResolveColor(FrameworkElement owner, string resourceKey, string fallbackHex)
    {
        if (Resolve(owner, resourceKey, fallbackHex) is SolidColorBrush solid)
            return solid.Color;

        return (ParseHexBrush(fallbackHex) as SolidColorBrush)?.Color
            ?? System.Windows.Media.Color.FromRgb(128, 128, 128);
    }

    private static string GetFallbackHex(string resourceKey)
    {
        return SemanticFallbacks.TryGetValue(resourceKey, out var fallbackHex)
            ? fallbackHex
            : GenericFallbackHex;
    }

    private static Brush GetFallbackBrush(string fallbackHex)
    {
        lock (FallbackBrushLock)
        {
            if (FallbackBrushCache.TryGetValue(fallbackHex, out var cached))
                return cached;

            var brush = ParseHexBrush(fallbackHex)
                ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128));
            if (brush.CanFreeze)
                brush.Freeze();
            FallbackBrushCache[fallbackHex] = brush;
            return brush;
        }
    }

    private static Brush? ParseHexBrush(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#')
            return null;

        var digits = hex.AsSpan(1);
        if (digits.Length != 8 && digits.Length != 6)
            return null;

        byte a = 0xFF;
        int offset = 0;
        if (digits.Length == 8)
        {
            if (!TryParseHexByte(digits, offset, out a))
                return null;
            offset += 2;
        }

        if (!TryParseHexByte(digits, offset, out var r) ||
            !TryParseHexByte(digits, offset + 2, out var g) ||
            !TryParseHexByte(digits, offset + 4, out var b))
        {
            return null;
        }

        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static bool TryParseHexByte(ReadOnlySpan<char> digits, int offset, out byte value)
    {
        value = 0;
        if (offset < 0 || offset + 2 > digits.Length)
            return false;

        return byte.TryParse(
            digits.Slice(offset, 2),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out value);
    }
}
