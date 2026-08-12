using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NVMeDriverPatcher.Tests;

public sealed class RootHygieneScriptTests
{
    [Fact]
    public void ValidateRootHygiene_AllowsSupportedRootFiles()
    {
        using var workspace = new TempRoot();
        File.WriteAllText(Path.Combine(workspace.Path, "README.md"), "# test");
        File.WriteAllText(Path.Combine(workspace.Path, "icon.png"), "test");
        File.WriteAllText(Path.Combine(workspace.Path, "NVMe_Driver_Patcher.ps1"), "Write-Host ok");

        var result = RunScript(workspace.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("passed", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LibreSpot.ps1")]
    [InlineData("_wpf_test.ps1")]
    [InlineData("NVMe_Driver_Patcher_winforms_backup.ps1")]
    [InlineData("icon - Copy.png")]
    public void ValidateRootHygiene_BlocksKnownRootArtifacts(string fileName)
    {
        using var workspace = new TempRoot();
        File.WriteAllText(Path.Combine(workspace.Path, fileName), "leftover");

        var result = RunScript(workspace.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(fileName, result.StdOut);
    }

    private static ScriptResult RunScript(string repoRoot)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(ScriptPath());
        startInfo.ArgumentList.Add("-RepoRoot");
        startInfo.ArgumentList.Add(repoRoot);

        var result = TestProcessRunner.Run(startInfo, TimeSpan.FromSeconds(10));

        return new ScriptResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    private static string ScriptPath([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(repoRoot, "scripts", "Validate-RootHygiene.ps1");
    }

    private sealed record ScriptResult(int ExitCode, string StdOut, string StdErr);

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"NVMeDriverPatcher.RootHygiene.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
