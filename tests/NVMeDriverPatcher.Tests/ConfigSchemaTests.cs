using System.Runtime.CompilerServices;
using System.Text.Json;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ConfigSchemaTests
{
    [Fact]
    public void DefaultConfigVersion_MatchesCurrentMigrationSchema()
    {
        Assert.Equal(ConfigMigrationService.CurrentSchemaVersion, new AppConfig().ConfigVersion);
    }

    [Fact]
    public void PackagedSchema_CoversSavedConfigContract()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.SchemaTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var config = new AppConfig
            {
                ConfigFile = Path.Combine(tempDir, "config.json"),
                LastRun = DateTimeOffset.UtcNow.ToString("o")
            };
            ConfigService.Save(config);

            using var savedJson = JsonDocument.Parse(File.ReadAllText(config.ConfigFile));
            using var schemaJson = JsonDocument.Parse(File.ReadAllText(ConfigSchemaPath()));

            var savedProperties = savedJson.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var schemaProperties = schemaJson.RootElement.GetProperty("properties").EnumerateObject()
                .Select(p => p.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(savedProperties, schemaProperties);
            Assert.Equal(
                ConfigMigrationService.CurrentSchemaVersion,
                schemaJson.RootElement
                    .GetProperty("properties")
                    .GetProperty(nameof(AppConfig.ConfigVersion))
                    .GetProperty("default")
                    .GetInt32());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PackagedSchema_ListsPersistedEnumNames()
    {
        using var schemaJson = JsonDocument.Parse(File.ReadAllText(ConfigSchemaPath()));
        var properties = schemaJson.RootElement.GetProperty("properties");

        Assert.Equal(
            Enum.GetNames<AppThemeMode>().Order(StringComparer.Ordinal),
            EnumValues(properties.GetProperty(nameof(AppConfig.ThemeMode))));
        Assert.Equal(
            Enum.GetNames<PatchProfile>().Order(StringComparer.Ordinal),
            EnumValues(properties.GetProperty(nameof(AppConfig.PatchProfile))));
    }

    private static string ConfigSchemaPath([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(repoRoot, "packaging", "schemas", "config.schema.json");
    }

    private static string[] EnumValues(JsonElement property)
    {
        return property.GetProperty("enum").EnumerateArray()
            .Select(v => v.GetString()!)
            .Where(v => v is not null)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
