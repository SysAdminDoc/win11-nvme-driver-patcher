using System.Runtime.CompilerServices;
using NVMeDriverPatcher.Services;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// People who never read the README now reach the Apply confirmation. It has to say that the measured
/// gains are high-queue-depth gains, that desktop use may see nothing, and that StorageReview's
/// Windows Server testing measured 4K random writes slightly slower.
/// </summary>
public sealed class FirstRunExpectationTests
{
    [Fact]
    public void ConfirmationNote_SeparatesHighQueueDepthFromDesktopUse_AndNamesTheWriteRegression()
    {
        var note = MainViewModel.FirstRunExpectationNote;

        Assert.Contains("high queue depths", note, StringComparison.Ordinal);
        Assert.Contains("ordinary desktop use may see little or no difference", note, StringComparison.Ordinal);
        Assert.Contains("4K random writes slightly slower", note, StringComparison.Ordinal);
        Assert.Contains("StorageReview", note, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2014', note);
        Assert.DoesNotContain('\u2013', note);
    }

    [Fact]
    public void ApplyConfirmation_ListsTheNoteInTheGoodToKnowTier()
    {
        var source = ReadRepoFile("src", "NVMeDriverPatcher", "ViewModels", "MainViewModel.cs");
        var apply = source.IndexOf("if (title == \"Apply Patch\")\n        {\n            warnings.Add(\"Global scope", StringComparison.Ordinal);
        Assert.True(apply >= 0, "Apply branch of BuildConfirmMessage not found");
        Assert.Contains("notes.Insert(1, FirstRunExpectationNote);", source[apply..], StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_SaysTheSameThingAboveTheFoldAndInWhatDoesThisDo()
    {
        var readme = ReadRepoFile("README.md");
        var firstSection = readme.IndexOf("## Quick Start", StringComparison.Ordinal);
        var above = readme[..firstSection];
        Assert.Contains("high queue depths", above, StringComparison.Ordinal);
        Assert.Contains("little or no change", above, StringComparison.Ordinal);

        var what = readme.IndexOf("## What Does This Do?", StringComparison.Ordinal);
        var section = readme[what..readme.IndexOf("\n## ", what + 5, StringComparison.Ordinal)];
        Assert.Contains("high queue depths", section, StringComparison.Ordinal);
        Assert.Contains("little or no change", section, StringComparison.Ordinal);
        Assert.Contains("4K random writes slightly slower", section, StringComparison.Ordinal);
        Assert.Contains("https://www.storagereview.com/review/windows-server-native-nvme", section, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineOverviewDoc_NoLongerPromisesBroadGains()
    {
        var text = DocsService.Render("overview");

        Assert.Contains("high queue depths", text, StringComparison.Ordinal);
        Assert.Contains("4K random writes slightly slower", text.Replace("\r\n", " ").Replace("\n", " "), StringComparison.Ordinal);
        Assert.DoesNotContain("delivers large gains", text, StringComparison.Ordinal);
    }

    // LF-normalized: the multi-line IndexOf probes above spell "\n", and a CRLF checkout must match too.
    private static string ReadRepoFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts])).Replace("\r\n", "\n");

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
