using System.Text.RegularExpressions;
using NVMeDriverPatcher.Cli;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Main parses every option once from its own args, aliases included. Most of Program's handlers
/// can't run here (they sit behind the administrator gate and mutate the machine), so these test
/// the parsing helpers directly and pin the wiring in the source.
/// </summary>
public sealed class CliProgramOptionTests
{
    [Fact]
    public void Program_NeverRescansTheRawCommandLine()
    {
        // fallback used to re-scan Environment.GetCommandLineArgs() for "--force" only, so
        // `fallback -f` refused on a failed recovery proof while `fallback --force` proceeded.
        Assert.DoesNotContain("GetCommandLineArgs", ReadCliProgram(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fallback_ReceivesTheParsedForceSwitch()
    {
        Assert.Matches(@"=>\s*FallbackCommand\(config,\s*force\b", ReadCliProgram());
    }

    [Theory]
    [InlineData("--threshold=abc", "abc")]
    [InlineData("--threshold=", "")]
    [InlineData("--threshold=7.5", "7.5")]
    [InlineData("--THRESHOLD=5%%", "5%%")]
    public void MalformedThreshold_IsAUsageErrorNamingTheValue(string option, string value)
    {
        var error = CliCommandRegistry.ReadThresholdOption(["compare-benchmarks", option], out var percent);

        Assert.NotNull(error);
        Assert.Contains($"'{value}'", error, StringComparison.Ordinal);
        Assert.Equal(CliCommandRegistry.DefaultThresholdPercent, percent);
    }

    [Theory]
    [InlineData(null, 15)]
    [InlineData("--threshold=5%", 5)]
    [InlineData("--threshold=25", 25)]
    public void ValidOrAbsentThreshold_IsNotAnError(string? option, int expected)
    {
        string?[] args = option is null ? ["compare-benchmarks"] : ["compare-benchmarks", option];

        Assert.Null(CliCommandRegistry.ReadThresholdOption(args, out var percent));
        Assert.Equal(expected, percent);
    }

    [Fact]
    public void ThresholdIsValidatedAsAUsageErrorBeforeTheAdministratorGate()
    {
        var program = ReadCliProgram();
        var read = program.IndexOf("CliCommandRegistry.ReadThresholdOption(", StringComparison.Ordinal);
        var adminGate = program.IndexOf("PreflightService.IsRunningAsAdmin()", StringComparison.Ordinal);

        Assert.True(read >= 0, "Program no longer validates --threshold");
        Assert.True(adminGate > read, "--threshold must be rejected like an unknown option, before elevation matters");
        Assert.Matches(@"ReadThresholdOption\([^;]*;\s*if \(thresholdError is not null\)\s*\{[^}]*return 3;", program[read..]);
        Assert.DoesNotContain("int thresholdArg = 15", program, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigMigrationFailure_IsReportedNotSwallowed()
    {
        var program = ReadCliProgram();
        var call = program.IndexOf("ConfigMigrationService.Migrate(", StringComparison.Ordinal);
        Assert.True(call >= 0, "Program no longer runs the config migration");

        // Body runs to the first line that closes a block, so braces inside interpolated strings don't cut it short.
        var handler = Regex.Match(program[call..], @"(?s)catch\s*(?<filter>\([^)]*\))?\s*\{(?<body>.*?)\n\s*\}");
        Assert.True(handler.Success, "no handler after the config migration call");
        Assert.Contains("Exception", handler.Groups["filter"].Value, StringComparison.Ordinal);
        Assert.Contains("Console.Error.WriteLine", handler.Groups["body"].Value, StringComparison.Ordinal);

        // The event log is only initialized (and the user's WriteEventLog choice honored) after
        // the migration, so the failure is recorded there once it's ready.
        var initialize = program.IndexOf("EventLogService.Initialize(", call, StringComparison.Ordinal);
        Assert.True(initialize > call, "event log initialization moved ahead of the migration");
        Assert.Matches(@"EventLogService\.Write\(\s*migrationFailure", program[initialize..]);
    }

    private static string ReadCliProgram() => ReadRepoFile("src", "NVMeDriverPatcher.Cli", "Program.cs");

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
}
