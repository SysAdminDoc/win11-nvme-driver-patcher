using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BenchmarkHistoryCacheTests : IDisposable
{
    private readonly string _workingDir = Path.Combine(
        Path.GetTempPath(),
        $"NVMePatcher_BenchmarkCache_{Guid.NewGuid():N}");

    public BenchmarkHistoryCacheTests()
    {
        Directory.CreateDirectory(_workingDir);
    }

    [Fact]
    public void UnchangedHistory_ReturnsCachedParse_AndInvalidationReloads()
    {
        var historyPath = Path.Combine(_workingDir, "benchmark_results.json");
        File.WriteAllText(historyPath, """
            [{"Label":"baseline","Timestamp":"2026-08-02T12:00:00Z","Read":{"IOPS":42000},"Write":{"IOPS":51000}}]
            """);

        var cache = new BenchmarkHistoryCache();
        var first = cache.Get(_workingDir);
        var second = cache.Get(_workingDir);

        Assert.Same(first, second);
        Assert.Single(first);

        File.WriteAllText(historyPath, """
            [{"Label":"baseline","Timestamp":"2026-08-02T12:00:00Z","Read":{"IOPS":42000},"Write":{"IOPS":51000}},
             {"Label":"post","Timestamp":"2026-08-02T13:00:00Z","Read":{"IOPS":68000},"Write":{"IOPS":84000}}]
            """);
        File.SetLastWriteTimeUtc(historyPath, DateTime.UtcNow.AddMinutes(1));

        var changed = cache.Get(_workingDir);
        Assert.NotSame(first, changed);
        Assert.Equal(2, changed.Count);

        cache.Invalidate();
        var forced = cache.Get(_workingDir);
        Assert.NotSame(changed, forced);
        Assert.Equal(2, forced.Count);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workingDir))
                Directory.Delete(_workingDir, recursive: true);
        }
        catch { }
    }
}
