using NVMeDriverPatcher.ViewModels;

namespace NVMeDriverPatcher.Tests;

public sealed class MainViewModelProcessTests
{
    [Fact]
    public void CreateExplorerStartInfo_UsesTokenizedPathArgument()
    {
        const string path = @"C:\Users\alice\NVMe Kits";

        var psi = MainViewModel.CreateExplorerStartInfo(path);

        Assert.Equal("explorer.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal([path], psi.ArgumentList.ToArray());
        Assert.Equal(string.Empty, psi.Arguments);
    }

    [Fact]
    public void CreateNotepadStartInfo_UsesTokenizedFileArgument()
    {
        const string path = @"C:\Users\alice\NVMe Kits\Verify Patch.ps1";

        var psi = MainViewModel.CreateNotepadStartInfo(path);

        Assert.Equal("notepad.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal([path], psi.ArgumentList.ToArray());
        Assert.Equal(string.Empty, psi.Arguments);
    }

    [Theory]
    [InlineData("https://github.com/SysAdminDoc/win11-nvme-driver-patcher", true)]
    [InlineData("http://github.com/SysAdminDoc/win11-nvme-driver-patcher", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:windowsupdate", false)]
    [InlineData("/relative/path", false)]
    [InlineData("https://user:password@example.com/", false)]
    public void IsAllowedBrowserUrl_AllowsOnlyAbsoluteHttpsWithoutCredentials(string url, bool expected)
    {
        Assert.Equal(expected, MainViewModel.IsAllowedBrowserUrl(url));
    }

    [Fact]
    public void IsExistingTextFile_RejectsZipSupportBundlePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.ViewModelTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var reportPath = Path.Combine(tempDir, "NVMe_Diagnostics_20260419.txt");
            var bundlePath = Path.Combine(tempDir, "NVMe_SupportBundle_20260419.zip");
            File.WriteAllText(reportPath, "report");
            File.WriteAllText(bundlePath, "zip");

            Assert.True(MainViewModel.IsExistingTextFile(reportPath));
            Assert.False(MainViewModel.IsExistingTextFile(bundlePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsExistingTextFile_RejectsRelativePathsEvenWhenTheyExist()
    {
        var relativePath = $"NVMe_Diagnostics_relative_{Guid.NewGuid():N}.txt";
        var fullPath = Path.Combine(Environment.CurrentDirectory, relativePath);
        try
        {
            File.WriteAllText(fullPath, "report");

            Assert.False(MainViewModel.IsExistingTextFile(relativePath));
        }
        finally
        {
            try { File.Delete(fullPath); } catch { }
        }
    }
}
