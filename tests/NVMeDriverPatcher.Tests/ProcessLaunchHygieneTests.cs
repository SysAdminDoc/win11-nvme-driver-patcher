using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Every child process this app launches drains stdout and stderr asynchronously before it waits.
/// A synchronous ReadToEnd on one pipe blocks until the child exits, and the child blocks once the
/// other pipe's buffer fills, so the two wait on each other and no WaitForExit timeout ever runs.
/// The BCD test-signing probe shipped that shape for a while; this keeps it from coming back.
/// </summary>
public sealed class ProcessLaunchHygieneTests
{
    private static readonly Regex SyncPipeRead = new(@"\.Standard(Output|Error)\.ReadToEnd\(\)", RegexOptions.Compiled);

    [Fact]
    public void NoLauncherReadsAProcessPipeSynchronously()
    {
        var offenders = new List<string>();
        foreach (var file in ShippedSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (SyncPipeRead.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Process pipes must be drained with ReadToEndAsync before WaitForExit (see the manage-bde launcher in PatchService):\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void TheGateReadsTheRealSourceTree()
    {
        // A gate over zero files passes for the wrong reason.
        var files = ShippedSourceFiles().ToList();
        Assert.Contains(files, f => f.EndsWith(Path.Combine("Services", "PreflightService.cs"), StringComparison.OrdinalIgnoreCase));
        Assert.True(files.Count > 50, $"only {files.Count} source files found under src");
        Assert.Matches(SyncPipeRead, "var stdout = proc.StandardOutput.ReadToEnd();");
        Assert.DoesNotMatch(SyncPipeRead, "var stdoutTask = proc.StandardOutput.ReadToEndAsync();");
    }

    private static IEnumerable<string> ShippedSourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
