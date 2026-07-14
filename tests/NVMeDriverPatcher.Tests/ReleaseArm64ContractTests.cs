using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;

namespace NVMeDriverPatcher.Tests;

public sealed class ReleaseArm64ContractTests
{
    private static readonly (string Project, string AssetId, string Arm64AssetName)[] RuntimeProjects =
    [
        ("src/NVMeDriverPatcher/NVMeDriverPatcher.csproj", "gui-arm64", "NVMeDriverPatcher-win-arm64.exe"),
        ("src/NVMeDriverPatcher.Cli/NVMeDriverPatcher.Cli.csproj", "cli-arm64", "NVMeDriverPatcher.Cli-win-arm64.exe"),
        ("src/NVMeDriverPatcher.Tray/NVMeDriverPatcher.Tray.csproj", "tray-arm64", "NVMeDriverPatcher.Tray-win-arm64.exe"),
        ("src/NVMeDriverPatcher.Watchdog/NVMeDriverPatcher.Watchdog.csproj", "watchdog-arm64", "NVMeDriverPatcher.Watchdog-win-arm64.exe"),
    ];

    [Theory]
    [MemberData(nameof(RuntimeProjectRows))]
    public void RuntimeProject_DefaultsToX64AndAllowsArm64(string projectPath, string assetId, string arm64AssetName)
    {
        // Row-integrity: the ARM64 asset id/name columns must stay well-formed alongside the RID contract.
        Assert.EndsWith("-arm64", assetId);
        Assert.EndsWith("-win-arm64.exe", arm64AssetName);

        var project = XDocument.Load(Path.Combine(RepoRoot(), projectPath.Replace('/', Path.DirectorySeparatorChar)));
        var properties = project.Descendants()
            .Where(e => e.Name.LocalName is "RuntimeIdentifier" or "RuntimeIdentifiers")
            .ToDictionary(e => e.Name.LocalName, e => e.Value);

        Assert.Equal("win-x64", properties["RuntimeIdentifier"]);
        var rids = properties["RuntimeIdentifiers"].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("win-x64", rids);
        Assert.Contains("win-arm64", rids);
    }

    [Fact]
    public void ReleaseArtifactContract_RequiresDistinctArm64PortableAssets()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "packaging/release-artifacts.json")));
        var artifacts = json.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
        var byId = artifacts.ToDictionary(a => a.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);

        var filenames = artifacts
            .Select(a => Path.GetFileName(a.GetProperty("path").GetString()!.Replace("{version}", "5.0.0")))
            .ToArray();
        Assert.Equal(filenames.Length, filenames.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var (_, assetId, arm64AssetName) in RuntimeProjects)
        {
            Assert.True(byId.TryGetValue(assetId, out var artifact), $"Missing {assetId} in release-artifacts.json");
            Assert.Equal($"publish/{arm64AssetName}", artifact.GetProperty("path").GetString());
            Assert.True(artifact.GetProperty("required").GetBoolean());
            Assert.True(artifact.GetProperty("sign").GetBoolean());
            Assert.True(artifact.GetProperty("checksum").GetBoolean());
            Assert.True(artifact.GetProperty("upload").GetBoolean());
            Assert.Equal("portable-arm64", artifact.GetProperty("channel").GetString());
            Assert.Equal("win-arm64", artifact.GetProperty("runtime").GetString());
            Assert.Contains("Diagnostic/status", artifact.GetProperty("note").GetString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void LocalReleaseBuilder_IncludesX64AndArm64Matrix()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts/Build-ReleaseArtifacts.ps1"));

        Assert.Contains("win-x64", script);
        Assert.Contains("win-arm64", script);
        foreach (var (_, assetId, arm64AssetName) in RuntimeProjects)
        {
            Assert.Contains(assetId, script);
            Assert.Contains(arm64AssetName, script);
        }
    }

    public static IEnumerable<object[]> RuntimeProjectRows() =>
        RuntimeProjects.Select(p => new object[] { p.Project, p.AssetId, p.Arm64AssetName });

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
