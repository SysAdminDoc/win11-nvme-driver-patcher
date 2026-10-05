using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The GUI had 18 inline font sizes, half a point apart in places, with nothing to change them
/// together. Sizes now come from the type ramp at the top of DarkTheme.xaml (the light and
/// high-contrast dictionaries merge it), and these tests fail on a literal size anywhere else.
/// </summary>
public sealed class TypeRampTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly Regex RampEntry = new(
        @"<sys:Double x:Key=""(FontSize\w+)"">([0-9.]+)</sys:Double>", RegexOptions.Compiled);
    private static readonly Regex TokenReference = new(
        @"^\{(?:StaticResource|DynamicResource) (?:ResourceKey=)?(FontSize\w+)\}$", RegexOptions.Compiled);
    // FontSize = <expr> or SetValue(X.FontSizeProperty, <expr>), across line breaks, up to the end
    // of the expression. A numeric literal anywhere in it (12, 12.5, 12d, 12.0f) is an offender.
    private static readonly Regex CodeAssignment = new(
        @"\bFontSize\s*=(?!=)\s*(?<expr>[^;,{}]*)|\bFontSizeProperty\s*,\s*(?<expr>[^;)]*)", RegexOptions.Compiled);
    private static readonly Regex NumericLiteral = new(
        @"(?<![\w.""])\d+(?:\.\d+)?[dDfFmM]?(?![\w.])", RegexOptions.Compiled);

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
        var offenders = GuiFiles("*.xaml")
            .SelectMany(file => XamlOffenders(XDocument.Load(file, LoadOptions.SetLineInfo).Root!, keys)
                .Select(hit => $"{Path.GetFileName(file)}:{hit}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Font sizes outside the type ramp:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void GuiCode_SetsNoLiteralFontSize()
    {
        var offenders = GuiFiles("*.cs")
            .SelectMany(file => CodeOffenders(File.ReadAllText(file)).Select(hit => $"{Path.GetFileName(file)}:{hit}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Literal font sizes in GUI code:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ScannerCatchesLiteralSizes()
    {
        // Positive control: each shape a literal size can take must be flagged, and token use must not.
        var keys = new HashSet<string>(StringComparer.Ordinal) { "FontSizeBody" };
        var xaml = XElement.Parse($$$"""
            <Grid xmlns="{{{Wpf.NamespaceName}}}" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <TextBlock FontSize="11.5"/>
              <Run FontSize="9"/>
              <Span TextElement.FontSize="9"/>
              <Setter Property="FontSize" Value="12"/>
              <Setter TargetName="border" Property="FontSize" Value="14"/>
              <Setter
                  Property="TextElement.FontSize"
                  Value="12"/>
              <Setter Property="FontSize"><Setter.Value>14</Setter.Value></Setter>
              <TextBlock><TextBlock.FontSize>14</TextBlock.FontSize></TextBlock>
              <DoubleAnimation Storyboard.TargetProperty="FontSize" To="20"/>
              <TextBlock FontSize="{StaticResource FontSizeMissing}"/>
              <TextBlock FontSize="{StaticResource FontSizeBody}"/>
              <TextBlock FontSize="{DynamicResource ResourceKey=FontSizeBody}"/>
              <Setter Property="FontSize" Value="{StaticResource FontSizeBody}"/>
              <Setter Property="FontSize"><Setter.Value><StaticResource ResourceKey="FontSizeBody"/></Setter.Value></Setter>
            </Grid>
            """, LoadOptions.SetLineInfo);
        Assert.Equal(10, XamlOffenders(xaml, keys).Count());

        Assert.Equal(5, CodeOffenders("""
            block.FontSize = isDecisionLine ? 13.5 : 13.25;
            block.FontSize = 12d;
            block.SetValue(TextBlock.FontSizeProperty, 14.0);
            var run = new Run { FontSize =
                12 };
            label.FontSize = 1.5 * TypeRamp("FontSizeBody");
            block.FontSize = TypeRamp("FontSizeBody");
            if (block.FontSize == other.FontSize) { }
            // block.FontSize = 99;
            """).Count());
    }

    private static IEnumerable<string> XamlOffenders(XElement root, IReadOnlySet<string> keys)
    {
        bool IsToken(string value)
        {
            var token = TokenReference.Match(value.Trim());
            return token.Success && keys.Contains(token.Groups[1].Value);
        }

        // <Setter.Value><StaticResource ResourceKey="FontSizeBody"/></Setter.Value> and the like.
        bool HoldsToken(XElement container) =>
            container.Elements().Count() == 1 &&
            container.Elements().Single() is var only &&
            only.Name.LocalName is "StaticResource" or "DynamicResource" &&
            keys.Contains((string?)only.Attribute("ResourceKey") ?? string.Empty);

        static bool IsFontSize(string name) => name == "FontSize" || name.EndsWith(".FontSize", StringComparison.Ordinal);

        foreach (var element in root.DescendantsAndSelf())
        {
            int line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            string name = element.Name.LocalName;

            foreach (var attribute in element.Attributes().Where(a => IsFontSize(a.Name.LocalName)))
                if (!IsToken(attribute.Value))
                    yield return $"{line}: {name} {attribute.Name.LocalName}=\"{attribute.Value}\"";

            if (name == "Setter" && IsFontSize((string?)element.Attribute("Property") ?? string.Empty))
            {
                var value = element.Attribute("Value");
                var valueElement = element.Element(Wpf + "Setter.Value");
                if (value is not null ? !IsToken(value.Value) : valueElement is null || !HoldsToken(valueElement))
                    yield return $"{line}: Setter {element.Attribute("Property")!.Value} = {value?.Value ?? valueElement?.Value ?? "(no value)"}";
            }

            // Property element syntax: <TextBlock.FontSize>14</TextBlock.FontSize>.
            if (IsFontSize(name) && name.Contains('.') && !HoldsToken(element))
                yield return $"{line}: <{name}>{element.Value}</{name}>";

            // An animated size leaves the ramp by definition.
            if (((string?)element.Attribute("Storyboard.TargetProperty") ?? string.Empty).Contains("FontSize", StringComparison.Ordinal))
                yield return $"{line}: {name} animates FontSize";
        }
    }

    private static IEnumerable<string> CodeOffenders(string source)
    {
        // Line comments go first so a commented-out assignment doesn't count. Replacing them with
        // spaces keeps every offset, and so every line number, where it was.
        var code = Regex.Replace(source, @"//[^\r\n]*", match => new string(' ', match.Length));
        foreach (Match match in CodeAssignment.Matches(code))
        {
            var expr = match.Groups["expr"].Value;
            if (!NumericLiteral.IsMatch(expr)) continue;
            int line = code.Take(match.Index).Count(c => c == '\n') + 1;
            yield return $"{line}: {match.Value.Trim()}";
        }
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
