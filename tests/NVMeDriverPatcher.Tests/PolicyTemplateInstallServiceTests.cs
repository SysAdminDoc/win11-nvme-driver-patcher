using System.IO;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PolicyTemplateInstallServiceTests : IDisposable
{
    private readonly string _src;
    private readonly string _dst;

    public PolicyTemplateInstallServiceTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "NVMePatcher_Policy_" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(root, "admx");
        _dst = Path.Combine(root, "PolicyDefinitions");
        Directory.CreateDirectory(Path.Combine(_src, "en-US"));
        Directory.CreateDirectory(Path.Combine(_src, "de-DE"));
        File.WriteAllText(Path.Combine(_src, "NVMeDriverPatcher.admx"), "<policyDefinitions/>");
        File.WriteAllText(Path.Combine(_src, "en-US", "NVMeDriverPatcher.adml"), "<policyDefinitionResources/>");
        File.WriteAllText(Path.Combine(_src, "de-DE", "NVMeDriverPatcher.adml"), "<policyDefinitionResources/>");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_src)!, recursive: true); } catch { }
    }

    [Fact]
    public void BuildPlan_MapsAdmxToRootAndAdmlToLanguageFolders()
    {
        var plan = PolicyTemplateInstallService.BuildPlan(_src, _dst);

        Assert.Empty(plan.Warnings);
        Assert.Equal(3, plan.Copies.Count);
        Assert.Contains(plan.Copies, c => c.Dest == Path.Combine(_dst, "NVMeDriverPatcher.admx"));
        Assert.Contains(plan.Copies, c => c.Dest == Path.Combine(_dst, "en-US", "NVMeDriverPatcher.adml"));
        Assert.Contains(plan.Copies, c => c.Dest == Path.Combine(_dst, "de-DE", "NVMeDriverPatcher.adml"));
    }

    [Fact]
    public void BuildPlan_MissingSource_Warns()
    {
        var plan = PolicyTemplateInstallService.BuildPlan(Path.Combine(_src, "nope"), _dst);
        Assert.Empty(plan.Copies);
        Assert.NotEmpty(plan.Warnings);
    }

    [Fact]
    public void Install_CopiesFilesIntoMatchingFolders_ThenUninstallRemovesThem()
    {
        var (ok, _) = PolicyTemplateInstallService.Install(_src, _dst);
        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(_dst, "NVMeDriverPatcher.admx")));
        Assert.True(File.Exists(Path.Combine(_dst, "en-US", "NVMeDriverPatcher.adml")));
        Assert.True(File.Exists(Path.Combine(_dst, "de-DE", "NVMeDriverPatcher.adml")));

        var (uok, _) = PolicyTemplateInstallService.Uninstall(_src, _dst);
        Assert.True(uok);
        Assert.False(File.Exists(Path.Combine(_dst, "NVMeDriverPatcher.admx")));
        Assert.False(File.Exists(Path.Combine(_dst, "en-US", "NVMeDriverPatcher.adml")));
    }

    [Fact]
    public void Install_IsIdempotent_OverwritesWithoutError()
    {
        Assert.True(PolicyTemplateInstallService.Install(_src, _dst).Success);
        Assert.True(PolicyTemplateInstallService.Install(_src, _dst).Success);
    }

    [Fact]
    public void Install_EmptySource_FailsCleanly()
    {
        var empty = Path.Combine(Path.GetDirectoryName(_src)!, "emptysrc");
        Directory.CreateDirectory(empty);
        var (ok, summary) = PolicyTemplateInstallService.Install(empty, _dst);
        Assert.False(ok);
        Assert.Contains("No policy templates", summary);
    }
}
