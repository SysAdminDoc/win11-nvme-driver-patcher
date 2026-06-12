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
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(ScriptPath());
        process.StartInfo.ArgumentList.Add("-RepoRoot");
        process.StartInfo.ArgumentList.Add(repoRoot);

        process.Start();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);

        return new ScriptResult(process.ExitCode, stdOut, stdErr);
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
