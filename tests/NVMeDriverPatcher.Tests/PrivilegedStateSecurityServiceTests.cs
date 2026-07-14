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
