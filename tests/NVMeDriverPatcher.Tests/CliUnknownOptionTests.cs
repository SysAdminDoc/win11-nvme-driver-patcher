using NVMeDriverPatcher.Cli;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Every CLI option is detected with <c>args.Any(...)</c>, so anything unrecognised used to be
/// silently ignored. On a tool that mutates boot-storage configuration that is dangerous:
/// <c>apply --unattended --dryrun</c> — one missing hyphen — performed a real apply AND an
/// automatic reboot instead of the preview the user asked for.
/// </summary>
public sealed class CliUnknownOptionTests
{
    [Theory]
    [InlineData("--dryrun")]        // the reboot-causing typo
    [InlineData("--previeww")]
    [InlineData("--safemode")]
    [InlineData("--no-restrat")]
    [InlineData("--nonsense")]
    public void UnknownOptions_AreRejected(string option)
    {
        Assert.Equal(option, CliCommandRegistry.FindUnknownOption(new[] { "apply", "--unattended", option }));
    }

    [Theory]
    [InlineData("--dry-run")]
    [InlineData("--preview")]
    [InlineData("--safe")]
    [InlineData("--full-mode")]
    [InlineData("-f")]
    [InlineData("--force-unsupported-build")]
    [InlineData("--json")]
    [InlineData("--write-native")]
    [InlineData("--on")]
    [InlineData("--reset")]
    [InlineData("--threshold=25")]
    [InlineData("--max=4")]
    [InlineData("--input=C:/kit")]
    [InlineData("--output=C:/out.iso")]
    public void DocumentedOptions_AreAccepted(string option)
    {
        Assert.Null(CliCommandRegistry.FindUnknownOption(new[] { "apply", option }));
    }

    [Fact]
    public void PositionalArgumentsAndCommands_AreNotTreatedAsOptions()
    {
        Assert.Null(CliCommandRegistry.FindUnknownOption(new[] { "verify-payload", @"C:\kits\NVMe_Recovery_Kit" }));
    }

    [Theory]
    [InlineData("--dryrun", "--dry-run")]
    [InlineData("--no-restrat", "--no-restart")]
    [InlineData("--jsn", "--json")]
    public void NearMisses_SuggestTheIntendedOption(string typo, string expected)
    {
        Assert.Equal(expected, CliCommandRegistry.SuggestOption(typo));
    }

    [Fact]
    public void UnrelatedInput_SuggestsNothingRatherThanGuessing()
    {
        Assert.Null(CliCommandRegistry.SuggestOption("--wildly-unrelated-token"));
    }
}
