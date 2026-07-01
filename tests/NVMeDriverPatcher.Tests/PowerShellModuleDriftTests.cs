using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NVMeDriverPatcher.Tests;

public sealed class PowerShellModuleDriftTests
{
    [Fact]
    public void ExportedCmdlets_MatchReadmeList()
    {
        var psd1 = File.ReadAllText(RepoPath("packaging", "powershell", "NVMeDriverPatcher.psd1"));
        var readme = File.ReadAllText(RepoPath("packaging", "powershell", "README.md"));

        var exported = Regex.Matches(psd1, @"'([A-Z][a-z]+-Nvme\w+)'")
            .Select(m => m.Groups[1].Value)
            .OrderBy(s => s)
            .ToList();

        var documented = Regex.Matches(readme, @"`([A-Z][a-z]+-Nvme\w+)`")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        var missingInReadme = exported.Except(documented).ToList();
        var extraInReadme = documented.Except(exported).ToList();

        Assert.True(missingInReadme.Count == 0,
            $"Cmdlets exported in .psd1 but missing from README: {string.Join(", ", missingInReadme)}");
        Assert.True(extraInReadme.Count == 0,
            $"Cmdlets documented in README but not exported in .psd1: {string.Join(", ", extraInReadme)}");
    }

    private static string RepoPath(params string[] parts)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CallerPath())!, "..", ".."));
        return Path.Combine(new[] { repoRoot }.Concat(parts).ToArray());
    }

    private static string CallerPath([CallerFilePath] string path = "") => path;
}
