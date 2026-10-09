using Microsoft.Win32;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class RegistryBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.RegBackup.{Guid.NewGuid():N}");

    private readonly string _fixtureRoot = $@"Software\NVMeDriverPatcherTests\BackupFixture\{Guid.NewGuid():N}";

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

    // Fixture-driven variants: the same export against an HKCU tree standing in for HKLM, so the
    // "value present" branches run identically on a clean host and on a patched dev machine.

    [Fact]
    public void ExportRegistryBackup_Fixture_RestoresPresentFeatureValuesAndDeletesTheRest()
    {
        using var hive = Registry.CurrentUser.CreateSubKey(_fixtureRoot, writable: true)!;
        using (var overrides = hive.CreateSubKey(AppConfig.RegistrySubKey, writable: true)!)
        {
            overrides.SetValue(AppConfig.PrimaryFeatureID, 1, RegistryValueKind.DWord);
            overrides.SetValue(AppConfig.ServerFeatureID, 0, RegistryValueKind.DWord);
        }

        var path = RegistryService.ExportRegistryBackup(_dir, "Fixture", hive);
        Assert.NotNull(path);
        var reg = File.ReadAllText(path!);

        Assert.Contains($"\"{AppConfig.PrimaryFeatureID}\"=dword:00000001", reg);
        Assert.Contains($"\"{AppConfig.ServerFeatureID}\"=dword:00000000", reg);
        foreach (var id in AppConfig.FeatureIDs.Where(id => id != AppConfig.PrimaryFeatureID))
            Assert.Contains($"\"{id}\"=-", reg);
        Assert.DoesNotContain($"\"{AppConfig.PrimaryFeatureID}\"=-", reg);
        Assert.DoesNotContain($"\"{AppConfig.ServerFeatureID}\"=-", reg);
    }

    [Fact]
    public void ExportRegistryBackup_Fixture_NeverDeletesAPresentSafeBootKey_AndEscapesItsDefault()
    {
        using var hive = Registry.CurrentUser.CreateSubKey(_fixtureRoot, writable: true)!;
        // Minimal: present, default value needs escaping. Network: present with only a foreign named
        // value (the 26200.8737 shape), so there is no default to restore.
        using (var minimal = hive.CreateSubKey(AppConfig.SafeBootMinimalPath, writable: true)!)
            minimal.SetValue("", "Storage \\Disks \"x\"", RegistryValueKind.String);
        using (var network = hive.CreateSubKey(AppConfig.SafeBootNetworkPath, writable: true)!)
            network.SetValue("NvmeDisk", "Storage Disks", RegistryValueKind.String);

        var path = RegistryService.ExportRegistryBackup(_dir, "Fixture", hive);
        Assert.NotNull(path);
        var reg = File.ReadAllText(path!);

        Assert.Contains($"[HKEY_LOCAL_MACHINE\\{AppConfig.SafeBootMinimalPath}]", reg);
        Assert.Contains("@=\"Storage \\\\Disks \\\"x\\\"\"", reg);
        Assert.Contains($"[HKEY_LOCAL_MACHINE\\{AppConfig.SafeBootNetworkPath}]", reg);
        Assert.DoesNotContain($"[-HKEY_LOCAL_MACHINE\\{AppConfig.SafeBootMinimalPath}]", reg);
        Assert.DoesNotContain($"[-HKEY_LOCAL_MACHINE\\{AppConfig.SafeBootNetworkPath}]", reg);
        // The service-name keys are absent in the fixture, so only those are scheduled for deletion.
        Assert.Contains($"[-HKEY_LOCAL_MACHINE\\{AppConfig.SafeBootMinimalServicePath}]", reg);
        Assert.Contains($"[-HKEY_LOCAL_MACHINE\\{AppConfig.SafeBootNetworkServicePath}]", reg);
    }

    [Theory]
    [InlineData("Storage Disks", "Storage Disks")]
    [InlineData(@"C:\Path\With\Slashes", @"C:\\Path\\With\\Slashes")]
    [InlineData("has \"quotes\"", "has \\\"quotes\\\"")]
    public void EscapeRegString_EscapesBackslashesAndQuotes(string input, string expected)
    {
        Assert.Equal(expected, RegistryService.EscapeRegString(input));
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_fixtureRoot, throwOnMissingSubKey: false); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
