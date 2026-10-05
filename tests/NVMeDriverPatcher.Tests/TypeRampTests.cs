using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The GUI had 18 inline font sizes, half a point apart in places, with nothing to change them
/// together. Sizes now come from the type ramp at the top of DarkTheme.xaml (the light and
/// high-contrast dictionaries merge it), and these tests fail on a literal size anywhere else.
/// </summary>
public sealed class TypeRampTests
{
    private static readonly Regex RampEntry = new(
        @"<sys:Double x:Key=""(FontSize\w+)"">([0-9.]+)</sys:Double>", RegexOptions.Compiled);
    private static readonly Regex FontSizeAttribute = new(@"\bFontSize=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex FontSizeSetter = new(
        @"<Setter\s+Property=""(?:\w+\.)?FontSize""\s+Value=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex TokenReference = new(
        @"^\{(?:StaticResource|DynamicResource) (FontSize\w+)\}$", RegexOptions.Compiled);
    private static readonly Regex LiteralCodeSize = new(@"\bFontSize\s*=\s*[^;,\r\n]*\b\d+(\.\d+)?\b", RegexOptions.Compiled);

    [Fact]
    public void Ramp_IsOrderedAndAtLeastAPointApart()
    {
        var ramp = ReadRamp();

        Assert.True(ramp.Count >= 6, "the ramp should cover caption through display");
        for (int i = 1; i < ramp.Count; i++)
            Assert.True(ramp[i].Size - ramp[i - 1].Size >= 1.0,
                $"{ramp[i - 1].Key} ({ramp[i - 1].Size}) and {ramp[i].Key} ({ramp[i].Size}) are less than a point apart");
    }

    [Fact]
    public void EveryXamlFontSize_ResolvesToARampToken()
    {
        var keys = ReadRamp().Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var file in GuiFiles("*.xaml"))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var values = FontSizeAttribute.Matches(lines[i]).Select(m => m.Groups[1].Value)
                    .Concat(FontSizeSetter.Matches(lines[i]).Select(m => m.Groups[1].Value));
                foreach (var value in values)
                {
                    var token = TokenReference.Match(value);
                    if (!token.Success || !keys.Contains(token.Groups[1].Value))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: FontSize=\"{value}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Font sizes outside the type ramp:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void GuiCode_SetsNoLiteralFontSize()
    {
        var offenders = new List<string>();
        foreach (var file in GuiFiles("*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (LiteralCodeSize.IsMatch(line))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line}");
            }
        }

        Assert.True(offenders.Count == 0, "Literal font sizes in GUI code:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ScannerCatchesLiteralSizes()
    {
        // Positive control: the patterns above must flag the shapes they exist to stop.
        Assert.DoesNotMatch(TokenReference, FontSizeAttribute.Match("<TextBlock FontSize=\"11.5\"/>").Groups[1].Value);
        Assert.Matches(FontSizeSetter, "<Setter Property=\"FontSize\" Value=\"12\"/>");
        Assert.Matches(FontSizeSetter, "<Setter Property=\"TextElement.FontSize\" Value=\"12\"/>");
        Assert.Matches(LiteralCodeSize, "FontSize = isDecisionLine ? 13.5 : 13.25,");
        Assert.DoesNotMatch(LiteralCodeSize, "FontSize = TypeRamp(\"FontSizeBody\"),");
    }

    private static List<(string Key, double Size)> ReadRamp()
    {
        var theme = File.ReadAllText(GuiPath("Themes", "DarkTheme.xaml"));
        return RampEntry.Matches(theme)
            .Select(m => (m.Groups[1].Value, double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
    }

    private static IEnumerable<string> GuiFiles(string pattern) =>
        Directory.EnumerateFiles(GuiPath(), pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string GuiPath(string? folder = null, string? file = null, [CallerFilePath] string sourceFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "src", "NVMeDriverPatcher"));
        if (folder is null) return root;
        return file is null ? Path.Combine(root, folder) : Path.Combine(root, folder, file);
    }
}
