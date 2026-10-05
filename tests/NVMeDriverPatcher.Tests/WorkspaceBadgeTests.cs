using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The redesign moved workspace navigation to the sidebar and gave the workspace TabControl a
/// template that renders only the selected content, never a header strip. The Benchmark,
/// Telemetry and Recovery tab headers kept their status badges (behind a style that also
/// defaulted them to Collapsed), so the badges and the view-model logic feeding them could
/// never reach the screen.
/// </summary>
public sealed class WorkspaceBadgeTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string HeaderlessTabControlStyle = "WorkspaceContentTabControl";

    [Fact]
    public void WorkspaceTabHeaders_CarryNoStatusContent()
    {
        var root = RepoRoot();

        // Premise: the workspace TabControl template has no header host. If a header strip is
        // ever restored, this fails first and the badges can be reconsidered on purpose.
        var theme = XDocument.Load(Path.Combine(root, "src", "NVMeDriverPatcher", "Themes", "DarkTheme.xaml"));
        var style = Assert.Single(theme.Root!.Elements(Wpf + "Style"),
            s => s.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == HeaderlessTabControlStyle));
        Assert.DoesNotContain(style.Descendants(), e => e.Name.LocalName == "TabPanel");
        Assert.DoesNotContain(style.Descendants().Attributes(), a => a.Name.LocalName == "IsItemsHost");

        var window = XDocument.Load(Path.Combine(root, "src", "NVMeDriverPatcher", "Views", "MainWindow.xaml"), LoadOptions.SetLineInfo);
        var headers = window.Descendants(Wpf + "TabControl")
            .Where(t => (t.Attribute("Style")?.Value ?? "").Contains(HeaderlessTabControlStyle, StringComparison.Ordinal))
            .SelectMany(t => t.Descendants(Wpf + "TabItem.Header"))
            .ToList();
        Assert.NotEmpty(headers);

        var hidden = headers
            .SelectMany(h => h.DescendantsAndSelf().Attributes())
            .Where(a => a.Value.StartsWith("{Binding", StringComparison.Ordinal))
            .Select(a => $"MainWindow.xaml:{((System.Xml.IXmlLineInfo)a.Parent!).LineNumber} {a.Name.LocalName}=\"{a.Value}\"")
            .ToList();
        Assert.True(hidden.Count == 0,
            "These bindings sit in workspace tab headers, which the headerless tab template never renders:\n" +
            string.Join("\n", hidden));

        // Every badge the view model computes must be bound somewhere that does render.
        var headerElements = headers.SelectMany(h => h.DescendantsAndSelf()).ToHashSet();
        var renderedBindings = window.Descendants()
            .Where(e => !headerElements.Contains(e))
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .Where(v => v.Contains("{Binding", StringComparison.Ordinal))
            .ToList();
        var unbound = typeof(MainViewModel)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .Where(n => n.EndsWith("BadgeText", StringComparison.Ordinal) || n.EndsWith("BadgeColor", StringComparison.Ordinal))
            .Where(n => !renderedBindings.Any(v => Regex.IsMatch(v, $@"\{{Binding (?:Path=)?{n}\b")))
            .ToList();
        Assert.True(unbound.Count == 0,
            "View-model badge properties with no rendered binding: " + string.Join(", ", unbound));
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
