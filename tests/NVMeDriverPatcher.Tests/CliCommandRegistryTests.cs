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
    public void DescriptorCount_MatchesExpectedCommandSurface()
    {
        Assert.True(CliCommandRegistry.All.Length >= 42,
            $"Expected at least 42 descriptors, found {CliCommandRegistry.All.Length}");
    }
}
