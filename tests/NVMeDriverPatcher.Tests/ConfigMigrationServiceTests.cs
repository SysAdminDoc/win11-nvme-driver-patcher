using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ConfigMigrationServiceTests
{
    [Fact]
    public void V0Config_MigratesThroughAllHops_ToCurrent()
    {
        var config = new AppConfig { ConfigVersion = 0 };
        var (changed, summary) = ConfigMigrationService.Migrate(config);
        Assert.True(changed);
        Assert.Equal(ConfigMigrationService.CurrentSchemaVersion, config.ConfigVersion);
        Assert.Contains("v1 → v2", summary);
        Assert.Contains("v2 → v3", summary);
    }

    [Fact]
    public void V2Config_OnlyUpgradesOnce()
    {
        var config = new AppConfig { ConfigVersion = 2 };
        var (changed, summary) = ConfigMigrationService.Migrate(config);
        Assert.True(changed);
        Assert.Equal(3, config.ConfigVersion);
        Assert.DoesNotContain("v1 → v2", summary);
    }

    [Fact]
    public void UpToDateConfig_NoChange()
    {
        var config = new AppConfig { ConfigVersion = ConfigMigrationService.CurrentSchemaVersion };
        var (changed, summary) = ConfigMigrationService.Migrate(config);
        Assert.False(changed);
        Assert.Equal("Config already at current schema.", summary);
    }

    [Fact]
    public void MigrateIsIdempotent()
    {
        var config = new AppConfig { ConfigVersion = 1 };
        ConfigMigrationService.Migrate(config);
        var (changedSecond, _) = ConfigMigrationService.Migrate(config);
        Assert.False(changedSecond);
    }
}
