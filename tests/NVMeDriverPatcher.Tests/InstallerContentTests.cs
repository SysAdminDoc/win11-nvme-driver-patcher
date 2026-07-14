using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

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
        Assert.Contains("sc.exe showsid", script);
        Assert.Contains("FileSystemRights]::Modify", script);
        Assert.Contains("System-log readiness probe", script);
    }

    [Fact]
    public void WatchdogManualInstaller_GrantsSharedStateAccessByWellKnownSid()
    {
        var program = Read("src", "NVMeDriverPatcher.Watchdog", "Program.cs");
        Assert.Contains("GrantStateDirectoryAccess", program);
        Assert.Contains("icacls.exe", program);
        Assert.Contains("S-1-5-80-153395662-1388266646-3167021078-3452987457-2818666036", program);
    }
}
