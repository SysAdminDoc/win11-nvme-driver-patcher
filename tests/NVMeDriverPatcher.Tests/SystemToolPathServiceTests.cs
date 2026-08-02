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

    /// <summary>
    /// Any <c>"tool.exe"</c> literal that is not wrapped in a <see cref="SystemToolPathService"/>
    /// resolve call. The previous detector only matched a literal sitting directly inside
    /// <c>new ProcessStartInfo(</c>, so it stayed green while the watchdog shipped
    /// <c>RunProcess("sc.exe", args)</c> — one level of indirection was enough to hide the defect.
    /// Matching on the literal instead of on the call shape removes that escape hatch.
    /// </summary>
    private static readonly Regex UnresolvedToolLiteral = new(
        @"(?<!SystemToolPathService\.Resolve\()""[A-Za-z0-9_.\-]+\.exe""",
        RegexOptions.Compiled);

    /// <summary>
    /// Literals that name an executable without launching it: an asset/file name, a fallback for a
    /// path this process already owns, or a name being compared against. Each is a full line match
    /// so a launch site can never hide behind one.
    /// </summary>
    private static readonly Regex NonLaunchToolLiteral = new(
        @"(Environment\.ProcessPath|Path\.Combine|Directory\.GetFiles|Directory\.Enumerate|File\.Exists|const string|\.Equals\(|GetFileName\()",
        RegexOptions.Compiled);

    [Fact]
    public void NoShippedSourceLaunchesAToolByBareName()
    {
        // Both shipped executables carry a requireAdministrator manifest, so a bare tool name
        // resolves through the executable directory, the current directory and PATH, and would run
        // a planted binary elevated. This is the same shadowing that made the recovery kit's bare
        // `find` hang the integrity gate.

        // Self-check: the detector must fire on every shape it is meant to catch, including the two
        // that the pre-2026-08 regex missed.
        Assert.True(IsOffendingLine(@"var psi = new ProcessStartInfo(""fsutil.exe"")"));
        Assert.True(IsOffendingLine(@"=> RunProcess(""sc.exe"", args);"));            // watchdog defect
        Assert.True(IsOffendingLine(@"var bcd = RunCapture(""bcdedit.exe"", args);")); // WinRE probe defect
        Assert.True(IsOffendingLine(@"await runner(""dism.exe"","));                   // WinPE/WinRE defect

        // ...and must stay quiet on a resolved launch and on the non-launch shapes.
        Assert.False(IsOffendingLine(@"new ProcessStartInfo(SystemToolPathService.Resolve(""fsutil.exe""))"));
        Assert.False(IsOffendingLine(@"var exe = Environment.ProcessPath ?? ""NVMeDriverPatcher.exe"";"));
        Assert.False(IsOffendingLine(@"var p = Path.Combine(dir, ""diskspd.exe"");"));

        var offenders = ShippedSourceFiles("src")
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (path, line, number: index + 1))
                .Where(entry => IsOffendingLine(entry.line))
                .Select(entry => $"{Path.GetFileName(entry.path)}:{entry.number}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These lines name a tool without resolving it; route them through SystemToolPathService: {string.Join(", ", offenders)}");
    }

    [Fact]
    public async Task Resolve_IgnoresAToolPlantedInTheWorkingDirectory()
    {
        // The watchdog's control verbs only ever run elevated, so the concrete risk is a planted
        // sc.exe in the current directory being launched with a SYSTEM token. Prove the resolved
        // path is used by planting a stub that would be unmistakable if it ran.
        var plantDir = Path.Combine(Path.GetTempPath(), "nvme-plant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plantDir);
        try
        {
            var plant = Path.Combine(plantDir, "sc.exe");
            File.Copy(SystemToolPathService.Resolve("cmd.exe"), plant);

            var psi = new System.Diagnostics.ProcessStartInfo(SystemToolPathService.Resolve("sc.exe"))
            {
                WorkingDirectory = plantDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // A query for a service that does not exist: real sc.exe reports 1060, the planted
            // cmd.exe stub would sit waiting for input instead.
            psi.ArgumentList.Add("query");
            psi.ArgumentList.Add("NVMeDriverPatcherNoSuchService" + Guid.NewGuid().ToString("N"));

            using var proc = System.Diagnostics.Process.Start(psi)!;
            // Read asynchronously and bound the wait: a synchronous ReadToEnd turns a stub that
            // blocks on stdin into a wedged suite instead of one failing test.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                Assert.Fail("sc.exe did not exit — the planted stub may have run.");
            }

            Assert.Equal(1060, proc.ExitCode); // ERROR_SERVICE_DOES_NOT_EXIST
            Assert.Contains("1060", await stdout + await stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(plantDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static bool IsOffendingLine(string line)
    {
        var code = line.TrimStart();
        if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal))
            return false;
        return UnresolvedToolLiteral.IsMatch(line) && !NonLaunchToolLiteral.IsMatch(line);
    }

    private static IEnumerable<string> ShippedSourceFiles(params string[] relativeRoots) =>
        relativeRoots
            .Select(root => Path.Combine(RepoRoot(), root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
