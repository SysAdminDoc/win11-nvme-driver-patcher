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
        // An absent SchemaVersion defaults to 1 (the model default), so it must not be rejected.
        var json = """{ "Config": { "RestartDelay": 10 } }""";
        var (ok, error) = ConfigImportExportService.ValidateBundleJson(json);
        Assert.True(ok, error);
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
}
