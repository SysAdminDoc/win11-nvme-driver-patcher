using System.Runtime.CompilerServices;
using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// The workspace summaries used to be rebuilt on the UI thread: a directory scan, three SQLite reads
/// and a registry read on startup, after every command and inside the preflight render. The gather
/// now runs on the thread pool and hands back a plain snapshot that the UI thread only applies.
/// </summary>
public sealed class OperationalHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_HistoryTests_" + Guid.NewGuid().ToString("N"));

    public OperationalHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Gather_ReadsTheWorkingFolderOffTheCallingContextAndReturnsPlainData()
    {
        File.WriteAllText(Path.Combine(_dir, "Pre_Patch_Backup_20260420.reg"), "x");
        File.WriteAllText(Path.Combine(_dir, "Verify_NVMe_Patch.ps1"), "x");
        File.WriteAllText(Path.Combine(_dir, "NVMe_Diagnostics_20260420.txt"), "x");
        var kit = Path.Combine(_dir, "NVMe_Recovery_Kit");
        Directory.CreateDirectory(kit);
        File.WriteAllText(Path.Combine(kit, "README.txt"), "x");

        var inputs = new MainViewModel.OperationalHistoryInputs(_dir, null, null, null);
        var snapshot = await Task.Run(() => MainViewModel.GatherOperationalHistory(inputs));

        Assert.True(snapshot.HasBackupFiles);
        Assert.Equal(kit, snapshot.RecoveryKitPath);
        Assert.True(snapshot.RecoveryKitInWorkingFolder);
        Assert.Equal(Path.Combine(_dir, "Verify_NVMe_Patch.ps1"), snapshot.VerificationScriptPath);
        Assert.EndsWith("NVMe_Diagnostics_20260420.txt", snapshot.DiagnosticsReportPath, StringComparison.Ordinal);
        Assert.False(snapshot.RecoveryKitReadFailed || snapshot.VerificationScriptReadFailed || snapshot.DiagnosticsReportReadFailed);
    }

    [Fact]
    public void Gather_OfAnEmptyFolder_ReportsNothingPresent()
    {
        var snapshot = MainViewModel.GatherOperationalHistory(new MainViewModel.OperationalHistoryInputs(_dir, null, null, null));

        Assert.False(snapshot.HasBackupFiles);
        Assert.Null(snapshot.RecoveryKitPath);
        Assert.Null(snapshot.VerificationScriptPath);
        Assert.Null(snapshot.DiagnosticsReportPath);
    }

    [Fact]
    public void RefreshHistory_GathersInsideTaskRun_AndTheUiStepDoesNoIo()
    {
        var source = ReadSource("MainViewModel.Workspace.cs");

        Assert.Contains("private void UpdateOperationalHistory() => _ = RefreshOperationalHistoryAsync();", source, StringComparison.Ordinal);

        var refresh = MethodBody(source, "internal async Task RefreshOperationalHistoryAsync()");
        Assert.Contains("await Task.Run(() => GatherOperationalHistory(inputs))", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("DataService.", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", refresh, StringComparison.Ordinal);

        // The gather must not touch the dispatcher or any bound state, or it couldn't run off-thread.
        var gather = MethodBody(source, "internal static OperationalHistorySnapshot GatherOperationalHistory(");
        foreach (var forbidden in new[] { "Dispatcher", "Application.", "HasRecoveryKit", "StatusText", "Log(" })
            Assert.DoesNotContain(forbidden, gather, StringComparison.Ordinal);
        Assert.Contains("DataService.GetBenchmarkHistory()", gather, StringComparison.Ordinal);

        var apply = MethodBody(source, "private void ApplyOperationalHistory(");
        foreach (var io in new[] { "DataService.", "Directory.", "File.", "FileInfo", "RegistryService." })
            Assert.DoesNotContain(io, apply, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_ReadsTheBenchmarkHistoryOncePerCycle_OffTheUiThread()
    {
        var source = ReadSource("MainViewModel.cs");
        var preflight = MethodBody(source, "public async Task RunPreflightAsync()");

        Assert.Equal(1, CountOf(preflight, "BenchmarkService.GetHistory("));
        Assert.Contains("await Task.Run(() => BenchmarkService.GetHistory(Config.WorkingDir))", preflight, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(preflight, "UpdateOverviewSummary(benchmarkHistory)"));
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) { count++; index += needle.Length; }
        return count;
    }

    // Text of the method that starts at the signature, up to its matching closing brace.
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new InvalidOperationException("Unbalanced braces after " + signature);
    }

    private static string ReadSource(string file, [CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile)!, "..", "..", "src", "NVMeDriverPatcher", "ViewModels", file)));
}
