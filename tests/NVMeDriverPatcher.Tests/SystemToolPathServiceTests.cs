using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class SystemToolPathServiceTests
{
    [Theory]
    [InlineData("fsutil.exe")]
    [InlineData("mountvol.exe")]
    [InlineData("manage-bde.exe")]
    [InlineData("bcdedit.exe")]
    [InlineData("pnputil.exe")]
    [InlineData("schtasks.exe")]
    [InlineData("shutdown.exe")]
    public void Resolve_ReturnsAnExistingAbsoluteSystem32Path(string tool)
    {
        var resolved = SystemToolPathService.Resolve(tool);

        Assert.True(Path.IsPathFullyQualified(resolved), $"{tool} did not resolve to an absolute path.");
        Assert.True(File.Exists(resolved), $"{tool} resolved to a path that does not exist: {resolved}");
        Assert.Equal(tool, Path.GetFileName(resolved), ignoreCase: true);
    }

    [Fact]
    public void PowerShell_ResolvesToWindowsPowerShellUnderSystem32()
    {
        var resolved = SystemToolPathService.PowerShell;

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.True(File.Exists(resolved), $"powershell.exe not found at {resolved}");
        Assert.Contains(@"WindowsPowerShell\v1.0", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_RejectsAnEmptyToolName(string tool) =>
        Assert.Throws<ArgumentException>(() => SystemToolPathService.Resolve(tool));

    [Fact]
    public void NoShippedSourceLaunchesAToolByBareName()
    {
        // Both shipped executables carry a requireAdministrator manifest, so a bare tool name in
        // ProcessStartInfo resolves through PATH and would run a planted binary elevated. This is
        // the same shadowing that made the recovery kit's bare `find` hang the integrity gate.
        var bareName = new Regex(@"new ProcessStartInfo\(\s*""[^""\\/]+""", RegexOptions.Compiled);

        // Self-check: the detector must fire on the shape it is meant to catch.
        Assert.Matches(bareName, @"var psi = new ProcessStartInfo(""fsutil.exe"")");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => bareName.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These files launch a process by bare name; route them through SystemToolPathService: {string.Join(", ", offenders)}");
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
