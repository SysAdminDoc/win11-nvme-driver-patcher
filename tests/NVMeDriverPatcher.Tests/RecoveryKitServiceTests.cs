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
