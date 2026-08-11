using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Every persisted-intent property must survive serialize -> load. The save path writes an
    /// anonymous object naming each field by hand and the load path assigns each one by hand, so a
    /// property added to only one list (or neither) is accepted at runtime, reported as Saved, and
    /// silently dropped. That is how the persistence guard's enable flag and its re-apply budget
    /// became durable no-ops: the CLI reported success and every next process read the defaults,
    /// which also restarted the anti-boot-loop counter at zero on every boot.
    /// </summary>
    [Fact]
    public void EveryPersistedProperty_SurvivesASaveLoadRoundTrip()
    {
        var config = new AppConfig();
        var persisted = PersistedProperties().ToList();
        Assert.NotEmpty(persisted);

        foreach (var property in persisted)
            property.SetValue(config, DistinctiveValue(property, config));

        var json = ConfigService.SerializeConfig(config);
        var saved = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(saved);

        var loaded = new AppConfig();
        ConfigService.ApplySavedConfig(loaded, saved!);

        var dropped = persisted
            .Where(p => !Equals(p.GetValue(loaded), p.GetValue(config)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            dropped.Count == 0,
            "These AppConfig properties are not persisted end to end (missing from SerializeConfig " +
            "and/or ApplySavedConfig): " + string.Join(", ", dropped));
    }

    private static IEnumerable<PropertyInfo> PersistedProperties() =>
        typeof(AppConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            // Paths are re-validated against the filesystem on load by design (a recorded path that
            // no longer exists is dropped), so they cannot round-trip a synthetic value.
            .Where(p => !p.Name.StartsWith("Last", StringComparison.Ordinal) ||
                        p.Name == "LastRun" || p.Name == "LastVerifiedProfile" ||
                        p.Name == "LastVerificationResult");

    private static object DistinctiveValue(PropertyInfo property, AppConfig current)
    {
        var value = property.GetValue(current);
        return property.PropertyType switch
        {
            var t when t == typeof(bool) => !(bool)(value ?? false),
            // Stay inside every clamp in AppConfig (RestartDelay 0-3600, guard budget 0-10).
            var t when t == typeof(int) => ((int)(value ?? 0)) == 7 ? 6 : 7,
            var t when t == typeof(string) => "round-trip-probe",
            var t when t == typeof(AppThemeMode) => AppThemeMode.HighContrast,
            var t when t == typeof(PatchProfile) => PatchProfile.Full,
            _ => throw new Xunit.Sdk.XunitException(
                $"AppConfig.{property.Name} has unhandled type {property.PropertyType.Name}; " +
                "extend DistinctiveValue so the round-trip gate keeps covering every persisted field.")
        };
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
