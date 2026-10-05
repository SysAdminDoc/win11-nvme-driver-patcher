using System.Security.AccessControl;
using System.Security.Principal;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PrivilegedStateSecurityServiceTests
{
    [Fact]
    public void Descriptor_RejectsStandardUserWritePrecreation()
    {
        var descriptor = Descriptor(
            "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;GW;;;BU)");

        Assert.False(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, StateDirectoryRole.Privileged, requireProtectedAcl: true));
    }

    [Fact]
    public void Descriptor_AcceptsProtectedAdminAndSystemOnlyState()
    {
        var descriptor = Descriptor("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");

        Assert.True(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, StateDirectoryRole.Privileged, requireProtectedAcl: true));
    }

    [Fact]
    public void Descriptor_WatchdogAllowsOnlyServiceWritersBeyondAdmins()
    {
        var descriptor = Descriptor(
            $"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;LS)" +
            $"(A;OICI;0x1301bf;;;{PrivilegedStateSecurityService.WatchdogServiceSid})");

        Assert.True(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, StateDirectoryRole.Watchdog, requireProtectedAcl: true));
        Assert.False(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, StateDirectoryRole.Privileged, requireProtectedAcl: true));
    }

    [Fact]
    public void Descriptor_SharedRootAsTheServiceBuildsIt_Validates()
    {
        // The exact descriptor BuildSecurity(SharedRoot) produces. It never validated: the
        // write-capable mask was built from the COMPOSITE FileSystemRights values (Write 0x116,
        // Modify 0x301BF, FullControl 0x1F01FF), each of which folds in READ_CONTROL and
        // SYNCHRONIZE, so a plain ReadAndExecute+Synchronize ace (0x1200A9) ANDed non-zero and
        // every read-only grant counted as a writer. The root's entire purpose is
        // Users:ReadAndExecute, so it failed its own check forever -- elevated callers silently
        // re-ACLed the tree on every call, and callers that cannot re-ACL saw correct state as
        // untrusted. A check that rejects the state its own writer produces is not a check.
        var descriptor = Descriptor(
            "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x1200a9;;;LS)" +
            $"(A;OICI;0x1200a9;;;{PrivilegedStateSecurityService.WatchdogServiceSid})");

        Assert.True(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, StateDirectoryRole.SharedRoot, requireProtectedAcl: true));
    }

    [Fact]
    public void Descriptor_WatchdogChildGrantsUsersReadSoTheTrayCanDisplayTheVerdict()
    {
        var descriptor = Descriptor(
            "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;LS)" +
            $"(A;OICI;0x1301bf;;;{PrivilegedStateSecurityService.WatchdogServiceSid})" +
            "(A;OICI;0x1200a9;;;BU)");

        Assert.True(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, StateDirectoryRole.Watchdog, requireProtectedAcl: true));
    }

    [Fact]
    public void WatchdogFolderFromBeforeTheTrayGrant_IsTrustedButNoLongerCurrent()
    {
        // The template a released build applied: service writers, no Users read. It still passes
        // the writer check, which is why trust alone kept an existing tree on it forever.
        var released = Descriptor(
            "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;LS)" +
            $"(A;OICI;0x1301bf;;;{PrivilegedStateSecurityService.WatchdogServiceSid})");

        Assert.True(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            released, StateDirectoryRole.Watchdog, requireProtectedAcl: true));
        Assert.False(PrivilegedStateSecurityService.DescriptorCarriesTemplateGrants(
            released, StateDirectoryRole.Watchdog));
    }

    [Fact]
    public void WatchdogFolderWithTheTrayGrant_IsCurrent()
    {
        var current = Descriptor(
            "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;LS)" +
            $"(A;OICI;0x1301bf;;;{PrivilegedStateSecurityService.WatchdogServiceSid})" +
            "(A;OICI;0x1200a9;;;BU)");

        Assert.True(PrivilegedStateSecurityService.DescriptorCarriesTemplateGrants(
            current, StateDirectoryRole.Watchdog));
    }

    [Theory]
    [InlineData(StateDirectoryRole.SharedRoot)]
    [InlineData(StateDirectoryRole.Privileged)]
    [InlineData(StateDirectoryRole.Watchdog)]
    public void EveryRoleTemplate_CarriesItsOwnGrants(StateDirectoryRole role)
    {
        var sddl = PrivilegedStateSecurityService.BuildSecurity(role, isDirectory: true)
            .GetSecurityDescriptorSddlForm(AccessControlSections.All);

        Assert.True(PrivilegedStateSecurityService.DescriptorCarriesTemplateGrants(Descriptor(sddl), role));
    }

    [Fact]
    public void WatchdogTemplate_AppliedToARealFolder_ReadsBackAsCurrent()
    {
        // An entry the kernel stores differently from the template would make every elevated run
        // re-apply the DACL. Only the DACL is written, so this needs no elevation.
        var path = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcherAcl-{Guid.NewGuid():N}");
        var folder = Directory.CreateDirectory(path);
        try
        {
            var dacl = new DirectorySecurity();
            dacl.SetSecurityDescriptorSddlForm(
                PrivilegedStateSecurityService.BuildSecurity(StateDirectoryRole.Watchdog, isDirectory: true)
                    .GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                AccessControlSections.Access);
            folder.SetAccessControl(dacl);

            Assert.True(PrivilegedStateSecurityService.DescriptorCarriesTemplateGrants(
                folder.GetAccessControl(AccessControlSections.Access), StateDirectoryRole.Watchdog));
        }
        finally
        {
            var reset = new DirectorySecurity();
            reset.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            reset.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            folder.SetAccessControl(reset);
            folder.Delete();
        }
    }

    [Theory]
    // A trusted tree with a stale template is fine for a caller that can't re-apply it...
    [InlineData(true, false, false, true)]
    // ...but an elevated caller repairs it instead of trusting the old template forever.
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, true, false)]
    public void RuntimeTree_ElevatedCallerReappliesAStaleTemplate(
        bool trusted,
        bool elevated,
        bool carriesTemplateGrants,
        bool ready)
    {
        Assert.Equal(ready, PrivilegedStateSecurityService.RuntimeTreeIsReady(
            trusted, elevated, () => carriesTemplateGrants));
    }

    [Theory]
    // Read-only grants to a standard user are fine on every role - that is the point of the fix.
    [InlineData("0x1200a9", StateDirectoryRole.SharedRoot, true)]
    [InlineData("0x1200a9", StateDirectoryRole.Privileged, true)]
    // Genuine write bits from a standard user must still be rejected everywhere.
    [InlineData("0x116", StateDirectoryRole.SharedRoot, false)]      // Write
    [InlineData("0x301bf", StateDirectoryRole.SharedRoot, false)]    // Modify
    [InlineData("FA", StateDirectoryRole.SharedRoot, false)]         // FullControl
    [InlineData("0x40000", StateDirectoryRole.Privileged, false)]    // WRITE_DAC alone
    [InlineData("0x80000", StateDirectoryRole.Privileged, false)]    // WRITE_OWNER alone
    [InlineData("0x10000", StateDirectoryRole.Privileged, false)]    // DELETE alone
    [InlineData("GW", StateDirectoryRole.Privileged, false)]         // raw GENERIC_WRITE
    [InlineData("GA", StateDirectoryRole.Privileged, false)]         // raw GENERIC_ALL
    public void Descriptor_SeparatesReadOnlyGrantsFromRealWriteGrants(
        string usersRights,
        StateDirectoryRole role,
        bool expected)
    {
        var descriptor = Descriptor(
            $"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;{usersRights};;;BU)");

        Assert.Equal(expected, PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, role, requireProtectedAcl: true));
    }

    [Fact]
    public void ValidationScope_WatchdogCallerNeverValidatesThePrivilegedChild()
    {
        // The LocalService watchdog has no ace on the privileged child, so reading its DACL throws
        // for that identity. Asking a watchdog caller to validate it made EnsureRuntimeTree fail,
        // fall into the elevated-only repair, throw, and take the service down within ~2 minutes of
        // every start. The scope, not the ACL, is what has to stay narrow.
        var scope = PrivilegedStateSecurityService.RequiredValidationScope(
            @"C:\ProgramData\NVMePatcher", StateDirectoryRole.Watchdog);

        Assert.DoesNotContain(scope, entry => entry.Role == StateDirectoryRole.Privileged);
        Assert.Contains(scope, entry => entry.Role == StateDirectoryRole.SharedRoot);
        Assert.Contains(scope, entry => entry.Role == StateDirectoryRole.Watchdog);
    }

    [Fact]
    public void ValidationScope_MutationCallerStillValidatesEveryChild()
    {
        var scope = PrivilegedStateSecurityService.RequiredValidationScope(
            @"C:\ProgramData\NVMePatcher", StateDirectoryRole.Privileged);

        Assert.Equal(3, scope.Count);
        Assert.Contains(scope, entry => entry.Role == StateDirectoryRole.Privileged);
        Assert.Contains(scope, entry => entry.Role == StateDirectoryRole.Watchdog);
        Assert.Contains(scope, entry => entry.Role == StateDirectoryRole.SharedRoot);
    }

    [Fact]
    public void Descriptor_WatchdogAcceptsLocalServiceOwnershipOfItsOwnState()
    {
        // A watchdog.json published by the service is owned by LocalService — the token holds
        // neither WRITE_OWNER nor SeRestorePrivilege, so it can never be handed to Administrators.
        var descriptor = Descriptor(
            "O:LSD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;LS)");

        Assert.True(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, StateDirectoryRole.Watchdog, requireProtectedAcl: true));
    }

    [Fact]
    public void Descriptor_MutationStateStillRejectsNonAdminOwnership()
    {
        var descriptor = Descriptor("O:LSD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");

        Assert.False(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, StateDirectoryRole.Privileged, requireProtectedAcl: true));
    }

    [Fact]
    public void Descriptor_WatchdogStillRejectsAnUnexpectedWriterRegardlessOfOwner()
    {
        var descriptor = Descriptor(
            "O:LSD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;LS)(A;OICI;GW;;;BU)");

        Assert.False(PrivilegedStateSecurityService.DescriptorAllowsOnlyExpectedWriters(
            descriptor, StateDirectoryRole.Watchdog, requireProtectedAcl: true));
    }

    [Theory]
    [InlineData(FileAttributes.Normal, 1, true)]
    [InlineData(FileAttributes.ReparsePoint, 1, false)]
    [InlineData(FileAttributes.Normal, 0, false)]
    [InlineData(FileAttributes.Normal, 2, false)]
    public void FileMetadata_RejectsReparseAndHardLinkSubstitution(
        FileAttributes attributes,
        uint links,
        bool expected)
    {
        Assert.Equal(expected, PrivilegedStateSecurityService.IsTrustedFileMetadata(attributes, links));
    }

    private static DirectorySecurity Descriptor(string sddl)
    {
        var descriptor = new DirectorySecurity();
        descriptor.SetSecurityDescriptorSddlForm(sddl);
        return descriptor;
    }
}
