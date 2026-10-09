using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// New-UpdateManifest.ps1 signs on Windows PowerShell 5.1 through CNG; the app verifies on .NET 10
// and Validate-ReleaseAssets.ps1 verifies on 5.1 again from the SPKI's raw point. These run all
// three against one fixture so the formats can't drift apart.
public sealed class UpdateManifestScriptTests
{
    private const string ReleaseVersion = "9.9.9";

    [Fact]
    public void ScriptSignedManifest_VerifiesInTheAppAndPassesTheReleaseGate()
    {
        using var repo = ManifestFixture.Create();

        var sign = repo.RunSign();
        Assert.True(sign.ExitCode == 0, $"stdout: {sign.StdOut}\nstderr: {sign.StdErr}");

        var manifest = File.ReadAllBytes(repo.ManifestPath);
        var check = UpdateManifestService.Verify(
            manifest, File.ReadAllText(repo.ManifestPath + ".sig"), [repo.PublicKey], new Version(5, 7, 0), DateTimeOffset.UtcNow);
        Assert.True(check.Success, check.Summary);
        Assert.Equal(new Version(9, 9, 9), check.Manifest!.Version);
        Assert.Equal(new Version(5, 0, 0), check.Manifest.MinimumVersion);
        Assert.Equal(repo.AppHash, check.Manifest.Sha256For("app.exe"));
        Assert.True(check.Manifest.ExpiresUtc > DateTimeOffset.UtcNow.AddDays(360));

        var gate = repo.RunValidate();
        Assert.True(gate.ExitCode == 0, $"stdout: {gate.StdOut}\nstderr: {gate.StdErr}");
    }

    [Fact]
    public void ManifestEditedAfterSigning_FailsTheReleaseGate()
    {
        using var repo = ManifestFixture.Create();
        Assert.Equal(0, repo.RunSign().ExitCode);
        var text = File.ReadAllText(repo.ManifestPath).Replace("\"minimumVersion\": \"5.0.0\"", "\"minimumVersion\": \"1.0.0\"", StringComparison.Ordinal);
        File.WriteAllText(repo.ManifestPath, text, new UTF8Encoding(false));

        var gate = repo.RunValidate();

        Assert.NotEqual(0, gate.ExitCode);
        Assert.Contains("signature does not verify", gate.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestSignedWithAKeyTheAppDoesntTrust_FailsTheReleaseGate()
    {
        using var repo = ManifestFixture.Create();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var strangerPem = Path.Combine(repo.Path, "stranger.pem");
        File.WriteAllText(strangerPem, stranger.ExportPkcs8PrivateKeyPem());
        Assert.Equal(0, repo.RunSign(strangerPem).ExitCode);

        var gate = repo.RunValidate();

        Assert.NotEqual(0, gate.ExitCode);
        Assert.Contains("signature does not verify", gate.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactRebuiltAfterSigning_FailsTheReleaseGate()
    {
        using var repo = ManifestFixture.Create();
        Assert.Equal(0, repo.RunSign().ExitCode);
        repo.WriteApp(Encoding.ASCII.GetBytes("MZ rebuilt after the manifest was signed"));

        var gate = repo.RunValidate();

        Assert.NotEqual(0, gate.ExitCode);
        Assert.Contains("update manifest hash for app.exe does not match the built file", gate.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSigningKey_StopsTheBuildWithTheKeyPath()
    {
        using var repo = ManifestFixture.Create();
        var missing = Path.Combine(repo.Path, "no-such-key.pem");

        var sign = repo.RunSign(missing);

        Assert.NotEqual(0, sign.ExitCode);
        Assert.Contains("signing key not found", sign.StdErr + sign.StdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(repo.ManifestPath));
    }

    private sealed record ScriptResult(int ExitCode, string StdOut, string StdErr);

    private sealed class ManifestFixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        private ManifestFixture(string path) => Path = path;

        public string Path { get; }
        public string ManifestPath => System.IO.Path.Combine(Path, "publish", "update-manifest.json");
        public string KeyPath => System.IO.Path.Combine(Path, "signing.pem");
        public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        public string AppHash { get; private set; } = string.Empty;

        public static ManifestFixture Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"NVMeDriverPatcher.ManifestScript.Tests.{Guid.NewGuid():N}");
            var fixture = new ManifestFixture(root);
            Directory.CreateDirectory(System.IO.Path.Combine(root, "publish"));
            File.WriteAllText(fixture.KeyPath, fixture._key.ExportPkcs8PrivateKeyPem());
            fixture.WriteApp(Encoding.ASCII.GetBytes("MZ release payload"));

            Write(root, "packaging/release-artifacts.json", """
                {
                  "artifacts": [
                    { "id": "gui", "path": "publish/app.exe", "required": true, "sign": true, "checksum": true, "upload": true },
                    { "id": "update-manifest", "path": "publish/update-manifest.json", "required": true, "sign": false, "checksum": false, "upload": true },
                    { "id": "update-manifest-signature", "path": "publish/update-manifest.json.sig", "required": true, "sign": false, "checksum": false, "upload": true }
                  ]
                }
                """);
            Write(root, "src/NVMeDriverPatcher.Core/Services/UpdateManifestService.cs", $$"""
                    // update-manifest-keys:start
                    internal static readonly IReadOnlyList<string> TrustedPublicKeys =
                    [
                        "{{fixture.PublicKey}}",
                    ];
                    // update-manifest-keys:end
                """);
            Write(root, "NVMe_Driver_Patcher.ps1", """
                [CmdletBinding()]
                param(
                    [switch]$Apply,
                    [switch]$Remove,
                    [switch]$Status,
                    [switch]$ExportDiagnostics,
                    [switch]$GenerateVerifyScript,
                    [switch]$ExportRecoveryKit
                )
                $script:MutationRetiredExitCode = 5
                $script:MutationRetiredGuidance = "Use NVMeDriverPatcher.exe or NVMeDriverPatcher.Cli.exe apply --safe; retained: -Status -Remove -ExportDiagnostics -GenerateVerifyScript -ExportRecoveryKit"
                if ($Apply) {
                    [Console]::Error.WriteLine($script:MutationRetiredGuidance)
                    exit $script:MutationRetiredExitCode
                }
                function Test-Administrator { return $true }
                function Test-PatchStatus { return $null }
                function Uninstall-NVMePatch { return $true }
                function Export-SystemDiagnostics { return $null }
                function New-VerificationScript { return $null }
                function Export-RecoveryKit { return $null }
                """);
            return fixture;
        }

        // The exe plus the sidecar and SHA256SUMS line the release build writes beside it.
        public void WriteApp(byte[] bytes)
        {
            File.WriteAllBytes(System.IO.Path.Combine(Path, "publish", "app.exe"), bytes);
            AppHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.WriteAllText(System.IO.Path.Combine(Path, "publish", "app.exe.sha256"), $"{AppHash}  app.exe");
            File.WriteAllText(System.IO.Path.Combine(Path, "publish", "SHA256SUMS.txt"), $"{AppHash}  app.exe\n");
        }

        public ScriptResult RunSign(string? keyPath = null) =>
            Run("New-UpdateManifest.ps1", "-Version", ReleaseVersion, "-RepoRoot", Path, "-KeyPath", keyPath ?? KeyPath);

        public ScriptResult RunValidate() =>
            Run("Validate-ReleaseAssets.ps1", "-Version", ReleaseVersion, "-RepoRoot", Path);

        public void Dispose()
        {
            _key.Dispose();
            try { Directory.Delete(Path, recursive: true); } catch { }
        }

        private static ScriptResult Run(string script, params string[] args)
        {
            var startInfo = new ProcessStartInfo(SystemToolPathService.PowerShell)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.Environment.Remove("PSModulePath");
            startInfo.Environment.Remove("NVME_PATCHER_UPDATE_KEY");
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(script) }.Concat(args))
                startInfo.ArgumentList.Add(a);
            var result = TestProcessRunner.Run(startInfo, TimeSpan.FromSeconds(30));
            return new ScriptResult(result.ExitCode, result.StdOut, result.StdErr);
        }

        private static string ScriptPath(string script, [CallerFilePath] string sourceFile = "") =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourceFile)!, "..", "..", "scripts", script));

        private static void Write(string root, string relativePath, string content)
        {
            var path = System.IO.Path.Combine(root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
