using System.Text;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Redirected CLI output went out in the OEM code page, so `dry-run > plan.md` lost the arrow in
/// "Before → After". The CLI and the Watchdog exe can't be launched from here (the CLI requires
/// elevation), so these pin the writer they install and the wiring in each Main.
/// </summary>
public sealed class RedirectedConsoleEncodingTests
{
    [Fact]
    public void RedirectedWriter_WritesUtf8WithoutABom()
    {
        using var stream = new MemoryStream();
        var writer = RedirectedConsoleEncoding.CreateWriter(stream);
        writer.Write("Before → After, t4/o16 ≈ QD64");

        // AutoFlush: the bytes are there without a Flush, which matters when a command returns early.
        var bytes = stream.ToArray();
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "redirected output must not start with a BOM");
        Assert.Equal("Before → After, t4/o16 ≈ QD64", Encoding.UTF8.GetString(bytes));
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0xE2, 0x86, 0x92 }) >= 0, "the arrow should be its UTF-8 bytes");
    }

    [Theory]
    [InlineData("NVMeDriverPatcher.Cli", @"static int Main\(string\[\] args\)")]
    [InlineData("NVMeDriverPatcher.Watchdog", @"static async Task<int> Main\(string\[\] args\)")]
    public void Main_InstallsTheWritersBeforeAnythingPrints(string project, string signature)
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", project, "Program.cs"));
        var main = Regex.Match(program, signature + @"\s*\{\s*(?<first>[^;]*;)");

        Assert.True(main.Success, $"{project} Main not found");
        Assert.Equal("RedirectedConsoleEncoding.UseUtf8WhenRedirected();", main.Groups["first"].Value.Trim());
    }

    [Theory]
    [InlineData("NVMeDriverPatcher.Cli")]
    [InlineData("NVMeDriverPatcher.Watchdog")]
    [InlineData("NVMeDriverPatcher.Core")]
    public void ConsoleProjects_NeverChangeTheConsoleCodePage(string project)
    {
        // Console.OutputEncoding's setter calls SetConsoleOutputCP, which outlives the process and
        // changes the code page of the shell that launched the CLI or the Watchdog.
        var dir = Path.Combine(RepoRoot(), "src", project);
        var offenders = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"Console\.(Output|Input)Encoding\s*=(?!=)"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("packaging/powershell/NVMeDriverPatcher.psm1")]
    [InlineData("packaging/intune/Remediate-NVMeDriverPatcher.ps1")]
    public void PowerShellCallers_DecodeUtf8AndPutTheConsoleEncodingBack(string relative)
    {
        // Windows PowerShell 5.1 decodes a native program's output with [Console]::OutputEncoding,
        // so the scripts that parse the CLI's output switch it for the call and restore it after.
        var script = File.ReadAllText(Path.Combine(RepoRoot(), relative));

        Assert.Contains("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)", script);
        Assert.Contains("[Console]::OutputEncoding = $previousEncoding", script);
        Assert.Matches(@"finally\s*\{[^}]*\[Console\]::OutputEncoding = \$previousEncoding", script);
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
