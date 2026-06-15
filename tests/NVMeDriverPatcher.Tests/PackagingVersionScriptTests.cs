using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NVMeDriverPatcher.Tests;

public sealed class PackagingVersionScriptTests
{
    [Fact]
    public void ValidateReleaseVersions_AllowsVersionPlaceholdersInPackagingMarkdown()
    {
        using var repo = VersionFixture.Create("5.0.0",
            "Use `NVMeDriverPatcher-<version>.msi` and `NVMeDriverPatcher-<version>.intunewin`.");

        var result = RunScript(repo.Path);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ValidateReleaseVersions_RejectsStaleMajorPlaceholdersInPackagingMarkdown()
    {
        using var repo = VersionFixture.Create("5.0.0",
            "Use `NVMeDriverPatcher-4.x.y.msi`.");

        var result = RunScript(repo.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("4.x.y", result.StdOut);
        Assert.Contains("packaging", result.StdOut);
    }

    [Fact]
    public void ValidateReleaseVersions_RejectsStaleConcreteArtifactVersionsInPackagingMarkdown()
    {
        using var repo = VersionFixture.Create("5.0.0",
            "Use `NVMeDriverPatcher-4.6.1.msi`.");

        var result = RunScript(repo.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("4.6.1", result.StdOut);
        Assert.Contains("5.0.0", result.StdOut);
    }

    [Fact]
    public void ValidateReleaseVersions_RejectsStaleReadmeVersionBadge()
    {
        using var repo = VersionFixture.Create("5.0.0", "Use `NVMeDriverPatcher-<version>.msi`.");
        File.WriteAllText(Path.Combine(repo.Path, "README.md"),
            "![Version](https://img.shields.io/badge/Version-4.6.0-blue)");

        var result = RunScript(repo.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("README.md", result.StdOut);
    }

    [Fact]
    public void ValidateReleaseVersions_AcceptsMatchingNarrativeVersions()
    {
        using var repo = VersionFixture.Create("5.0.0", "Use `NVMeDriverPatcher-<version>.msi`.");
        File.WriteAllText(Path.Combine(repo.Path, "README.md"),
            "![Version](https://img.shields.io/badge/Version-5.0.0-blue)");
        File.WriteAllText(Path.Combine(repo.Path, "ROADMAP.md"), "Current ship: **v5.0.0**.");

        var result = RunScript(repo.Path);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ValidateReleaseVersions_RejectsStaleRoadmapCurrentShip()
    {
        using var repo = VersionFixture.Create("5.0.0", "Use `NVMeDriverPatcher-<version>.msi`.");
        File.WriteAllText(Path.Combine(repo.Path, "ROADMAP.md"), "Current ship: **v4.6.0**.");

        var result = RunScript(repo.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ROADMAP.md", result.StdOut);
    }

    private static ScriptResult RunScript(string repoRoot)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(ScriptPath());
        process.StartInfo.ArgumentList.Add("-RepoRoot");
        process.StartInfo.ArgumentList.Add(repoRoot);

        process.Start();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);

        return new ScriptResult(process.ExitCode, stdOut, stdErr);
    }

    private static string ScriptPath([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(repoRoot, "scripts", "Validate-ReleaseVersions.ps1");
    }

    private sealed record ScriptResult(int ExitCode, string StdOut, string StdErr);

    private sealed class VersionFixture : IDisposable
    {
        private VersionFixture(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static VersionFixture Create(string version, string packagingMarkdown)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"NVMeDriverPatcher.VersionScript.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            Write(root, "Directory.Build.props", $"""
                <Project>
                  <PropertyGroup>
                    <VersionPrefix>{version}</VersionPrefix>
                  </PropertyGroup>
                </Project>
                """);
            Write(root, "packaging/powershell/NVMeDriverPatcher.psd1", $"@{{ ModuleVersion = '{version}' }}");
            Write(root, "packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml", $"""
                PackageVersion: {version}
                InstallerUrl: https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v{version}/NVMeDriverPatcher.exe
                """);
            Write(root, "packaging/wix/NVMeDriverPatcher.wxs", $"""<Package Version="{version}.0" />""");
            Write(root, "packaging/intune/Detect-NVMeDriverPatcher.ps1", $"$minVersion = [Version]'{version}'");
            Write(root, "packaging/intune/README.md", packagingMarkdown);
            Write(root, "src/NVMeDriverPatcher/Models/AppConfig.cs", $"""private const string FallbackVersionLiteral = "{version}";""");

            return new VersionFixture(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }

        private static void Write(string root, string relativePath, string content)
        {
            var path = System.IO.Path.Combine(root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
