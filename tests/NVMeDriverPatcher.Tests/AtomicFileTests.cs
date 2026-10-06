using System.Collections.Concurrent;
using System.IO;
using System.Text;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// The GUI, the CLI, the tray and the SYSTEM scheduled task share one working directory. Every
// writer here used `path + ".tmp"` as its staging name, so two of them at once truncated or
// stole each other's staging file and one update vanished into an empty catch.
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "NVMePatcher_AtomicFile_" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ParallelWriters_ToOneFile_AllSucceedAndLeaveOneWholePayloadAndNoStagingFiles()
    {
        var path = Path.Combine(_dir, "state.json");
        var payloads = Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 64 * 1024) + i).ToArray();
        var failures = new ConcurrentBag<Exception>();

        Parallel.For(0, payloads.Length, new ParallelOptions { MaxDegreeOfParallelism = payloads.Length }, i =>
        {
            for (var round = 0; round < 25; round++)
            {
                try { AtomicFile.WriteAllText(path, payloads[i]); }
                catch (Exception ex) { failures.Add(ex); }
            }
        });

        Assert.Empty(failures.Select(f => f.GetType().Name + ": " + f.Message).Distinct());
        Assert.Contains(File.ReadAllText(path), payloads);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task WriteAllText_TargetHeldOpenByAReader_StillLandsOnceTheReaderLetsGo()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "old");
        using var gate = new ManualResetEventSlim(false);
        var reader = Task.Run(() =>
        {
            // File.ReadAllText opens like this: no FileShare.Delete, so a rename over the file
            // is refused while the handle is open.
            using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            gate.Set();
            Thread.Sleep(60);
        });
        gate.Wait();

        AtomicFile.WriteAllText(path, "new");

        await reader;
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void WriteAllText_PublishFails_ThrowsAndLeavesNoStagingFile()
    {
        // A directory where the file should go: the staging write works, the rename can't.
        var path = Directory.CreateDirectory(Path.Combine(_dir, "taken")).FullName;

        var ex = Record.Exception(() => AtomicFile.WriteAllText(path, "x"));
        Assert.True(ex is IOException or UnauthorizedAccessException, ex?.ToString());

        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void WriteAllText_EncodingDefaultsToUtf8WithoutBomAndHonorsAnExplicitOne()
    {
        var utf8 = Path.Combine(_dir, "utf8.txt");
        var utf16 = Path.Combine(_dir, "utf16.reg");

        AtomicFile.WriteAllText(utf8, "h\u00e9llo");
        AtomicFile.WriteAllText(utf16, "h\u00e9llo", Encoding.Unicode);

        Assert.Equal(new UTF8Encoding(false).GetBytes("h\u00e9llo"), File.ReadAllBytes(utf8));
        Assert.Equal(new byte[] { 0xFF, 0xFE }, File.ReadAllBytes(utf16).Take(2));
        Assert.Equal("h\u00e9llo", File.ReadAllText(utf16));
    }

    [Fact]
    public void WriteAllLines_MatchesFileWriteAllLines()
    {
        var expected = Path.Combine(_dir, "expected.txt");
        var actual = Path.Combine(_dir, "actual.txt");
        var lines = new[] { "first", "", "third line" };

        File.WriteAllLines(expected, lines);
        AtomicFile.WriteAllLines(actual, lines);

        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
    }

    [Fact]
    public void StagingPath_CarriesTheProcessIdAndNeverRepeats()
    {
        var a = AtomicFile.StagingPath(@"C:\x\y.json");
        var b = AtomicFile.StagingPath(@"C:\x\y.json");

        Assert.StartsWith(@"C:\x\y.json." + Environment.ProcessId + ".", a);
        Assert.EndsWith(".tmp", a);
        Assert.NotEqual(a, b);
    }

    // --- The read-modify-write of benchmark_results.json ---

    [Fact]
    public void SaveResults_ParallelWriters_KeepEveryEntry()
    {
        var results = Enumerable.Range(0, 8)
            .Select(i => new BenchmarkResult { Label = "run-" + i, Timestamp = $"2026-10-05T00:00:0{i}Z" })
            .ToArray();

        Parallel.ForEach(results, new ParallelOptions { MaxDegreeOfParallelism = results.Length },
            r => BenchmarkService.SaveResults(_dir, r));

        var history = BenchmarkService.GetHistory(_dir);
        Assert.Equal(results.Select(r => r.Label).Order(), history.Select(h => h.Label).Order());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt"));
    }

    [Fact]
    public void SaveResults_KeepsTheTenNewest()
    {
        for (var i = 0; i < BenchmarkService.HistoryLength + 3; i++)
            BenchmarkService.SaveResults(_dir, new BenchmarkResult { Label = "run-" + i });

        var labels = BenchmarkService.GetHistory(_dir).Select(h => h.Label).ToList();
        Assert.Equal(BenchmarkService.HistoryLength, labels.Count);
        Assert.Equal("run-3", labels[0]);
        Assert.Equal("run-12", labels[^1]);
    }

    // --- The baseline compare-benchmarks reads ---

    [Fact]
    public void SaveBaseline_RoundTripsAndReportsSuccess()
    {
        var config = new AppConfig { WorkingDir = Path.Combine(_dir, "nested", "work") };
        var log = new List<string>();

        Assert.True(AutoBenchmarkService.SaveBaseline(config, new BenchmarkBaseline { ReadIops = 123_456, WriteIops = 7 }, log.Add));

        var loaded = AutoBenchmarkService.LoadBaseline(config);
        Assert.Equal(123_456, loaded!.ReadIops);
        Assert.Empty(log);
        Assert.Empty(Directory.GetFiles(config.WorkingDir, "*.tmp"));
    }

    [Fact]
    public void SaveBaseline_CannotWrite_ReturnsFalseLogsAndKeepsTheOldBaseline()
    {
        // The working directory path is taken by a file, so neither the directory nor the
        // baseline can be created. This used to throw out of SaveBaseline.
        var blocker = Path.Combine(_dir, "work");
        File.WriteAllText(blocker, "not a directory");
        var config = new AppConfig { WorkingDir = blocker };
        var log = new List<string>();

        Assert.False(AutoBenchmarkService.SaveBaseline(config, new BenchmarkBaseline { ReadIops = 1 }, log.Add));

        var line = Assert.Single(log);
        Assert.Contains("baseline not saved", line, StringComparison.Ordinal);
        Assert.Equal("not a directory", File.ReadAllText(blocker));
    }
}
