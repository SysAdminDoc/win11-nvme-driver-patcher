using System.Text.RegularExpressions;
using NVMeDriverPatcher.Cli;

namespace NVMeDriverPatcher.Tests;

public sealed class CliCommandRegistryTests
{
    [Fact]
    public void AllDescriptors_HaveRequiredFields()
    {
        foreach (var d in CliCommandRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Name), "descriptor name missing");
            Assert.False(string.IsNullOrWhiteSpace(d.Summary), $"{d.Name}: summary missing");
            Assert.True(Enum.IsDefined(d.Group), $"{d.Name}: invalid group");
            Assert.True(Enum.IsDefined(d.Risk), $"{d.Name}: invalid risk");
        }
    }

    [Fact]
    public void PrimaryNames_AreUnique()
    {
        var names = CliCommandRegistry.All.Select(d => d.Name).ToList();
        var dupes = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1)
                         .Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void AllAliases_AreUniqueAcrossDescriptors()
    {
        var allTokens = new List<string>();
        foreach (var d in CliCommandRegistry.All)
        {
            allTokens.Add(d.Name);
            allTokens.AddRange(d.Aliases);
        }
        var dupes = allTokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                             .Where(g => g.Count() > 1)
                             .Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void IsKnown_AcceptsEveryRegisteredToken()
    {
        foreach (var d in CliCommandRegistry.All)
        {
            Assert.True(CliCommandRegistry.IsKnown(d.Name), $"{d.Name} not recognized");
            foreach (var a in d.Aliases)
                Assert.True(CliCommandRegistry.IsKnown(a), $"alias {a} (of {d.Name}) not recognized");
        }
    }

    [Fact]
    public void IsKnown_RejectsUnknownCommands()
    {
        Assert.False(CliCommandRegistry.IsKnown("nonexistent"));
        Assert.False(CliCommandRegistry.IsKnown(""));
        Assert.False(CliCommandRegistry.IsKnown("format-c"));
    }

    [Fact]
    public void Find_ReturnsDescriptorForPrimaryAndAliases()
    {
        var d = CliCommandRegistry.Find("apply");
        Assert.NotNull(d);
        Assert.Equal("apply", d!.Name);

        var via = CliCommandRegistry.Find("install");
        Assert.NotNull(via);
        Assert.Equal("apply", via!.Name);
    }

    [Fact]
    public void Find_ReturnsNull_ForUnknownCommand()
    {
        Assert.Null(CliCommandRegistry.Find("nope"));
    }

    [Fact]
    public void EveryGroup_HasAtLeastOneDescriptor()
    {
        foreach (CommandGroup g in Enum.GetValues<CommandGroup>())
            Assert.True(CliCommandRegistry.All.Any(d => d.Group == g), $"group {g} has no descriptors");
    }

    [Fact]
    public void RenderUsage_ContainsEveryPrimaryCommand()
    {
        var usage = CliCommandRegistry.RenderUsage("test");
        foreach (var d in CliCommandRegistry.All)
            Assert.Contains(d.Name, usage);
    }

    [Fact]
    public void RenderUsage_ContainsAllGroupHeaders()
    {
        var usage = CliCommandRegistry.RenderUsage("test");
        Assert.Contains("Lifecycle:", usage);
        Assert.Contains("Recovery:", usage);
        Assert.Contains("Diagnostics:", usage);
        Assert.Contains("Fleet & Admin:", usage);
        Assert.Contains("Advanced:", usage);
    }

    [Fact]
    public void RenderUsage_MarksExperimentalCommands()
    {
        var usage = CliCommandRegistry.RenderUsage("test");
        Assert.Contains("[experimental]", usage);
    }

    [Fact]
    public void RenderUsage_WrapsLongCommandNamesBeforeSummary()
    {
        var usage = CliCommandRegistry.RenderUsage("test");

        Assert.Contains("    re-enable-after-update" + Environment.NewLine + "                          Re-apply", usage);
        Assert.DoesNotContain("re-enable-after-updateRe-apply", usage);
    }

    // Help text is the CLI's contract. These summaries used to describe jobs, modes and option
    // coverage the implementation never had.

    [Fact]
    public void RegisterTasksSummary_NamesTheTasksSchedulerServiceActuallyRegisters()
    {
        var summary = CliCommandRegistry.Find("register-tasks")!.Summary;

        // SchedulerService registers BootVerify (watchdog --auto-revert at start-up) and
        // WatchdogSweep; there is no benchmark-regression or firmware-nudge job.
        Assert.DoesNotContain("benchmark", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("firmware", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("boot", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("watchdog sweep", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TailSummary_DescribesAOneShotDumpNotALiveTail()
    {
        // EventLogTailService.Recent prints the last 60 minutes (up to 100 records) and exits.
        var summary = CliCommandRegistry.Find("tail")!.Summary;
        Assert.DoesNotMatch(new Regex(@"(?i)\blive\b|\bfollow"), summary);
        Assert.Contains("last hour", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WatchdogSummary_SaysAutoRevertRunsNowRatherThanArming()
    {
        // `watchdog --auto-revert` runs AutoRevertService immediately; it doesn't arm anything.
        var summary = CliCommandRegistry.Find("watchdog")!.Summary;
        Assert.DoesNotContain("to arm", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--auto-revert", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalJsonHelp_NamesEveryCommandThatHonorsJson()
    {
        var program = ReadRepoFile("src", "NVMeDriverPatcher.Cli", "Program.cs");

        // Handlers that take the json switch, then the command each call site routes from.
        var handlers = Regex.Matches(program, @"static int (?<name>\w+)\((?<params>[^)]*)\)")
            .Where(m => Regex.IsMatch(m.Groups["params"].Value, @"\bbool json\b"))
            .Select(m => m.Groups["name"].Value)
            .ToList();
        Assert.NotEmpty(handlers);

        var lines = program.Split('\n');
        var jsonCommands = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            var call = new Regex(@"\b" + handler + @"\(");
            for (var i = 0; i < lines.Length; i++)
            {
                if (!call.IsMatch(lines[i]) || lines[i].Contains("static int ", StringComparison.Ordinal)) continue;
                for (var j = i; j >= 0; j--)
                {
                    var command = Regex.Match(lines[j], "\"(?<cmd>[a-z][a-z0-9-]*)\"");
                    if (!command.Success) continue;
                    jsonCommands.Add(command.Groups["cmd"].Value);
                    break;
                }
            }
        }
        Assert.Contains("status", jsonCommands);
        Assert.Contains("verify-payload", jsonCommands);

        var usage = CliCommandRegistry.RenderUsage("test");
        var start = usage.IndexOf("  --json", StringComparison.Ordinal);
        var end = usage.IndexOf("Exit codes:", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "global --json help line not found");
        var jsonHelp = usage[start..end];

        foreach (var command in jsonCommands)
            Assert.True(
                Regex.IsMatch(jsonHelp, @"(?<![\w-])" + Regex.Escape(command) + @"(?![\w-])"),
                $"'{command}' honors --json but the global --json help doesn't name it:{Environment.NewLine}{jsonHelp}");
    }

    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NVMeDriverPatcher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"expected repo file missing: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void DescriptorCount_MatchesExpectedCommandSurface()
    {
        Assert.True(CliCommandRegistry.All.Length >= 42,
            $"Expected at least 42 descriptors, found {CliCommandRegistry.All.Length}");
    }
}
