using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class RecoveryKitServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.RecoveryKit.Tests.{Guid.NewGuid():N}");

    public RecoveryKitServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void GenerateVerificationScript_SafeProfileChecksOnlySafeFeatureSet()
    {
        var path = RecoveryKitService.GenerateVerificationScript(_tempRoot, PatchProfile.Safe, includeServerKey: false);

        Assert.NotNull(path);
        var script = File.ReadAllText(path!);
        Assert.Contains("Expected profile: Safe", script, StringComparison.Ordinal);
        Assert.Contains("735209102", script, StringComparison.Ordinal);
        Assert.DoesNotContain("1853569164", script, StringComparison.Ordinal);
        Assert.DoesNotContain("156965516", script, StringComparison.Ordinal);
        Assert.DoesNotContain("1176759950", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateVerificationScript_FullProfileIncludesExtendedAndServerKeysWhenRequested()
    {
        var path = RecoveryKitService.GenerateVerificationScript(_tempRoot, PatchProfile.Full, includeServerKey: true);

        Assert.NotNull(path);
        var script = File.ReadAllText(path!);
        Assert.Contains("Expected profile: Full", script, StringComparison.Ordinal);
        Assert.Contains("735209102", script, StringComparison.Ordinal);
        Assert.Contains("1853569164", script, StringComparison.Ordinal);
        Assert.Contains("156965516", script, StringComparison.Ordinal);
        Assert.Contains("1176759950", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRecoveryKit_BatchUsesWinPeDetectionForOfflineRecovery()
    {
        var kitDir = RecoveryKitService.Export(_tempRoot);

        Assert.NotNull(kitDir);
        var batch = File.ReadAllText(Path.Combine(kitDir!, "Remove_NVMe_Patch.bat"));
        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WinPE", batch, StringComparison.Ordinal);
        Assert.DoesNotContain(@"reg query ""HKLM\SYSTEM\CurrentControlSet""", batch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("for /L %%N in (1,1,9)", batch, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_RemovalArtifacts_CoverServiceNameSafeBootEntries()
    {
        // The kit must remove BOTH SafeBoot entry styles: the GUID-class entries and the
        // KB5079391-era service-name entries (added v4.6.1) — a kit that leaves
        // SafeBoot\*\nvmedisk behind doesn't fully revert the patch.
        var kitDir = RecoveryKitService.Export(_tempRoot);
        Assert.NotNull(kitDir);

        var reg = File.ReadAllText(Path.Combine(kitDir!, "NVMe_Remove_Patch.reg"));
        Assert.Contains(@"SafeBoot\Minimal\nvmedisk", reg);
        Assert.Contains(@"SafeBoot\Network\nvmedisk", reg);
        Assert.Contains("Remove_NVMe_Patch.bat is the canonical removal path", reg);

        var bat = File.ReadAllText(Path.Combine(kitDir!, "Remove_NVMe_Patch.bat"));
        Assert.Contains(@"SafeBoot\Minimal\nvmedisk", bat);
        Assert.Contains(@"SafeBoot\Network\nvmedisk", bat);
        // Offline sweep covers rolled control sets; service-name entries must be in the loop.
        Assert.Contains(@"ControlSet00%%N\Control\SafeBoot\Minimal\nvmedisk", bat);
    }

    [Fact]
    public void BuildRegContent_DerivesIdsAndKeysFromAppConfig()
    {
        var reg = RecoveryKitService.BuildRegContent("003", "2026-06-14 12:00:00");

        // Every patch feature ID + the optional Server key, sourced from AppConfig — so a future
        // ID change flows through instead of leaving the kit deleting stale values.
        foreach (var id in AppConfig.FeatureIDs)
            Assert.Contains($"\"{id}\"=-", reg, StringComparison.Ordinal);
        Assert.Contains($"\"{AppConfig.ServerFeatureID}\"=-", reg, StringComparison.Ordinal);

        Assert.Contains(AppConfig.SafeBootGuid, reg, StringComparison.Ordinal);
        Assert.Contains($@"SafeBoot\Minimal\{AppConfig.SafeBootServiceName}", reg, StringComparison.Ordinal);

        // Value-deletes appear once per control set (CurrentControlSet + ControlSet003).
        int expected = (AppConfig.FeatureIDs.Count + 1) * 2;
        int actual = reg.Split("\"=-").Length - 1;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildBatContent_DerivesIdsFromAppConfig_AndIsUniformCrlf()
    {
        var bat = RecoveryKitService.BuildBatContent();

        foreach (var id in AppConfig.FeatureIDs)
            Assert.Contains($"/v {id} /f", bat, StringComparison.Ordinal);
        Assert.Contains($"/v {AppConfig.ServerFeatureID} /f", bat, StringComparison.Ordinal);
        Assert.Contains($@"SafeBoot\Network\{AppConfig.SafeBootServiceName}", bat, StringComparison.Ordinal);
        Assert.Contains(AppConfig.SafeBootGuid, bat, StringComparison.Ordinal);

        // No stray LF once CRLF pairs are removed — guards against mixed line endings.
        Assert.DoesNotContain("\n", bat.Replace("\r\n", string.Empty));
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
