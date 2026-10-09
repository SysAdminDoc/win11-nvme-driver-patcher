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

    // Only this class writes under TestParent, so the startup sweep can clear every sibling tree
    // (left behind by a run that died between the deny ACE and Dispose) without touching other suites.
    private const string TestParent = @"Software\NVMeDriverPatcherTests\SafeBootAccess";

    private readonly string _root = $@"{TestParent}\{Guid.NewGuid():N}";

    static SafeBootRemovalAccessTests()
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(TestParent);
            if (parent is null) return;
            foreach (var name in parent.GetSubKeyNames())
                RestoreAccessAndDelete($@"{TestParent}\{name}");
        }
        catch
        {
            // Best effort: a stuck orphan under a GUID path is harmless.
        }
    }

    [Fact]
    public void AclDeniedOsOwnedKeyIsReportedAsPreservedNotFailed()
    {
        using var parent = Registry.CurrentUser.CreateSubKey($@"{_root}\SafeBoot\Minimal", writable: true)!;
        try
        {
            using (var osOwned = parent.CreateSubKey(Leaf, writable: true)!)
            {
                // Exactly what the reporter found on 26200.8737.
                osOwned.SetValue("NvmeDisk", "Storage Disks", RegistryValueKind.String);
                DenyAllAccess(osOwned);
            }

            var log = new List<string>();
            var removed = 0;

            PatchService.RemoveOwnedSafeBootKey(
                Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "Safe Boot Minimal", ref removed, log.Add);

            var line = Assert.Single(log);
            Assert.DoesNotContain("[FAIL]", line);
            Assert.Contains("[PRESERVED]", line);
            Assert.Contains("Safe Boot Minimal", line);
            Assert.Equal(0, removed);

            // And the OS-owned key must still be there — never deleted, never re-ACL'd.
            // Enumerated from the parent, because opening the leaf hits the same deny ACE
            // the production code just refused to fight (which is the whole point).
            Assert.Contains(Leaf, parent.GetSubKeyNames());
        }
        finally
        {
            RestoreAccess(_root);
        }
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
            Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "Safe Boot Minimal", ref removed, log.Add);

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
            Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "Safe Boot Minimal", ref removed, log.Add);

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
            Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "Safe Boot Minimal", ref removed, log.Add);

        Assert.Contains("[ABSENT]", Assert.Single(log));
        Assert.Equal(0, removed);
    }

    [Fact]
    public void WindowsDefaultValueIsLeftAlone()
    {
        // 24H2 26100.9550 keeps Windows' "NvmeDisk" in the default value itself, with no named
        // values, so "no named values" alone no longer proves the key is ours.
        using var parent = Registry.CurrentUser.CreateSubKey($@"{_root}\SafeBoot\Minimal", writable: true)!;
        using (var osOwned = parent.CreateSubKey(Leaf, writable: true)!)
            osOwned.SetValue("", "NvmeDisk", RegistryValueKind.String);

        var log = new List<string>();
        var removed = 0;

        PatchService.RemoveOwnedSafeBootKey(
            Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "Safe Boot Minimal", ref removed, log.Add);

        Assert.Contains("[PRESERVED]", Assert.Single(log));
        Assert.Equal(0, removed);
        using var kept = parent.OpenSubKey(Leaf);
        Assert.Equal("NvmeDisk", kept!.GetValue(""));
    }

    [Fact]
    public void WriteProtectedWindowsKeyIsNotReportedAsRemoved()
    {
        // The exact 26100.9550 shape: readable, writable only by TrustedInstaller, default
        // "NvmeDisk". The old code logged [REMOVED] after DeleteSubKeyTree quietly did nothing.
        using var parent = Registry.CurrentUser.CreateSubKey($@"{_root}\SafeBoot\Minimal", writable: true)!;
        try
        {
            using (var osOwned = parent.CreateSubKey(Leaf, writable: true)!)
            {
                osOwned.SetValue("", "NvmeDisk", RegistryValueKind.String);
                DenyWrites(osOwned);
            }

            var log = new List<string>();
            var removed = 0;

            PatchService.RemoveOwnedSafeBootKey(
                Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "Safe Boot Minimal", ref removed, log.Add);

            var line = Assert.Single(log);
            Assert.Contains("[PRESERVED]", line);
            Assert.DoesNotContain("[REMOVED]", line);
            Assert.Equal(0, removed);
            Assert.Contains(Leaf, parent.GetSubKeyNames());
        }
        finally
        {
            RestoreAccess(_root);
        }
    }

    [Fact]
    public void WriteProtectedKeyHoldingOurValueIsReportedAsFailedNotRemoved()
    {
        using var parent = Registry.CurrentUser.CreateSubKey($@"{_root}\SafeBoot\Minimal", writable: true)!;
        try
        {
            using (var ours = parent.CreateSubKey(Leaf, writable: true)!)
            {
                ours.SetValue("", "Storage Disks", RegistryValueKind.String);
                DenyWrites(ours);
            }

            var log = new List<string>();
            var removed = 0;

            PatchService.RemoveOwnedSafeBootKey(
                Registry.CurrentUser, $@"{_root}\SafeBoot\Minimal", Leaf, "Safe Boot Minimal", ref removed, log.Add);

            Assert.Contains("[FAIL]", Assert.Single(log));
            Assert.Equal(0, removed);
        }
        finally
        {
            RestoreAccess(_root);
        }
    }

    private static void DenyWrites(RegistryKey key)
    {
        var security = key.GetAccessControl();
        security.AddAccessRule(new RegistryAccessRule(
            WindowsIdentity.GetCurrent().User!,
            RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        key.SetAccessControl(security);
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

    public void Dispose() => RestoreAccessAndDelete(_root);

    private static void RestoreAccessAndDelete(string root)
    {
        RestoreAccess(root);
        try { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
        catch { /* leftover test key under a GUID path is harmless */ }
    }

    // Re-grant before deleting, otherwise the deny ACE blocks cleanup too.
    private static void RestoreAccess(string root)
    {
        try
        {
            using var minimal = Registry.CurrentUser.OpenSubKey($@"{root}\SafeBoot\Minimal", writable: true);
            using var leaf = minimal?.OpenSubKey(
                // The owner keeps READ_CONTROL and WRITE_DAC whatever the DACL says; asking for
                // TakeOwnership too made a full deny ACE refuse the open and leak the test key.
                Leaf, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
            if (leaf is not null)
            {
                var security = leaf.GetAccessControl(AccessControlSections.Access);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                // A deny ACE outranks any allow, so drop ours before granting.
                security.PurgeAccessRules(WindowsIdentity.GetCurrent().User!);
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
    }
}
