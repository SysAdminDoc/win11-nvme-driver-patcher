using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class WorkingDirResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NVMePatcher_WorkingDir_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(@"C:\Users\Alice\AppData\Local")]
    [InlineData(@"C:\Windows\ServiceProfiles\LocalService\AppData\Local")]
    public void SharedWorkingDirPath_DoesNotDependOnLocalAppDataProfile(string localAppData)
    {
        var shared = AppConfig.GetSharedWorkingDirPath(@"C:\ProgramData");
        var legacy = AppConfig.GetLegacyUserWorkingDirPath(localAppData);

        Assert.Equal(@"C:\ProgramData\NVMePatcher", shared);
        Assert.EndsWith(@"\NVMePatcher", legacy, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(shared, legacy, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrateLegacyWorkingDirIfNeeded_CopiesSharedStateFilesWithoutOverwriting()
    {
        var legacy = Path.Combine(_root, "Legacy");
        var target = Path.Combine(_root, "Shared");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(legacy, "config.json"), "{\"RestartDelay\":45}");
        File.WriteAllText(Path.Combine(legacy, "watchdog.json"), "{\"LastVerdict\":\"Warning\"}");
        File.WriteAllText(Path.Combine(legacy, "drive_scope.json"), "{\"rules\":[]}");
        File.WriteAllText(Path.Combine(legacy, "nvmepatcher.db"), "legacy-db");
        File.WriteAllText(Path.Combine(target, "config.json"), "{\"RestartDelay\":10}");

        var copied = ConfigService.MigrateLegacyWorkingDirIfNeeded(target, legacy);

        Assert.Equal(3, copied);
        Assert.Equal("{\"RestartDelay\":10}", File.ReadAllText(Path.Combine(target, "config.json")));
        Assert.Equal("{\"LastVerdict\":\"Warning\"}", File.ReadAllText(Path.Combine(target, "watchdog.json")));
        Assert.Equal("{\"rules\":[]}", File.ReadAllText(Path.Combine(target, "drive_scope.json")));
        Assert.Equal("legacy-db", File.ReadAllText(Path.Combine(target, "nvmepatcher.db")));

        File.Delete(Path.Combine(target, "watchdog.json"));
        Assert.Equal(0, ConfigService.MigrateLegacyWorkingDirIfNeeded(target, legacy));
        Assert.False(File.Exists(Path.Combine(target, "watchdog.json")));
    }

    [Fact]
    public void MigrateLegacyWorkingDirIfNeeded_SkipsMissingLegacyDir()
    {
        var target = Path.Combine(_root, "Shared");

        var copied = ConfigService.MigrateLegacyWorkingDirIfNeeded(target, Path.Combine(_root, "Missing"));

        Assert.Equal(0, copied);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void WatchdogState_RoundTripsAcrossSimulatedServiceAndGuiConfigs()
    {
        var shared = Path.Combine(_root, "ProgramData", AppConfig.WorkingDirFolderName);
        var serviceConfig = new AppConfig { WorkingDir = shared };
        var guiConfig = new AppConfig { WorkingDir = shared };
        var state = new WatchdogState
        {
            PatchAppliedAt = "2026-06-27T12:00:00.0000000Z",
            LastEvaluatedAt = "2026-06-27T12:05:00.0000000Z",
            LastVerdict = WatchdogVerdict.Warning.ToString(),
            CumulativeEvents = 4
        };

        EventLogWatchdogService.SaveState(serviceConfig, state);
        var loaded = EventLogWatchdogService.LoadState(guiConfig);

        Assert.Equal(WatchdogVerdict.Warning.ToString(), loaded.LastVerdict);
        Assert.Equal(4, loaded.CumulativeEvents);
        Assert.Equal(state.PatchAppliedAt, loaded.PatchAppliedAt);
    }
}
