namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Main parses every option once from its own args, aliases included. Program's handlers can't
/// run here (they sit behind the administrator gate and mutate the machine), so these pin the
/// wiring in the source instead.
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
