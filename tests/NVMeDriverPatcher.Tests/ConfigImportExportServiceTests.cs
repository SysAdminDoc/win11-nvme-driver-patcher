using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ConfigImportExportServiceTests
{
    [Fact]
    public void ValidateBundleJson_ValidBundle_Ok()
    {
        var json = """
        { "SchemaVersion": 1, "Config": { "RestartDelay": 30, "PatchProfile": "Safe" } }
        """;
        var (ok, error) = ConfigImportExportService.ValidateBundleJson(json);
        Assert.True(ok, error);
    }

    [Fact]
    public void ValidateBundleJson_FutureSchema_Rejected()
    {
        var json = """{ "SchemaVersion": 999, "Config": { "RestartDelay": 30 } }""";
        var (ok, error) = ConfigImportExportService.ValidateBundleJson(json);
        Assert.False(ok);
        Assert.Contains("SchemaVersion", error);
    }

    [Fact]
    public void ValidateBundleJson_NegativeRestartDelay_Rejected()
    {
        var json = """{ "SchemaVersion": 1, "Config": { "RestartDelay": -5 } }""";
        var (ok, error) = ConfigImportExportService.ValidateBundleJson(json);
        Assert.False(ok);
        Assert.Contains("RestartDelay", error);
    }

    [Fact]
    public void ValidateBundleJson_HugeRestartDelay_Rejected()
    {
        var json = """{ "SchemaVersion": 1, "Config": { "RestartDelay": 100000 } }""";
        var (ok, error) = ConfigImportExportService.ValidateBundleJson(json);
        Assert.False(ok);
        Assert.Contains("RestartDelay", error);
    }

    [Theory]
    [InlineData("\"Frobnicate\"")]
    [InlineData("99")]
    public void ValidateBundleJson_UndefinedPatchProfile_Rejected(string profileToken)
    {
        var json = $$"""{ "SchemaVersion": 1, "Config": { "PatchProfile": {{profileToken}} } }""";
        var (ok, error) = ConfigImportExportService.ValidateBundleJson(json);
        Assert.False(ok);
        Assert.Contains("PatchProfile", error);
    }

    [Theory]
    [InlineData("\"Safe\"")]
    [InlineData("\"Full\"")]
    [InlineData("0")]
    [InlineData("1")]
    public void ValidateBundleJson_KnownPatchProfile_Ok(string profileToken)
    {
        var json = $$"""{ "SchemaVersion": 1, "Config": { "PatchProfile": {{profileToken}} } }""";
        var (ok, error) = ConfigImportExportService.ValidateBundleJson(json);
        Assert.True(ok, error);
    }

    [Fact]
    public void ValidateBundleJson_MissingSchemaVersion_DefaultsToCurrentAndPasses()
    {
        // An absent SchemaVersion is treated as a legacy v1 bundle, so it must not be rejected.
        var json = """{ "Config": { "RestartDelay": 10 } }""";
        var (ok, error) = ConfigImportExportService.ValidateBundleJson(json);
        Assert.True(ok, error);
    }

    [Fact]
    public void Export_UsesSchemaV2AndOmitsUnenforcedDriveScope()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMePatcher.ConfigExport.{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "bundle.json");
        try
        {
            var config = new NVMeDriverPatcher.Models.AppConfig { WorkingDir = dir };

            var exportedPath = ConfigImportExportService.Export(config, output);

            Assert.Equal(output, exportedPath);
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(output));
            Assert.Equal(2, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("DriveScope", out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ValidateBundleJson_Garbage_RejectedNotThrown()
    {
        var (ok, error) = ConfigImportExportService.ValidateBundleJson("not json at all");
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Import_FutureSchemaBundle_DoesNotMutateConfig()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cfgbundle_{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, """{ "SchemaVersion": 999, "Config": { "RestartDelay": 30, "SkipWarnings": true } }""");
        try
        {
            var config = new NVMeDriverPatcher.Models.AppConfig { SkipWarnings = false };
            var (success, summary) = ConfigImportExportService.Import(tmp, config);
            Assert.False(success);
            Assert.Contains("SchemaVersion", summary);
            Assert.False(config.SkipWarnings); // untouched
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Import_LegacyDriveScope_IsAcceptedButExplicitlyIgnored()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMePatcher.ConfigImport.{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "bundle.json");
        File.WriteAllText(input,
            """
            {
              "SchemaVersion": 1,
              "Config": { "RestartDelay": 30, "PatchProfile": 0 },
              "DriveScope": { "Enabled": true, "ExcludedSerials": ["SN-1"] }
            }
            """);
        try
        {
            var config = new NVMeDriverPatcher.Models.AppConfig
            {
                WorkingDir = dir,
                ConfigFile = Path.Combine(dir, "config.json")
            };

            var (success, summary) = ConfigImportExportService.Import(input, config);

            Assert.True(success, summary);
            Assert.Contains("ignored", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("machine-wide", summary, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(dir, "drive_scope.json")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
