using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace NVMeDriverPatcher.Tests;

// Exercises the one release-metadata generator across winget, Scoop, and Chocolatey. Every
// architecture must stay bound to its matching PE, URL, and SHA-256.
public sealed class PackageManifestsScriptTests
{
    [Fact]
    public void Update_RewritesChocoAndScoopWithUrlHashAndVersion()
    {
        using var repo = ManifestFixture.Create(out var expectedHash, out var expectedArm64Hash);

        var result = RunScript(repo.Path, "9.9.9", repo.ExePath, repo.Arm64ExePath);
        Assert.True(result.ExitCode == 0, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");

        var choco = File.ReadAllText(Path.Combine(repo.Path, "packaging/chocolatey/tools/chocolateyInstall.ps1"));
        Assert.Contains("releases/download/v9.9.9/NVMeDriverPatcher.exe", choco);
        Assert.Contains(expectedHash, choco);
        Assert.DoesNotContain("REPLACE_ME", choco);

        var nuspec = File.ReadAllText(Path.Combine(repo.Path, "packaging/chocolatey/nvme-driver-patcher.nuspec"));
        Assert.Contains("<version>9.9.9</version>", nuspec);

        var winget = File.ReadAllText(Path.Combine(repo.Path, "packaging/winget/SysAdminDoc.NVMeDriverPatcher.installer.yaml"));
        AssertWingetArchitecture(winget, "x64", "NVMeDriverPatcher.exe", expectedHash);
        AssertWingetArchitecture(winget, "arm64", "NVMeDriverPatcher-win-arm64.exe", expectedArm64Hash);

        var scoopText = File.ReadAllText(Path.Combine(repo.Path, "packaging/scoop/nvme-driver-patcher.json"));
        Assert.DoesNotContain("REPLACE_ME", scoopText);
        using var scoop = JsonDocument.Parse(scoopText); // must still be valid JSON
        var root = scoop.RootElement;
        Assert.Equal("9.9.9", root.GetProperty("version").GetString());
        var arch64 = root.GetProperty("architecture").GetProperty("64bit");
        Assert.Contains("download/v9.9.9/NVMeDriverPatcher.exe", arch64.GetProperty("url").GetString());
        Assert.Equal(expectedHash, arch64.GetProperty("hash").GetString());
        // ARM64 block must exist with versioned URL and correct hash
        var archArm64 = root.GetProperty("architecture").GetProperty("arm64");
        Assert.Contains("download/v9.9.9/NVMeDriverPatcher-win-arm64.exe", archArm64.GetProperty("url").GetString());
        Assert.EndsWith("#/NVMeDriverPatcher.exe", archArm64.GetProperty("url").GetString());
        Assert.Equal(expectedArm64Hash, archArm64.GetProperty("hash").GetString());
        // The autoupdate template must keep its literal $version token, not be pinned to 9.9.9.
        Assert.Contains("download/v$version/NVMeDriverPatcher.exe",
            root.GetProperty("autoupdate").GetProperty("architecture").GetProperty("64bit").GetProperty("url").GetString());
        Assert.Contains("download/v$version/NVMeDriverPatcher-win-arm64.exe",
            root.GetProperty("autoupdate").GetProperty("architecture").GetProperty("arm64").GetProperty("url").GetString());
        Assert.EndsWith("#/NVMeDriverPatcher.exe",
            root.GetProperty("autoupdate").GetProperty("architecture").GetProperty("arm64").GetProperty("url").GetString());
    }

    [Fact]
    public void Update_OutputRootPublishesAllMetadataWithoutMutatingTemplates()
    {
        using var repo = ManifestFixture.Create(out var expectedHash, out var expectedArm64Hash);
        var sourceScoop = File.ReadAllText(Path.Combine(repo.Path, "packaging/scoop/nvme-driver-patcher.json"));
        var output = Path.Combine(repo.Path, "generated");

        var result = RunScript(repo.Path, "9.9.9", repo.ExePath, repo.Arm64ExePath, output);
        Assert.True(result.ExitCode == 0, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");

        Assert.Equal(sourceScoop, File.ReadAllText(Path.Combine(repo.Path, "packaging/scoop/nvme-driver-patcher.json")));
        var generatedScoop = File.ReadAllText(Path.Combine(output, "nvme-driver-patcher.json"));
        Assert.Contains(expectedHash, generatedScoop);
        Assert.Contains(expectedArm64Hash, generatedScoop);
        Assert.True(File.Exists(Path.Combine(output, "winget", "SysAdminDoc.NVMeDriverPatcher.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "winget", "SysAdminDoc.NVMeDriverPatcher.installer.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "winget", "SysAdminDoc.NVMeDriverPatcher.locale.en-US.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "chocolatey-package", "nvme-driver-patcher.nuspec")));
    }

    [Fact]
    public void Update_SwappedPeArchitecturesFailsBeforeMutatingTemplates()
    {
        using var repo = ManifestFixture.Create(out _, out _);
        var sourceScoop = File.ReadAllText(Path.Combine(repo.Path, "packaging/scoop/nvme-driver-patcher.json"));

        var result = RunScript(repo.Path, "9.9.9", repo.Arm64ExePath, repo.ExePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("expected x64", result.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceScoop, File.ReadAllText(Path.Combine(repo.Path, "packaging/scoop/nvme-driver-patcher.json")));
    }

    private static void AssertWingetArchitecture(string yaml, string architecture, string assetName, string expectedHash)
    {
        var block = System.Text.RegularExpressions.Regex.Match(
            yaml,
            $@"- Architecture:\s*{architecture}(?<body>.*?)(?=\r?\n\s*- Architecture:|\r?\nManifestType:)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(block.Success, $"Missing winget {architecture} block.");
        Assert.Contains($"/{assetName}", block.Value);
        Assert.Contains(expectedHash.ToUpperInvariant(), block.Value);
    }

    private static ScriptResult RunScript(
        string repoRoot,
        string version,
        string exePath,
        string arm64ExePath,
        string? outputRoot = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        process.StartInfo.Environment.Remove("PSModulePath"); // let Windows PowerShell find Get-FileHash
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
                                      "-Version", version, "-ExePath", exePath, "-RepoRoot", repoRoot };
        args.Add("-Arm64ExePath");
        args.Add(arm64ExePath);
        if (outputRoot is not null)
        {
            args.Add("-OutputRoot");
            args.Add(outputRoot);
        }
        foreach (var a in args)
            process.StartInfo.ArgumentList.Add(a);

        process.Start();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return new ScriptResult(process.ExitCode, stdOut, stdErr);
    }

    private static string ScriptPath([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(repoRoot, "scripts", "Update-PackageManifests.ps1");
    }

    private sealed record ScriptResult(int ExitCode, string StdOut, string StdErr);

    private sealed class ManifestFixture : IDisposable
    {
        private ManifestFixture(string path, string exePath, string arm64ExePath) { Path = path; ExePath = exePath; Arm64ExePath = arm64ExePath; }
        public string Path { get; }
        public string ExePath { get; }
        public string Arm64ExePath { get; }

        public static ManifestFixture Create(out string expectedHash, out string expectedArm64Hash)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"NVMeDriverPatcher.Manifests.Tests.{Guid.NewGuid():N}");

            Write(root, "packaging/chocolatey/tools/chocolateyInstall.ps1", """
                $packageArgs = @{
                    packageName    = 'nvme-driver-patcher'
                    url64bit       = 'https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v5.0.0/NVMeDriverPatcher.exe'
                    checksum64     = 'REPLACE_ME_WITH_RELEASE_SHA256'
                    checksumType64 = 'sha256'
                }
                """);
            Write(root, "packaging/chocolatey/nvme-driver-patcher.nuspec",
                "<package xmlns=\"http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd\"><metadata><id>nvme-driver-patcher</id><version>5.0.0</version></metadata></package>");
            Write(root, "packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml", """
                PackageIdentifier: SysAdminDoc.NVMeDriverPatcher
                PackageVersion: 5.0.0
                DefaultLocale: en-US
                ManifestType: version
                ManifestVersion: 1.12.0
                """);
            Write(root, "packaging/winget/SysAdminDoc.NVMeDriverPatcher.installer.yaml", """
                PackageIdentifier: SysAdminDoc.NVMeDriverPatcher
                PackageVersion: 5.0.0
                Installers:
                  - Architecture: x64
                    InstallerType: portable
                    InstallerUrl: https://example.invalid/NVMeDriverPatcher.exe
                    InstallerSha256: REPLACE_ME_WITH_RELEASE_SHA256
                  - Architecture: arm64
                    InstallerType: portable
                    InstallerUrl: https://example.invalid/NVMeDriverPatcher-win-arm64.exe
                    InstallerSha256: REPLACE_ME_WITH_ARM64_SHA256
                ManifestType: installer
                ManifestVersion: 1.12.0
                """);
            Write(root, "packaging/winget/SysAdminDoc.NVMeDriverPatcher.locale.en-US.yaml", """
                PackageIdentifier: SysAdminDoc.NVMeDriverPatcher
                PackageVersion: 5.0.0
                PackageLocale: en-US
                Publisher: Test
                PackageName: Test
                License: MIT
                ShortDescription: Test fixture
                ManifestType: defaultLocale
                ManifestVersion: 1.12.0
                """);
            Write(root, "packaging/scoop/nvme-driver-patcher.json", """
                {
                    "version": "5.0.0",
                    "bin": "NVMeDriverPatcher.exe",
                    "architecture": {
                        "64bit": {
                            "url": "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v5.0.0/NVMeDriverPatcher.exe#/NVMeDriverPatcher.exe",
                            "hash": "REPLACE_ME_WITH_RELEASE_SHA256"
                        },
                        "arm64": {
                            "url": "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v5.0.0/NVMeDriverPatcher-win-arm64.exe#/NVMeDriverPatcher.exe",
                            "hash": "REPLACE_ME_WITH_ARM64_SHA256"
                        }
                    },
                    "autoupdate": {
                        "architecture": {
                            "64bit": { "url": "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$version/NVMeDriverPatcher.exe#/NVMeDriverPatcher.exe" },
                            "arm64": { "url": "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$version/NVMeDriverPatcher-win-arm64.exe#/NVMeDriverPatcher.exe" }
                        }
                    }
                }
                """);

            var publishDir = System.IO.Path.Combine(root, "publish");
            Directory.CreateDirectory(publishDir);

            var exePath = System.IO.Path.Combine(publishDir, "NVMeDriverPatcher.exe");
            File.WriteAllBytes(exePath, MinimalPe(0x8664, 0x11));
            expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exePath))).ToLowerInvariant();

            var arm64ExePath = System.IO.Path.Combine(publishDir, "NVMeDriverPatcher-win-arm64.exe");
            File.WriteAllBytes(arm64ExePath, MinimalPe(0xAA64, 0x22));
            expectedArm64Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(arm64ExePath))).ToLowerInvariant();

            return new ManifestFixture(root, exePath, arm64ExePath);
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

        private static byte[] MinimalPe(ushort machine, byte marker)
        {
            var bytes = new byte[128];
            bytes[0] = 0x4D;
            bytes[1] = 0x5A;
            BitConverter.GetBytes(64).CopyTo(bytes, 0x3C);
            bytes[64] = 0x50;
            bytes[65] = 0x45;
            BitConverter.GetBytes(machine).CopyTo(bytes, 68);
            bytes[80] = marker;
            return bytes;
        }
    }
}
