using Microsoft.Win32;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class RegistryBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.RegBackup.{Guid.NewGuid():N}");

    public RegistryBackupTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void ExportRegistryBackup_EmitsRestoreOrDeleteDirective_ForEveryManagedFeatureId()
    {
        var path = RegistryService.ExportRegistryBackup(_dir, "Test");
        Assert.NotNull(path);
        var reg = File.ReadAllText(path!);

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var overrides = hklm.OpenSubKey(AppConfig.RegistrySubKey);

        foreach (var id in AppConfig.FeatureIDs.Append(AppConfig.ServerFeatureID))
        {
            bool presentLive = overrides?.GetValue(id) is int;
            if (presentLive)
                Assert.Contains($"\"{id}\"=dword:", reg);   // restore prior value
            else
                Assert.Contains($"\"{id}\"=-", reg);          // delete key the patch would add
        }
    }

    [Fact]
    public void ExportRegistryBackup_EmitsDeletionDirective_ForAbsentSafeBootKeys()
    {
        var path = RegistryService.ExportRegistryBackup(_dir, "Test");
        Assert.NotNull(path);
        var reg = File.ReadAllText(path!);

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var safeBootPath in new[] { AppConfig.SafeBootMinimalPath, AppConfig.SafeBootNetworkPath })
        {
            using var key = hklm.OpenSubKey(safeBootPath);
            if (key is null)
                // Absent pre-patch → the backup must schedule its deletion so re-import undoes the patch.
                Assert.Contains($"[-HKEY_LOCAL_MACHINE\\{safeBootPath}]", reg);
            else
                // Present pre-patch → never scheduled for deletion (may hold OS-owned state, issue #13).
                Assert.DoesNotContain($"[-HKEY_LOCAL_MACHINE\\{safeBootPath}]", reg);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
