using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Regression coverage for GitHub issue #13.
///
/// Recent Windows builds (26200.8737+) ship the SafeBoot GUID subkeys themselves,
/// populated with a "NvmeDisk" value and ACL-protected so even an elevated process
/// cannot open them. The legacy (pre-journal) uninstall path reported that as
///
///   [FAIL] SafeBoot Minimal: Access to the registry key '...' is denied.
///
/// which made a perfectly clean removal look broken on a machine that was never
/// patched. It is not a failure: the key is OS-owned and holds nothing of ours.
///
/// These tests build a real registry tree under HKCU (no admin needed) and apply a
/// genuine deny ACE, so the exception is produced by the registry rather than a mock.
/// </summary>
public sealed class SafeBootRemovalAccessTests : IDisposable
{
    private const string Leaf = "{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}";

    private readonly string _root = $@"Software\NVMeDriverPatcherTests\{Guid.NewGuid():N}";

    [Fact]
    public void AclDeniedOsOwnedKeyIsReportedAsPreservedNotFailed()
    {
        using var parent = Registry.CurrentUser.CreateSubKey($@"{_root}\SafeBoot\Minimal", writable: true)!;
        using (var osOwned = parent.CreateSubKey(Leaf, writable: true)!)
        {
            // Exactly what the reporter found on 26200.8737.
            osOwned.SetValue("NvmeDisk", "Storage Disks", RegistryValueKind.String);
            DenyAllAccess(osOwned);
        }

        var log = new List<string>();
        var removed = 0;

        PatchService.RemoveOwnedSafeBootKey(
            Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "SafeBoot Minimal", ref removed, log.Add);

        var line = Assert.Single(log);
        Assert.DoesNotContain("[FAIL]", line);
        Assert.Contains("[PRESERVED]", line);
        Assert.Contains("SafeBoot Minimal", line);
        Assert.Equal(0, removed);

        // And the OS-owned key must still be there — never deleted, never re-ACL'd.
        // Enumerated from the parent, because opening the leaf hits the same deny ACE
        // the production code just refused to fight (which is the whole point).
        Assert.Contains(Leaf, parent.GetSubKeyNames());
    }

    [Fact]
    public void KeyWeCreatedIsStillRemoved()
    {
        using var parent = Registry.CurrentUser.CreateSubKey($@"{_root}\SafeBoot\Minimal", writable: true)!;
        using (var ours = parent.CreateSubKey(Leaf, writable: true)!)
        {
            // Only a default value, no foreign named values — this one is ours.
            ours.SetValue("", "Storage Disks", RegistryValueKind.String);
        }

        var log = new List<string>();
        var removed = 0;

        PatchService.RemoveOwnedSafeBootKey(
            Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "SafeBoot Minimal", ref removed, log.Add);

        Assert.Contains("[REMOVED]", Assert.Single(log));
        Assert.Equal(1, removed);
        Assert.Null(parent.OpenSubKey(Leaf));
    }

    [Fact]
    public void ReadableOsOwnedKeyKeepsItsForeignValues()
    {
        using var parent = Registry.CurrentUser.CreateSubKey($@"{_root}\SafeBoot\Minimal", writable: true)!;
        using (var osOwned = parent.CreateSubKey(Leaf, writable: true)!)
        {
            osOwned.SetValue("NvmeDisk", "Storage Disks", RegistryValueKind.String);
            osOwned.SetValue("", "Storage Disks", RegistryValueKind.String);
        }

        var log = new List<string>();
        var removed = 0;

        PatchService.RemoveOwnedSafeBootKey(
            Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "SafeBoot Minimal", ref removed, log.Add);

        Assert.Contains("[PRESERVED]", Assert.Single(log));

        using var kept = parent.OpenSubKey(Leaf);
        Assert.NotNull(kept);
        Assert.Equal("Storage Disks", kept!.GetValue("NvmeDisk"));
        Assert.Null(kept.GetValue(""));   // only our default value was cleared
    }

    [Fact]
    public void AbsentKeyIsReportedAsAbsent()
    {
        using var parent = Registry.CurrentUser.CreateSubKey($@"{_root}\SafeBoot\Minimal", writable: true)!;

        var log = new List<string>();
        var removed = 0;

        PatchService.RemoveOwnedSafeBootKey(
            Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "SafeBoot Minimal", ref removed, log.Add);

        Assert.Contains("[ABSENT]", Assert.Single(log));
        Assert.Equal(0, removed);
    }

    private static void DenyAllAccess(RegistryKey key)
    {
        var security = key.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new RegistryAccessRule(
            WindowsIdentity.GetCurrent().User!,
            RegistryRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        key.SetAccessControl(security);
    }

    public void Dispose()
    {
        // Re-grant before deleting, otherwise the deny ACE blocks cleanup too.
        try
        {
            using var minimal = Registry.CurrentUser.OpenSubKey($@"{_root}\SafeBoot\Minimal", writable: true);
            using var leaf = minimal?.OpenSubKey(
                Leaf, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership | RegistryRights.ChangePermissions);
            if (leaf is not null)
            {
                var security = leaf.GetAccessControl(AccessControlSections.Access);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new RegistryAccessRule(
                    WindowsIdentity.GetCurrent().User!,
                    RegistryRights.FullControl,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                leaf.SetAccessControl(security);
            }
        }
        catch
        {
            // Best effort — the throwaway tree lives under a per-run GUID.
        }

        try { Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false); }
        catch { /* leftover test key under a GUID path is harmless */ }
    }
}
