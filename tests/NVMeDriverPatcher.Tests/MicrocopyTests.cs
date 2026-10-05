using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The GUI spelled the same concept "Safe Boot" in its buttons and tooltips but "SafeBoot" in its
/// toasts, dialogs and log lines, and every ThemedDialog had an empty window title, so screen
/// readers announced each modal as blank.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class MicrocopyTests
{
    [Theory]
    [InlineData("Clear Activity Log", DialogButtons.YesNo, DialogIcon.Warning, "Clear Activity Log")]
    [InlineData("Safe Boot Upgrade Failed", DialogButtons.OK, DialogIcon.Error, "Safe Boot Upgrade Failed")]
    [InlineData("", DialogButtons.OK, DialogIcon.Information, "NVMe Driver Patcher")]
    public void ThemedDialog_WindowTitleMatchesItsHeading(string title, DialogButtons buttons, DialogIcon icon, string expected)
    {
        WpfTestHost.Run(() =>
        {
            ThemeService.ApplyMode(AppThemeMode.Dark);
            var dialog = ThemedDialog.Create("Body text.", title, buttons, icon);
            try
            {
                Assert.Equal(expected, dialog.Title);
            }
            finally
            {
                // An un-closed Window keeps WPF (and therefore the test host) alive.
                dialog.Close();
            }
        });
    }

    // "SafeBoot" glued together is only right as part of a registry path (SafeBoot\Minimal).
    private static readonly Regex GluedSafeBoot = new(@"\bSafeBoot\b(?!\\)", RegexOptions.CultureInvariant);
    private static readonly Regex StringLiteral = new(@"@?\$?""(?:[^""\\\r\n]|\\.)*""", RegexOptions.CultureInvariant);
    private static readonly string[] ProseAttributes =
        ["Text", "ToolTip", "Content", "Header", "Title", "AutomationProperties.Name", "AutomationProperties.HelpText"];

    [Fact]
    public void UserFacingText_SpellsSafeBootAsTwoWords()
    {
        // Self-check: the detector must flag the original toast title and leave registry paths
        // and bare identifiers alone, or a pass means nothing.
        Assert.Single(ProseHits("ToastService.Show(\"SafeBoot Entries Upgraded\", message, ToastType.Success, Config.EnableToasts);"));
        Assert.Empty(ProseHits("\"service-name entries (SafeBoot\\\\Minimal\\\\nvmedisk, SafeBoot\\\\Network\\\\nvmedisk) \" +"));
        Assert.Empty(ProseHits("Id = \"SafeBoot/Net\", Name = \"Network\","));

        var root = RepoRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src", "NVMeDriverPatcher"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            // The upgrade service's results are the body of the GUI's upgrade toast and dialog.
            .Append(Path.Combine(root, "src", "NVMeDriverPatcher.Core", "Services", "SafeBootUpgradeService.cs"));

        var offenders = new List<string>();
        foreach (var file in sources)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) continue;
                offenders.AddRange(ProseHits(lines[i]).Select(hit => $"{Path.GetFileName(file)}:{i + 1} {hit}"));
            }
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "NVMeDriverPatcher", "Views"), "*.xaml"))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var attribute in doc.Descendants().Attributes()
                         .Where(a => ProseAttributes.Contains(a.Name.LocalName) && GluedSafeBoot.IsMatch(a.Value)))
            {
                offenders.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)attribute).LineNumber} {attribute.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Write \"Safe Boot\" in prose; keep \"SafeBoot\" for registry paths only:\n" + string.Join("\n", offenders));
    }

    // String literals that read as prose (contain a space) and glue "SafeBoot" outside a path.
    private static IEnumerable<string> ProseHits(string line) =>
        StringLiteral.Matches(line)
            .Select(m => m.Value)
            .Where(literal => literal.Contains(' ') && GluedSafeBoot.IsMatch(literal));

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
