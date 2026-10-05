using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// TextDimmer is the disabled-text brush. Its contrast is too low for anything a person has to
/// read, and the only use outside a disabled trigger was a bullet in a footer that never showed.
/// </summary>
public sealed class DisabledTextBrushTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void TextDimmer_IsOnlyUsedInsideDisabledTriggers()
    {
        // Self-check against the footer bullet this replaced, or a pass means nothing.
        var original = XElement.Parse($$$"""
            <Grid xmlns="{{{Wpf.NamespaceName}}}">
              <TextBlock Foreground="{DynamicResource TextDimmer}" Text="&#x2022;"/>
              <Style TargetType="Button">
                <Style.Triggers>
                  <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Foreground" Value="{DynamicResource TextDimmer}"/>
                  </Trigger>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter Property="Foreground" Value="{StaticResource TextDimmer}"/>
                  </Trigger>
                </Style.Triggers>
              </Style>
            </Grid>
            """);
        Assert.Equal(2, Offenders(original).Count());

        var offenders = XamlFiles()
            .SelectMany(file => Offenders(XDocument.Load(file, LoadOptions.SetLineInfo).Root!)
                .Select(line => $"{Path.GetFileName(file)}:{line}"))
            .ToList();
        Assert.True(offenders.Count == 0,
            "TextDimmer belongs to IsEnabled=False triggers only:\n" + string.Join("\n", offenders));

        var codeBehind = Directory.EnumerateFiles(GuiRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file) && File.ReadAllText(file).Contains("TextDimmer", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(codeBehind);
    }

    private static IEnumerable<int> Offenders(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            bool usesBrush = element.Attributes().Any(attribute =>
                attribute.Value.Contains("Resource TextDimmer}", StringComparison.Ordinal));
            if (usesBrush && !element.Ancestors().Any(IsDisabledTrigger))
                yield return ((System.Xml.IXmlLineInfo)element).LineNumber;
        }
    }

    private static bool IsDisabledTrigger(XElement element) =>
        element.Name.LocalName == "Trigger" &&
        (string?)element.Attribute("Property") == "IsEnabled" &&
        (string?)element.Attribute("Value") == "False";

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(GuiRoot(), "*.xaml", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file));

    private static bool IsBuildOutput(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }

    private static string GuiRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "src", "NVMeDriverPatcher"));
}
