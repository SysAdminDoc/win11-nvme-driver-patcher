using System.Text.Json.Nodes;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class DiagnosticsServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.Tests.{Guid.NewGuid():N}");

    public DiagnosticsServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void TryCreateShareableConfigText_RedactsPathLikePropertiesAndPreservesOtherSettings()
    {
        var configPath = Path.Combine(_tempRoot, "config.json");
        File.WriteAllText(configPath, """
            {
              "PatchProfile": "Safe",
              "LastDiagnosticsPath": "C:\\Users\\alice\\AppData\\Local\\NVMePatcher\\NVMe_Diagnostics_20260417.txt",
              "WorkingDir": "C:\\Users\\alice\\AppData\\Local\\NVMePatcher",
              "LastRun": "2026-04-17T18:30:00Z"
            }
            """);

        var sanitized = DiagnosticsService.TryCreateShareableConfigText(configPath);

        Assert.NotNull(sanitized);
        var root = JsonNode.Parse(sanitized!);
        Assert.NotNull(root);
        Assert.Equal("Safe", root!["PatchProfile"]?.GetValue<string>());
        Assert.Equal("NVMe_Diagnostics_20260417.txt", root["LastDiagnosticsPath"]?.GetValue<string>());
        Assert.Equal("NVMePatcher", root["WorkingDir"]?.GetValue<string>());
        Assert.Equal("2026-04-17T18:30:00Z", root["LastRun"]?.GetValue<string>());
    }

    [Fact]
    public void TryCreateShareableConfigText_ReturnsNullForMalformedJson()
    {
        var configPath = Path.Combine(_tempRoot, "broken-config.json");
        File.WriteAllText(configPath, "{ \"PatchProfile\": ");

        var sanitized = DiagnosticsService.TryCreateShareableConfigText(configPath);

        Assert.Null(sanitized);
    }

    [Fact]
    public void TryCreateShareableDiagnosticsText_RedactsComputerAndUserLines()
    {
        var reportPath = Path.Combine(_tempRoot, "diagnostics.txt");
        File.WriteAllText(reportPath, """
            NVMe Driver Patcher - System Diagnostics Report
            Computer Name: DESKTOP-ALICE
            User: alice
            OS: Windows 11 Pro
            """);

        var sanitized = DiagnosticsService.TryCreateShareableDiagnosticsText(reportPath);

        Assert.NotNull(sanitized);
        Assert.Contains("Computer Name: [redacted]", sanitized, StringComparison.Ordinal);
        Assert.Contains("User: [redacted]", sanitized, StringComparison.Ordinal);
        Assert.Contains("OS: Windows 11 Pro", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("DESKTOP-ALICE", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("User: alice", sanitized, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }
}
