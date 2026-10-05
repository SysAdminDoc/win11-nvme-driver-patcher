using System.Text;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Cli;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Redirected CLI output went out in the OEM code page, so `dry-run > plan.md` lost the arrow in
/// "Before → After". The CLI can't be launched from here (it requires elevation), so these pin the
/// writer it installs and the wiring in Main.
/// </summary>
public sealed class CliConsoleEncodingTests
{
    [Fact]
    public void RedirectedWriter_WritesUtf8WithoutABom()
    {
        using var stream = new MemoryStream();
        var writer = CliConsoleEncoding.CreateWriter(stream);
        writer.Write("Before → After, t4/o16 ≈ QD64");

        // AutoFlush: the bytes are there without a Flush, which matters when a command returns early.
        var bytes = stream.ToArray();
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "redirected output must not start with a BOM");
        Assert.Equal("Before → After, t4/o16 ≈ QD64", Encoding.UTF8.GetString(bytes));
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0xE2, 0x86, 0x92 }) >= 0, "the arrow should be its UTF-8 bytes");
    }

    [Fact]
    public void Main_InstallsTheWritersBeforeAnythingPrints()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher.Cli", "Program.cs"));
        var main = Regex.Match(program, @"static int Main\(string\[\] args\)\s*\{\s*(?<first>[^;]*;)");

        Assert.True(main.Success, "Main not found");
        Assert.Equal("CliConsoleEncoding.UseUtf8WhenRedirected();", main.Groups["first"].Value.Trim());
    }

    [Fact]
    public void Cli_NeverChangesTheConsoleCodePage()
    {
        // Console.OutputEncoding's setter calls SetConsoleOutputCP, which outlives the process and
        // changes the code page of the shell that launched the CLI.
        var cliDir = Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher.Cli");
        var offenders = Directory.EnumerateFiles(cliDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"Console\.(Output|Input)Encoding\s*=(?!=)"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NVMeDriverPatcher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
