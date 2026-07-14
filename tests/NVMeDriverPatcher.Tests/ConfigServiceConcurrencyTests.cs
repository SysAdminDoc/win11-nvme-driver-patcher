using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ConfigServiceConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.ConfigConc.{Guid.NewGuid():N}");

    public ConfigServiceConcurrencyTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void ConcurrentSaves_AllSucceed_AndLeaveValidConfig_NoTempLeaks()
    {
        var configFile = Path.Combine(_dir, "config.json");

        var results = new bool[16];
        Parallel.For(0, results.Length, i =>
        {
            var cfg = new AppConfig
            {
                WorkingDir = _dir,
                ConfigFile = configFile,
                RestartDelay = 30 + (i % 5),
                PendingFallbackApplied = i % 2 == 0
            };
            results[i] = ConfigService.Save(cfg);
        });

        // Every writer reports success (the mutex + unique temp name prevent lost/partial writes).
        Assert.All(results, Assert.True);

        // The final file is valid, non-empty JSON — not a half-written temp clobber.
        var json = File.ReadAllText(configFile);
        Assert.False(string.IsNullOrWhiteSpace(json));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("RestartDelay", out _));

        // No per-process temp files leaked behind.
        Assert.Empty(Directory.GetFiles(_dir, "config.json.*.tmp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
