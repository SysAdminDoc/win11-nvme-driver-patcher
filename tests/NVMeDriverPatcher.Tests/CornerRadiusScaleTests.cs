using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Corner radii drifted to 2, 3, 7, 9, 11, 14 and 17 across the views. The 17 made the workflow
/// nodes full circles and the 9 and 10 radii on 20 px status chips made them pills. Every radius now
/// comes from one scale, and nothing with a known size may round into a circle or capsule.
/// </summary>
public sealed class CornerRadiusScaleTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly HashSet<double> Scale = [0, 4, 6, 8, 10, 12];

    [Fact]
    public void EveryCornerRadius_ComesFromTheScale()
    {
        // Self-check against the markup this replaced, or a pass means nothing.
        var original = XElement.Parse($$$"""
            <Grid xmlns="{{{Wpf.NamespaceName}}}">
              <Border CornerRadius="9"/>
              <Border CornerRadius="0,2,2,0"/>
              <Style><Setter Property="CornerRadius" Value="14"/></Style>
            </Grid>
            """);
        Assert.Equal(3, OffScale(original).Count());

        var offenders = XamlFiles()
            .SelectMany(file => OffScale(XDocument.Load(file, LoadOptions.SetLineInfo).Root!)
                .Select(hit => $"{Path.GetFileName(file)}:{hit.Line} CornerRadius {hit.Value}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Corner radii must come from 0, 4, 6, 8, 10 or 12:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NothingWithAKnownSize_RoundsIntoACircleOrCapsule()
    {
        var original = XElement.Parse($$$"""
            <Grid xmlns="{{{Wpf.NamespaceName}}}">
              <Style x:Key="WorkflowNode" TargetType="Border" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Setter Property="Width" Value="34"/>
                <Setter Property="Height" Value="34"/>
                <Setter Property="CornerRadius" Value="17"/>
              </Style>
              <Ellipse Width="6" Height="6"/>
            </Grid>
            """);
        Assert.Equal(2, Rounded(original).Count());

        var offenders = XamlFiles()
            .SelectMany(file => Rounded(XDocument.Load(file, LoadOptions.SetLineInfo).Root!)
                .Select(hit => $"{Path.GetFileName(file)}:{hit}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Shapes stay square-cornered rectangles; a radius of half the short side or an Ellipse reads as a pill:\n" +
            string.Join("\n", offenders));
    }

    private static IEnumerable<(int Line, string Value)> OffScale(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            var value = element.Attribute("CornerRadius")?.Value;
            if (value is null && element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "CornerRadius")
                value = element.Attribute("Value")?.Value;
            if (value is null || value.StartsWith('{'))
                continue;

            var parts = value.Split(',').Select(part => double.Parse(part.Trim(), CultureInfo.InvariantCulture));
            if (!parts.All(Scale.Contains))
                yield return (((System.Xml.IXmlLineInfo)element).LineNumber, value);
        }
    }

    private static IEnumerable<string> Rounded(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            if (element.Name.LocalName == "Ellipse")
            {
                yield return $"{line} Ellipse";
                continue;
            }

            var size = SizeOf(element);
            if (size is null)
                continue;

            var shortSide = Math.Min(size.Value.Width, size.Value.Height);
            if (2 * size.Value.Radius >= shortSide)
                yield return $"{line} radius {size.Value.Radius} on {size.Value.Width}x{size.Value.Height}";
        }
    }

    // Width, Height and a uniform CornerRadius, read from attributes or from a style's setters.
    private static (double Width, double Height, double Radius)? SizeOf(XElement element)
    {
        string? Read(string property) =>
            element.Attribute(property)?.Value ??
            element.Elements(Wpf + "Setter")
                .FirstOrDefault(setter => (string?)setter.Attribute("Property") == property)
                ?.Attribute("Value")?.Value;

        static double? Number(string? text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

        var width = Number(Read("Width"));
        var height = Number(Read("Height"));
        var radius = Number(Read("CornerRadius"));
        return width is > 0 && height is > 0 && radius is > 0
            ? (width.Value, height.Value, radius.Value)
            : null;
    }

    private static IEnumerable<string> XamlFiles()
    {
        var root = Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher");
        return Directory.EnumerateFiles(Path.Combine(root, "Views"), "*.xaml")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Themes"), "*.xaml"));
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
