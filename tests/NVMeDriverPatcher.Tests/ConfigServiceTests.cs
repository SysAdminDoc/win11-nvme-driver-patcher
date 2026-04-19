using System.Text.Json;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ConfigServiceTests
{
    [Theory]
    [InlineData("\"Safe\"", PatchProfile.Safe)]
    [InlineData("\"safe\"", PatchProfile.Safe)]
    [InlineData("\"Full\"", PatchProfile.Full)]
    [InlineData("\"full\"", PatchProfile.Full)]
    [InlineData("0", PatchProfile.Safe)]
    [InlineData("1", PatchProfile.Full)]
    [InlineData("\"1\"", PatchProfile.Full)]
    [InlineData("\"FutureProfile\"", PatchProfile.Safe)]
    [InlineData("999", PatchProfile.Safe)]
    public void LenientPatchProfileJsonConverter_FallsBackForUnknownValues(
        string jsonValue,
        PatchProfile expected)
    {
        var config = DeserializeConfig($$"""
            {
              "PatchProfile": {{jsonValue}},
              "RestartDelay": 45
            }
            """);

        Assert.Equal(expected, config.PatchProfile);
        Assert.Equal(45, config.RestartDelay);
    }

    [Fact]
    public void LenientPatchProfileJsonConverter_WritesReadableProfileName()
    {
        var options = CreateOptions();

        var json = JsonSerializer.Serialize(PatchProfile.Full, options);

        Assert.Equal("\"Full\"", json);
    }

    [Fact]
    public void ExistingFileWithExtension_RejectsExistingFileWithWrongExtension()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.ConfigTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var txt = Path.Combine(tempDir, "NVMe_Diagnostics_20260419.txt");
            var zip = Path.Combine(tempDir, "NVMe_SupportBundle_20260419.zip");
            File.WriteAllText(txt, "diagnostics");
            File.WriteAllText(zip, "bundle");

            Assert.Equal(txt, ConfigService.ExistingFileWithExtension(txt, ".txt"));
            Assert.Null(ConfigService.ExistingFileWithExtension(zip, ".txt"));
            Assert.Equal(zip, ConfigService.ExistingFileWithExtension(zip, ".zip"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("relative.txt", false)]
    [InlineData(@"C:relative.txt", false)]
    [InlineData(@"C:\absolute.txt", true)]
    [InlineData(@"\\server\share\absolute.txt", true)]
    public void IsUsableAbsolutePath_RejectsRelativeConfigPaths(string? path, bool expected)
    {
        Assert.Equal(expected, ConfigService.IsUsableAbsolutePath(path));
    }

    private static AppConfig DeserializeConfig(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, CreateOptions());
        Assert.NotNull(config);
        return config!;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ConfigService.LenientPatchProfileJsonConverter());
        return options;
    }
}
