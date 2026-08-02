using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace NVMeDriverPatcher.Tests;

public sealed class ThemeContrastTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("DarkTheme.xaml")]
    [InlineData("LightTheme.xaml")]
    [InlineData("HighContrastTheme.xaml")]
    public void ThemeStyles_MeetContrastFloorForEveryDeclaredTextSurfacePair(string themeFile)
    {
        var themeRoot = Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Themes");
        var palette = ReadPalette(Path.Combine(themeRoot, themeFile));
        var darkDocument = XDocument.Load(Path.Combine(themeRoot, "DarkTheme.xaml"));
        var styles = darkDocument.Descendants(Presentation + "Style")
            .Where(style => style.Attribute(Xaml + "Key") is not null)
            .ToDictionary(style => (string)style.Attribute(Xaml + "Key")!, StringComparer.Ordinal);

        var violations = new List<string>();
        foreach (var style in styles.Values)
        {
            var baseProperties = ResolveBaseProperties(style, styles, new HashSet<string>(StringComparer.Ordinal));
            var fontSize = ParseFontSize(baseProperties.GetValueOrDefault("FontSize"));
            var fontWeight = baseProperties.GetValueOrDefault("FontWeight");
            var threshold = IsLargeText(fontSize, fontWeight) ? 3.0 : 4.5;

            foreach (var state in EnumerateStates(style, baseProperties))
            {
                if (!TryResolveColor(state.Foreground, palette, out var foreground) ||
                    !TryResolveColor(state.Background, palette, out var background))
                {
                    continue;
                }

                var contrast = ContrastRatio(foreground, background);
                if (contrast + 0.01 < threshold)
                {
                    violations.Add(
                        $"{themeFile}:{style.Attribute(Xaml + "Key")?.Value} {state.Label} " +
                        $"{state.Foreground} on {state.Background} = {contrast:F2}:1 (needs {threshold:F1}:1)");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("DarkTheme.xaml", "#FFFFFFFF", "#FF006EF6", "#FF0057C4", "#FF00489F")]
    [InlineData("LightTheme.xaml", "#FFFFFFFF", "#FF2563EB", "#FF1D4ED8", "#FF1E40AF")]
    [InlineData("HighContrastTheme.xaml", "#FF000000", "#FF66D9FF", "#FF9BE7FF", "#FF43B5D8")]
    public void ActionButtonRamp_IsPinnedForEveryTheme(
        string themeFile,
        string expectedForeground,
        string expectedRest,
        string expectedHover,
        string expectedPressed)
    {
        var path = Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher", "Themes", themeFile);
        var palette = ReadPalette(path);

        Assert.Equal(expectedForeground, palette["AccentForeground"]);
        Assert.Equal(expectedRest, palette["ActionButtonBackground"]);
        Assert.Equal(expectedHover, palette["ActionButtonHover"]);
        Assert.Equal(expectedPressed, palette["ActionButtonPressed"]);
        Assert.True(ContrastRatio(expectedForeground, expectedRest) >= 4.5);
        Assert.True(ContrastRatio(expectedForeground, expectedHover) >= 4.5);
        Assert.True(ContrastRatio(expectedForeground, expectedPressed) >= 4.5);
    }

    private static IEnumerable<ThemeState> EnumerateStates(
        XElement style,
        IReadOnlyDictionary<string, string> baseProperties)
    {
        yield return new ThemeState(
            baseProperties.GetValueOrDefault("Foreground"),
            baseProperties.GetValueOrDefault("Background"),
            "base");

        foreach (var trigger in style.Descendants().Where(IsTrigger))
        {
            if (IsDisabledState(trigger) || HasReducedOpacity(trigger))
            {
                continue;
            }

            var properties = new Dictionary<string, string>(baseProperties, StringComparer.Ordinal);
            foreach (var setter in trigger.Descendants(Presentation + "Setter"))
            {
                var property = (string?)setter.Attribute("Property");
                var value = (string?)setter.Attribute("Value");
                if (property is "Foreground" or "Background" && value is not null)
                {
                    properties[property] = value;
                }
            }

            yield return new ThemeState(
                properties.GetValueOrDefault("Foreground"),
                properties.GetValueOrDefault("Background"),
                trigger.Attribute("Property")?.Value ?? trigger.Name.LocalName);
        }
    }

    private static Dictionary<string, string> ResolveBaseProperties(
        XElement style,
        IReadOnlyDictionary<string, XElement> styles,
        HashSet<string> seen)
    {
        var key = (string?)style.Attribute(Xaml + "Key") ?? string.Empty;
        if (!seen.Add(key))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var basedOn = (string?)style.Attribute("BasedOn");
        var baseKey = ExtractResourceKey(basedOn);
        if (baseKey is not null && styles.TryGetValue(baseKey, out var baseStyle))
        {
            foreach (var pair in ResolveBaseProperties(baseStyle, styles, seen))
            {
                properties[pair.Key] = pair.Value;
            }
        }

        foreach (var setter in style.Elements(Presentation + "Setter"))
        {
            var property = (string?)setter.Attribute("Property");
            var value = (string?)setter.Attribute("Value");
            if (property is not null && value is not null)
            {
                properties[property] = value;
            }
        }

        return properties;
    }

    private static Dictionary<string, string> ReadPalette(string path) =>
        XDocument.Load(path)
            .Descendants(Presentation + "SolidColorBrush")
            .Where(brush => brush.Attribute(Xaml + "Key") is not null && brush.Attribute("Color") is not null)
            .ToDictionary(
                brush => (string)brush.Attribute(Xaml + "Key")!,
                brush => (string)brush.Attribute("Color")!,
                StringComparer.Ordinal);

    private static bool IsTrigger(XElement element) =>
        element.Name.LocalName is "Trigger" or "DataTrigger" or "MultiDataTrigger";

    private static bool IsDisabledState(XElement trigger) =>
        (string?)trigger.Attribute("Property") == "IsEnabled" &&
        string.Equals((string?)trigger.Attribute("Value"), "False", StringComparison.OrdinalIgnoreCase);

    private static bool HasReducedOpacity(XElement trigger) =>
        trigger.Descendants(Presentation + "Setter")
            .Where(setter => (string?)setter.Attribute("Property") == "Opacity")
            .Select(setter => (string?)setter.Attribute("Value"))
            .Any(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity) && opacity < 1);

    private static string? ExtractResourceKey(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var firstSpace = value.IndexOf(' ');
        var closingBrace = value.IndexOf('}');
        return value.StartsWith("{StaticResource ", StringComparison.Ordinal) && firstSpace >= 0 && closingBrace > firstSpace
            ? value[(firstSpace + 1)..closingBrace]
            : null;
    }

    private static bool TryResolveColor(
        string? value,
        IReadOnlyDictionary<string, string> palette,
        out string color)
    {
        color = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.StartsWith("{DynamicResource ", StringComparison.Ordinal))
        {
            var key = ExtractResourceKey(value.Replace("DynamicResource", "StaticResource", StringComparison.Ordinal));
            if (key is not null && palette.TryGetValue(key, out var resolved))
            {
                color = resolved;
                return true;
            }

            return false;
        }

        if (value.StartsWith('#') && (value.Length == 7 || value.Length == 9))
        {
            color = value;
            return value.Length == 7 || value.StartsWith("#FF", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static double ParseFontSize(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize)
            ? fontSize
            : 12.0;

    private static bool IsLargeText(double fontSize, string? fontWeight) =>
        fontSize >= 24 || (fontSize >= 18.66 && string.Equals(fontWeight, "Bold", StringComparison.OrdinalIgnoreCase));

    private static double ContrastRatio(string foreground, string background) =>
        (Math.Max(Luminance(foreground), Luminance(background)) + 0.05) /
        (Math.Min(Luminance(foreground), Luminance(background)) + 0.05);

    private static double Luminance(string color)
    {
        var hex = color.TrimStart('#');
        if (hex.Length == 8)
        {
            hex = hex[2..];
        }

        var channels = new[]
        {
            Convert.ToInt32(hex[0..2], 16),
            Convert.ToInt32(hex[2..4], 16),
            Convert.ToInt32(hex[4..6], 16)
        }.Select(channel => channel / 255.0)
         .Select(channel => channel <= 0.03928
             ? channel / 12.92
             : Math.Pow((channel + 0.055) / 1.055, 2.4))
         .ToArray();

        return (0.2126 * channels[0]) + (0.7152 * channels[1]) + (0.0722 * channels[2]);
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    private sealed record ThemeState(string? Foreground, string? Background, string Label);
}
