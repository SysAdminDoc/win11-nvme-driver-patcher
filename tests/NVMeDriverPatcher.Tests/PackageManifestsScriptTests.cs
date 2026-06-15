using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace NVMeDriverPatcher.Tests;

// Exercises scripts/Update-PackageManifests.ps1: it must substitute the tagged URL, the real exe
// SHA-256, and the version into the Chocolatey + Scoop manifests, removing the REPLACE_ME
// placeholders while leaving the Scoop autoupdate $version template intact.
public sealed class PackageManifestsScriptTests
{
    [Fact]
    public void Update_RewritesChocoAndScoopWithUrlHashAndVersion()
    {
        using var repo = ManifestFixture.Create(out var expectedHash);

        var result = RunScript(repo.Path, "9.9.9", repo.ExePath);
        Assert.True(result.ExitCode == 0, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");

        var choco = File.ReadAllText(Path.Combine(repo.Path, "packaging/chocolatey/tools/chocolateyInstall.ps1"));
        Assert.Contains("releases/download/v9.9.9/NVMeDriverPatcher.exe", choco);
        Assert.Contains(expectedHash, choco);
        Assert.DoesNotContain("REPLACE_ME", choco);

        var nuspec = File.ReadAllText(Path.Combine(repo.Path, "packaging/chocolatey/nvme-driver-patcher.nuspec"));
        Assert.Contains("<version>9.9.9</version>", nuspec);

        var scoopText = File.ReadAllText(Path.Combine(repo.Path, "packaging/scoop/nvme-driver-patcher.json"));
        Assert.DoesNotContain("REPLACE_ME", scoopText);
        using var scoop = JsonDocument.Parse(scoopText); // must still be valid JSON
        var root = scoop.RootElement;
        Assert.Equal("9.9.9", root.GetProperty("version").GetString());
        var arch64 = root.GetProperty("architecture").GetProperty("64bit");
        Assert.Contains("download/v9.9.9/NVMeDriverPatcher.exe", arch64.GetProperty("url").GetString());
        Assert.Equal(expectedHash, arch64.GetProperty("hash").GetString());
        // The autoupdate template must keep its literal $version token, not be pinned to 9.9.9.
        Assert.Contains("download/v$version/NVMeDriverPatcher.exe",
            root.GetProperty("autoupdate").GetProperty("architecture").GetProperty("64bit").GetProperty("url").GetString());
    }

    private static ScriptResult RunScript(string repoRoot, string version, string exePath)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        process.StartInfo.Environment.Remove("PSModulePath"); // let Windows PowerShell find Get-FileHash
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
                                  "-Version", version, "-ExePath", exePath, "-RepoRoot", repoRoot })
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
        private ManifestFixture(string path, string exePath) { Path = path; ExePath = exePath; }
        public string Path { get; }
        public string ExePath { get; }

        public static ManifestFixture Create(out string expectedHash)
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
                "<package><metadata><id>nvme-driver-patcher</id><version>5.0.0</version></metadata></package>");
            Write(root, "packaging/scoop/nvme-driver-patcher.json", """
                {
                    "version": "5.0.0",
                    "architecture": {
                        "64bit": {
                            "url": "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v5.0.0/NVMeDriverPatcher.exe#/NVMeDriverPatcher.exe",
                            "hash": "REPLACE_ME_WITH_RELEASE_SHA256"
                        }
                    },
                    "autoupdate": {
                        "architecture": {
                            "64bit": { "url": "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$version/NVMeDriverPatcher.exe#/NVMeDriverPatcher.exe" }
                        }
                    }
                }
                """);

            var exePath = System.IO.Path.Combine(root, "publish", "NVMeDriverPatcher.exe");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(exePath)!);
            File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 9, 8, 7, 6 });
            expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exePath))).ToLowerInvariant();

            return new ManifestFixture(root, exePath);
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
