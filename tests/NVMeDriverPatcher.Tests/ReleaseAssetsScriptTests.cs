using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace NVMeDriverPatcher.Tests;

// Exercises scripts/Validate-ReleaseAssets.ps1 -ExpectSigned behavior against a minimal fixture
// repo root. The fixture's release-artifacts.json declares one sign:true artifact; the artifact
// file is an unsigned dummy. Without -ExpectSigned the contract passes; with it (the case where
// signing secrets were configured) the missing signature fails the release.
public sealed class ReleaseAssetsScriptTests
{
    [Fact]
    public void Validate_WithoutExpectSigned_PassesForUnsignedArtifact()
    {
        using var repo = AssetsFixture.Create();
        var result = RunScript(repo.Path, expectSigned: false);
        Assert.True(result.ExitCode == 0, $"expected pass; stdout: {result.StdOut}\nstderr: {result.StdErr}");
    }

    [Fact]
    public void Validate_WithExpectSigned_FailsForUnsignedArtifact()
    {
        using var repo = AssetsFixture.Create();
        var result = RunScript(repo.Path, expectSigned: true);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Authenticode", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    private static ScriptResult RunScript(string repoRoot, bool expectSigned)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        // Drop any inherited PSModulePath (e.g. a pwsh-7 path from the spawning shell) so Windows
        // PowerShell uses its default module path and can auto-load Get-FileHash / Get-AuthenticodeSignature.
        process.StartInfo.Environment.Remove("PSModulePath");
        // Invoked via -File, which passes every arg as a literal string ($true/$false don't
        // evaluate). A [switch] under -File is set by presence: add the bare flag for true,
        // omit it for false (its default).
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
                                  "-Version", "9.9.9", "-RepoRoot", repoRoot })
            process.StartInfo.ArgumentList.Add(a);
        if (expectSigned) process.StartInfo.ArgumentList.Add("-ExpectSigned");

        process.Start();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return new ScriptResult(process.ExitCode, stdOut, stdErr);
    }

    private static string ScriptPath([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(repoRoot, "scripts", "Validate-ReleaseAssets.ps1");
    }

    private sealed record ScriptResult(int ExitCode, string StdOut, string StdErr);

    private sealed class AssetsFixture : IDisposable
    {
        private AssetsFixture(string path) => Path = path;
        public string Path { get; }

        public static AssetsFixture Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"NVMeDriverPatcher.AssetsScript.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(System.IO.Path.Combine(root, "publish"));

            // One sign:true artifact — an unsigned dummy "exe".
            var artifactRel = "publish/app.exe";
            var artifactFull = System.IO.Path.Combine(root, "publish", "app.exe");
            File.WriteAllBytes(artifactFull, new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03 }); // MZ + filler
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactFull))).ToLowerInvariant();

            Write(root, "packaging/release-artifacts.json", $$"""
                {
                  "artifacts": [
                    { "id": "gui", "path": "{{artifactRel}}", "required": true, "sign": true, "checksum": true, "upload": true }
                  ]
                }
                """);
            // Satisfy the checksum contract so only the signature check distinguishes the two cases.
            File.WriteAllText(System.IO.Path.Combine(root, "publish", "app.exe.sha256"), $"{hash}  app.exe");
            File.WriteAllText(System.IO.Path.Combine(root, "publish", "SHA256SUMS.txt"), $"{hash}  app.exe\n");

            return new AssetsFixture(root);
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
