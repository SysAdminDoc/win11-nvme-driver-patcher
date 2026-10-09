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

    [Theory]
    [InlineData("feature_ids.json")]
    [InlineData("windows_build_rules.json")]
    [InlineData("compat.json")]
    public void Msi_InstallsEveryCuratedDataFileBesideTheExe(string fileName)
    {
        // Without feature_ids.json beside the exe an MSI install fell back to an empty catalog,
        // so the FeatureStore evidence probe had no IDs to look for.
        var wxs = Read("packaging", "wix", "NVMeDriverPatcher.wxs");
        Assert.Contains($@"<File Source=""$(var.PublishDir)\{fileName}""", wxs);
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
        // INSTALLFOLDER can be set on the command line to a folder inside Program Files. Without an
        // explicit DACL it inherits its parent's, so a folder under another vendor's writable
        // directory leaves the watchdog binary writable by a standard user while the MSI registers
        // it as an auto-start service and invokes it from a deferred SYSTEM custom action.
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

        // A 3-second probe only proved the process started, and that let a service ship that could
        // never read its own state and died at ~t+60-90s. The smoke must outlast the flush loop's
        // third failure and then prove the loop actually published state.
        Assert.Contains("LivenessSeconds", script);
        Assert.Matches(@"\$LivenessSeconds\s*=\s*1[5-9]\d|\$LivenessSeconds\s*=\s*[2-9]\d\d", script);
        Assert.Contains(@"NVMePatcher\Watchdog\watchdog.json", script);

        // The smoke runs elevated, so a planted sc.exe would run as administrator -- and a stub
        // returning success would make every assertion above pass against a service that is not
        // there. It must resolve sc.exe rather than launch it by bare name.
        Assert.DoesNotContain("& sc.exe", script);
        Assert.Contains("SpecialFolder]::System)) 'sc.exe'", script);
    }

    [Fact]
    public void Msi_InstallsOnlyUnderProgramFiles_AndOffersNoFolderPicker()
    {
        // The install folder's DACL protects the files, not the path. A standard user who can rename
        // a parent outside Program Files (C:\Tools) can move the real folder aside and build their
        // own tree at the same path, which the elevated GUI, the SYSTEM custom action and the
        // watchdog service would then run.
        var wxs = Read("packaging", "wix", "NVMeDriverPatcher.wxs");
        Assert.DoesNotContain("WixUI_InstallDir", wxs);
        Assert.DoesNotContain("WIXUI_INSTALLDIR", wxs);
        Assert.Contains(@"<ui:WixUI Id=""WixUI_Minimal""", wxs);

        var launch = Regex.Match(wxs, @"<Launch\s+Condition='([^']+)'\s+Message=""([^""]+)""", RegexOptions.Singleline);
        Assert.True(launch.Success, "The MSI has no Launch condition guarding INSTALLFOLDER.");
        var condition = launch.Groups[1].Value;
        // No "Installed OR": in maintenance mode ADDLOCAL plus INSTALLFOLDER would otherwise put a
        // new component in a folder that never gets the pinned DACL.
        Assert.StartsWith("NOT INSTALLFOLDER OR (", condition);
        Assert.DoesNotContain("Installed", condition);
        Assert.Contains("INSTALLFOLDER ~&lt;&lt; ProgramFiles64Folder", condition);   // case-insensitive prefix
        Assert.Contains("NOT (INSTALLFOLDER ~= ProgramFiles64Folder)", condition);    // never Program Files itself
        Assert.Contains(@"NOT (INSTALLFOLDER &gt;&lt; ""\."")", condition);           // no "..", no "."
        Assert.Contains(@"NOT (INSTALLFOLDER &gt;&lt; ""/"")", condition);
        Assert.DoesNotContain(" OR ", condition[condition.IndexOf('(')..]);         // every guard is ANDed

        // The smoke matches the refusal by its first sentence.
        var script = Read("scripts", "Test-InstallFolderAcl.ps1");
        var refusal = Regex.Match(script, @"\$refusalText = '([^']+)'").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(refusal), "Test-InstallFolderAcl.ps1 no longer names the refusal text.");
        Assert.StartsWith(refusal, launch.Groups[2].Value);
    }

    [Fact]
    public void InstallFolderAclSmoke_ProvesTheDaclIsAppliedAtInstallTime()
    {
        // The unit tests above can only see the .wxs authoring; this smoke is what proves the
        // refusal fires and the DACL actually lands, by trying a user-writable folder outside
        // Program Files first and then installing to the default folder.
        var script = Read("scripts", "Test-InstallFolderAcl.ps1");
        Assert.Contains("INSTALLFOLDER=", script);
        Assert.Contains("ExitCode -eq 0", script);   // an install outside Program Files is a failure
        Assert.Contains("ProgramW6432", script);     // the default install is checked to be under Program Files
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

    [Theory]
    [InlineData("NVMeDriverPatcher.Cli", "static int RegisterTasksCommand(")]
    [InlineData("NVMeDriverPatcher.Watchdog", "static int HandleServiceControl(")]
    public void PersistentRegistration_RefusesADotnetHostedRun(string project, string method)
    {
        // Under `dotnet X.dll` Environment.ProcessPath is dotnet.exe; registering it would leave
        // a task or service that launches bare dotnet.exe forever. The guard names the host, so the
        // ARM64 exes (NVMeDriverPatcher.Cli-win-arm64.exe and friends) still register.
        var program = Read("src", project, "Program.cs");
        var start = program.IndexOf(method, StringComparison.Ordinal);
        Assert.True(start >= 0, method + " not found");
        var body = program[start..];
        var guard = body.IndexOf("Path.GetFileNameWithoutExtension(", StringComparison.Ordinal);
        var register = body.IndexOf(project.EndsWith("Cli") ? "SchedulerService.Register" : "RunSc(\"create\"", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < register, "exe-name guard must run before anything is registered");
        Assert.Contains("\"dotnet\"", body[guard..register]);
        Assert.DoesNotContain("Path.GetFileName(", body[..register]);
    }
}
