using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// Guards the MSI's installer-facing content: no placeholder text (issue #12), the WiX Package
// Version matches the repo version, and the watchdog service account stays LocalService.
public sealed class InstallerContentTests
{
    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    private static string Read(params string[] rel) => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(rel)));

    [Theory]
    [InlineData("packaging", "wix", "License.rtf")]
    [InlineData("packaging", "wix", "en-US.wxl")]
    [InlineData("packaging", "wix", "NVMeDriverPatcher.wxs")]
    public void InstallerAssets_ContainNoPlaceholderText(params string[] rel)
    {
        var text = Read(rel);
        foreach (var placeholder in new[] { "lorem", "ipsum", "dolor sit amet", "TODO", "PLACEHOLDER" })
            Assert.DoesNotContain(placeholder, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LicenseRtf_HasProductSpecificPurposeRiskAndRecovery()
    {
        var rtf = Read("packaging", "wix", "License.rtf");
        Assert.Contains("nvmedisk.sys", rtf, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Risk", rtf, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recovery", rtf, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WxsPackageVersion_MatchesRepoVersion()
    {
        var props = Read("Directory.Build.props");
        var prefix = Regex.Match(props, @"<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value.Trim();
        Assert.False(string.IsNullOrEmpty(prefix));

        var wxs = Read("packaging", "wix", "NVMeDriverPatcher.wxs");
        var wxsVersion = Regex.Match(wxs, @"Version=""([\d.]+)""").Groups[1].Value;
        Assert.StartsWith(prefix, wxsVersion); // e.g. 5.0.0.0 starts with 5.0.0
    }

    [Fact]
    public void WatchdogService_RunsAsLocalService_InWxsAndReadme()
    {
        var wxs = Read("packaging", "wix", "NVMeDriverPatcher.wxs");
        Assert.Contains(@"Account=""NT AUTHORITY\LocalService""", wxs);

        var readme = Read("packaging", "wix", "README.md");
        Assert.Contains("LocalService", readme);
        Assert.DoesNotContain("LocalSystem service", readme); // the corrected misstatement
    }

    [Fact]
    public void WatchdogService_WixPinsRecoveryPrivilegeAndAclContract()
    {
        var wxs = Read("packaging", "wix", "NVMeDriverPatcher.wxs");
        Assert.Contains("FirstFailureActionType=\"restart\"", wxs);
        Assert.Contains("SecondFailureActionType=\"restart\"", wxs);
        Assert.Contains("ThirdFailureActionType=\"none\"", wxs);
        Assert.Contains("FailureActionsWhen=\"failedToStopOrReturnedError\"", wxs);
        Assert.Contains("<RequiredPrivilege Name=\"SeChangeNotifyPrivilege\"", wxs);
        Assert.Contains("ServiceSid=\"restricted\"", wxs);
        Assert.Contains("ExeCommand=\"/grant-runtime-access\"", wxs);
        Assert.Contains("Return=\"check\"", wxs);
        Assert.Contains("Id=\"PRIVILEGEDSTATEFOLDER\"", wxs);
        Assert.Contains("Id=\"WATCHDOGSTATEFOLDER\"", wxs);
        Assert.Contains("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", wxs);
        Assert.Contains(PrivilegedStateSecurityService.WatchdogServiceSid, wxs);
    }

    [Fact]
    public void InstallFolder_GetsAProtectedDaclBecauseItRunsAServiceAndASystemCustomAction()
    {
        // INSTALLFOLDER is user-selectable via WixUI_InstallDir. Without an explicit DACL it
        // inherits its parent's, so an install outside Program Files leaves the watchdog binary
        // writable by a standard user while the MSI registers it as an auto-start service and
        // invokes it from a deferred SYSTEM custom action.
        var wxs = Read("packaging", "wix", "NVMeDriverPatcher.wxs");

        var installFolderSecurity = Regex.Match(
            wxs,
            @"<ComponentGroup Id=""InstallFolderSecurity"" Directory=""INSTALLFOLDER"">.*?</ComponentGroup>",
            RegexOptions.Singleline);
        Assert.True(installFolderSecurity.Success, "INSTALLFOLDER has no security component group.");

        var sddl = Regex.Match(installFolderSecurity.Value, @"<PermissionEx Sddl=""([^""]+)""").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(sddl), "INSTALLFOLDER has no PermissionEx DACL.");

        Assert.StartsWith("O:BA", sddl);           // owner cannot be a standard user who pre-created the dir
        Assert.Contains("D:P", sddl);              // protected: no inheritance from a user-writable parent
        Assert.Contains("(A;OICI;FA;;;SY)", sddl); // SYSTEM full
        Assert.Contains("(A;OICI;FA;;;BA)", sddl); // Administrators full
        Assert.Contains("(A;OICI;0x1200a9;;;BU)", sddl); // Users read+execute only

        // No write, delete, WRITE_DAC or WRITE_OWNER right for any non-administrative principal.
        foreach (var ace in Regex.Matches(sddl, @"\(A;[^)]*\)").Select(m => m.Value))
        {
            var fields = ace.Trim('(', ')').Split(';');
            var rights = fields[2];
            var trustee = fields[5];
            if (trustee is "SY" or "BA" or "CO") continue;
            Assert.True(
                rights == "0x1200a9",
                $"INSTALLFOLDER grants '{rights}' to '{trustee}'; only read+execute (0x1200a9) is allowed there.");
        }

        // The DACL only helps if the group is actually installed.
        Assert.Contains(@"<ComponentGroupRef Id=""InstallFolderSecurity"" />", wxs);
    }

    [Fact]
    public void Msi_RecordsInstallLocationSoThePowerShellModuleNeverSearchesPath()
    {
        var wxs = Read("packaging", "wix", "NVMeDriverPatcher.wxs");
        var registry = Regex.Match(
            wxs,
            @"<RegistryValue[^>]*Name=""InstallLocation""[^>]*>|<RegistryValue(?:(?!/>).)*?Name=""InstallLocation""(?:(?!/>).)*?/>",
            RegexOptions.Singleline);
        Assert.True(registry.Success, "The MSI does not record InstallLocation.");
        Assert.Contains(@"Root=""HKLM""", registry.Value);
        Assert.Contains(@"Value=""[INSTALLFOLDER]""", registry.Value);

        // The module must read exactly the key the MSI writes.
        var psm1 = Read("packaging", "powershell", "NVMeDriverPatcher.psm1");
        Assert.Contains(@"HKLM:\Software\SysAdminDoc\NVMeDriverPatcher", psm1);
        Assert.Contains("InstallLocation", psm1);
    }

    [Fact]
    public void WatchdogPackagingSmoke_ProvesLiveServiceContract()
    {
        var script = Read("scripts", "Test-WatchdogService.ps1");
        Assert.Contains("Get-CimInstance Win32_Service", script);
        Assert.Contains("qfailure", script);
        Assert.Contains("qfailureflag", script);
        Assert.Contains("qprivs", script);
        Assert.Contains("SeChangeNotifyPrivilege", script);
        Assert.Contains("ServiceSidType", script);
        Assert.Contains("showsid", script);
        Assert.Contains("FileSystemRights]::Modify", script);
        Assert.Contains("System-log readiness probe", script);

        // The smoke runs elevated, so a planted sc.exe would run as administrator -- and a stub
        // returning success would make every assertion above pass against a service that is not
        // there. It must resolve sc.exe rather than launch it by bare name.
        Assert.DoesNotContain("& sc.exe", script);
        Assert.Contains("SpecialFolder]::System)) 'sc.exe'", script);
    }

    [Fact]
    public void InstallFolderAclSmoke_ProvesTheDaclIsAppliedAtInstallTime()
    {
        // The unit test above can only see the .wxs authoring; this smoke is what proves the DACL
        // actually lands, by installing into a deliberately user-writable parent directory.
        var script = Read("scripts", "Test-InstallFolderAcl.ps1");
        Assert.Contains("INSTALLFOLDER=", script);
        Assert.Contains("AreAccessRulesProtected", script);
        Assert.Contains("GetOwner", script);
        Assert.Contains("NVMeDriverPatcher.Watchdog.exe", script);
        Assert.Contains("IsInRole", script); // refuses to run unelevated rather than reporting a false pass
        Assert.Contains("'/x', $msi", script); // always uninstalls
    }

    [Fact]
    public void WatchdogManualInstaller_GrantsOnlyDedicatedStateAccess()
    {
        var program = Read("src", "NVMeDriverPatcher.Watchdog", "Program.cs");
        Assert.Contains("GrantStateDirectoryAccess", program);
        Assert.Contains("EnsureForWatchdog", program);
        Assert.DoesNotContain("icacls.exe", program);
    }
}
