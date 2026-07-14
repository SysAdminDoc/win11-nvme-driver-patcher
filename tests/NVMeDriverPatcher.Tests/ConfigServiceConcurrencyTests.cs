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
        Assert.True(ConfigService.TryReadSavedConfig(configFile + ".bak", out _, out var backupError), backupError);
    }

    [Fact]
    public void ConsecutiveSaves_RetainValidatedPreviousVersionAsLastKnownGood()
    {
        var configFile = Path.Combine(_dir, "config.json");
        var config = new AppConfig
        {
            WorkingDir = _dir,
            ConfigFile = configFile,
            PendingFallbackApplied = false,
            RestartDelay = 10
        };

        Assert.True(ConfigService.SaveWithStatus(config).Success);
        config.PendingFallbackApplied = true;
        config.RestartDelay = 20;
        var second = ConfigService.SaveWithStatus(config);

        Assert.Equal(ConfigSaveStatus.Saved, second.Status);
        Assert.True(ConfigService.TryReadSavedConfig(configFile, out var primary, out var primaryError), primaryError);
        Assert.True(ConfigService.TryReadSavedConfig(configFile + ".bak", out var backup, out var backupError), backupError);
        Assert.True(primary!.PendingFallbackApplied);
        Assert.Equal(20, primary.RestartDelay);
        Assert.False(backup!.PendingFallbackApplied);
        Assert.Equal(10, backup.RestartDelay);
    }

    [Fact]
    public void LoadDetailed_CorruptPrimaryRecoversValidatedBackupAndPreservesEvidence()
    {
        var configFile = Path.Combine(_dir, "config.json");
        var config = new AppConfig
        {
            WorkingDir = _dir,
            ConfigFile = configFile,
            PendingFallbackApplied = false,
            RestartDelay = 17
        };
        Assert.True(ConfigService.Save(config));
        config.PendingFallbackApplied = true;
        config.RestartDelay = 29;
        Assert.True(ConfigService.Save(config));
        File.WriteAllText(configFile, "{ torn json");

        var result = ConfigService.LoadDetailed(_dir);

        Assert.Equal(ConfigLoadStatus.RecoveredFromBackup, result.Status);
        Assert.False(result.Config.PendingFallbackApplied);
        Assert.Equal(17, result.Config.RestartDelay);
        Assert.True(File.Exists(configFile + ".corrupt"));
        Assert.Equal("{ torn json", File.ReadAllText(configFile + ".corrupt"));
        Assert.True(ConfigService.TryReadSavedConfig(configFile, out var healed, out var error), error);
        Assert.Equal(17, healed!.RestartDelay);
    }

    [Fact]
    public void LoadDetailed_CorruptPrimaryAndBackupPreservesBothBeforeUsingDefaults()
    {
        var configFile = Path.Combine(_dir, "config.json");
        File.WriteAllText(configFile, "bad primary");
        File.WriteAllText(configFile + ".bak", "bad backup");

        var result = ConfigService.LoadDetailed(_dir);

        Assert.Equal(ConfigLoadStatus.Defaults, result.Status);
        Assert.True(File.Exists(configFile + ".corrupt"));
        Assert.True(File.Exists(configFile + ".bak.corrupt"));
        Assert.False(File.Exists(configFile));
        Assert.False(File.Exists(configFile + ".bak"));
    }

    [Fact]
    public async Task SaveDetailed_LockTimeoutReturnsBusyWithoutTouchingProtectedFiles()
    {
        var configFile = Path.Combine(_dir, "config.json");
        var mutexName = $@"Local\NVMeDriverPatcher.Config.Tests.{Guid.NewGuid():N}";
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, mutexName);
            mutex.WaitOne();
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var result = ConfigService.SaveDetailed(
                new AppConfig { WorkingDir = _dir, ConfigFile = configFile },
                TimeSpan.FromMilliseconds(50),
                mutexName);

            Assert.Equal(ConfigSaveStatus.Busy, result.Status);
            Assert.Contains("no protected state was written", result.Summary);
            Assert.False(File.Exists(configFile));
            Assert.Empty(Directory.GetFiles(_dir, "config.json.*.tmp"));
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task LoadDetailed_LockTimeoutReturnsBusyWithoutReadingPersistedState()
    {
        var configFile = Path.Combine(_dir, "config.json");
        var config = new AppConfig
        {
            WorkingDir = _dir,
            ConfigFile = configFile,
            PendingFallbackApplied = true
        };
        Assert.True(ConfigService.Save(config));

        var mutexName = $@"Local\NVMeDriverPatcher.Config.Tests.{Guid.NewGuid():N}";
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, mutexName);
            mutex.WaitOne();
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var result = ConfigService.LoadDetailed(
                _dir,
                TimeSpan.FromMilliseconds(50),
                mutexName);

            Assert.Equal(ConfigLoadStatus.Busy, result.Status);
            Assert.False(result.Success);
            Assert.False(result.Config.PendingFallbackApplied);
            Assert.Contains("no protected state was read", result.Summary);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
