using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The activity log rail had four small defects in one surface: the clear prompt said "entrys",
/// minidump warnings were logged as "WARN" (uncounted by the warning badge and rendered among
/// "[WARNING]" lines), Copy Selection let a clipboard-lock COMException reach the crash dialog,
/// and every appended line re-joined and re-rendered the whole visible log.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ActivityLogTests
{
    [Theory]
    [InlineData(1, "This clears 1 activity entry from the current session.")]
    [InlineData(2, "This clears 2 activity entries from the current session.")]
    [InlineData(5000, "This clears 5000 activity entries from the current session.")]
    public void ClearLogPrompt_PluralizesEntry(int count, string expectedOpening)
    {
        var prompt = MainViewModel.BuildClearLogPrompt(count);

        Assert.StartsWith(expectedOpening, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("entrys", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // The rail glued a bound count to a literal Run (" events"), rendering "4  events" and "1 events",
    // and called the same warnings "advisories" in one place and "warnings" in the next.
    [Theory]
    [InlineData(1, 1, 1, "1 entry", "1 warning", "1 error")]
    [InlineData(4, 0, 2, "4 entries", "0 warnings", "2 errors")]
    public void ActivityCounts_ReadAsPluralizedWords(int entries, int warnings, int errors,
        string entryText, string warningText, string errorText)
    {
        WpfTestHost.Run(() =>
        {
            var vm = new MainViewModel
            {
                LogEntryCount = entries,
                LogWarningCount = warnings,
                LogErrorCount = errors
            };

            Assert.Equal(entryText, vm.LogEntryCountText);
            Assert.Equal(warningText, vm.LogWarningCountText);
            Assert.Equal(errorText, vm.LogErrorCountText);
        });
    }

    // Levels AppendLogEntry understands. INFO and DEBUG are deliberately uncounted; anything
    // else (the old "WARN") is silently dropped from the badge counters.
    private static readonly HashSet<string> RecognizedLevels =
        ["INFO", "DEBUG", "SUCCESS", "WARNING", "ERROR"];

    // The level is the last argument of Log(...): either a literal or a ternary of two literals.
    private static readonly Regex LogLevelLiteral = new(
        @"\bLog\([^;]*?,\s*(?:[^;?]*\?\s*)?""(?<a>[A-Z]+)""(?:\s*:\s*""(?<b>[A-Z]+)"")?\s*\)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void EveryLoggedLevelLiteral_IsOneTheActivityCountersRecognize()
    {
        // Self-check: the detector must flag the original defect shape, or a pass means nothing.
        var original = Levels("Log($\"  [NVMe] {d.CreatedUtc:u} {Path.GetFileName(d.FilePath)}: {d.Notes}\", \"WARN\");");
        Assert.Equal(["WARN"], original);

        var offenders = new List<string>();
        var seen = 0;
        foreach (var file in GuiSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var level in Levels(lines[i]))
                {
                    seen++;
                    if (!RecognizedLevels.Contains(level))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1} logs level \"{level}\"");
                }
            }
        }

        // Dozens of call sites exist; a near-zero count means the pattern stopped matching.
        Assert.True(seen >= 50, $"Only {seen} log level literals found; the scan pattern is broken.");
        Assert.True(offenders.Count == 0,
            "Log levels the activity counters never see:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryGuiClipboardWrite_SitsDirectlyInsideATryBlock()
    {
        // Self-check against the original Copy Selection shape, which had no guard at all.
        const string original = """
            private void CopySelection_Click(object sender, RoutedEventArgs e)
            {
                string selectedText = ActivityRailLogOutput.SelectedText;

                if (!string.IsNullOrEmpty(selectedText))
                    Clipboard.SetText(selectedText);
            }
            """;
        Assert.False(IsDirectlyInsideTry(original, original.IndexOf("Clipboard.SetText", StringComparison.Ordinal)));

        var unguarded = new List<string>();
        var sites = 0;
        foreach (var file in GuiSourceFiles())
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, @"\bClipboard\.Set(?:Text|DataObject|Data|FileDropList|Image)\("))
            {
                sites++;
                if (!IsDirectlyInsideTry(source, match.Index))
                {
                    var line = source.AsSpan(0, match.Index).Count('\n') + 1;
                    unguarded.Add($"{Path.GetFileName(file)}:{line}");
                }
            }
        }

        // Copy Entire Log and Copy Selection at minimum.
        Assert.True(sites >= 2, $"Only {sites} clipboard writes found; the scan pattern is broken.");
        Assert.True(unguarded.Count == 0,
            "Clipboard writes throw COMException (CLIPBRD_E_CANT_OPEN) whenever another app holds the " +
            "clipboard, which reaches the crash dialog unless caught:\n" + string.Join("\n", unguarded));
    }

    [Fact]
    public void LogBurst_RefreshesTheLogTextOnceInsteadOfOncePerLine()
    {
        WpfTestHost.Run(() =>
        {
            var vm = new MainViewModel();
            Pump();

            var refreshes = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.LogText)) refreshes++;
            };

            const int burst = 200;
            var before = vm.LogEntryCount;
            for (var i = 0; i < burst; i++)
                vm.Log($"burst line {i}");

            // Counters stay live per line; only the expensive full-text refresh waits.
            Assert.Equal(before + burst, vm.LogEntryCount);
            Assert.Equal(0, refreshes);

            Pump();

            Assert.Equal(1, refreshes);
            Assert.EndsWith("burst line 199", vm.LogText, StringComparison.Ordinal);

            // A later line schedules a fresh refresh rather than being swallowed.
            vm.Log("after the burst");
            Pump();
            Assert.Equal(2, refreshes);
            Assert.EndsWith("after the burst", vm.LogText, StringComparison.Ordinal);
        });
    }

    // Runs every queued dispatcher operation at Background priority or above. Same-priority
    // operations run in FIFO order, so the refresh queued before this one has run by now.
    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static IEnumerable<string> Levels(string line)
    {
        foreach (Match match in LogLevelLiteral.Matches(line))
        {
            yield return match.Groups["a"].Value;
            if (match.Groups["b"].Success) yield return match.Groups["b"].Value;
        }
    }

    // True when the nearest enclosing block of the character at index is a try block.
    private static bool IsDirectlyInsideTry(string source, int index)
    {
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            switch (source[i])
            {
                case '}':
                    depth++;
                    break;
                case '{' when depth == 0:
                    return source[..i].TrimEnd().EndsWith("try", StringComparison.Ordinal);
                case '{':
                    depth--;
                    break;
            }
        }
        return false;
    }

    private static IEnumerable<string> GuiSourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
