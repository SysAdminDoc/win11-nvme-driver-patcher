using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ArtifactManifestScriptTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), $"NVMeDriverPatcher.ArtifactScript.Tests.{Guid.NewGuid():N}");

    public ArtifactManifestScriptTests() => Directory.CreateDirectory(_tempRoot);

    [Fact]
    public void Script_WritesServiceCompatibleManifestWithDeploymentRolesAndAtomicReplacement()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "NVMeDriverPatcher-5.0.0.msi"), "fake-msi");
        File.WriteAllText(Path.Combine(_tempRoot, "Detect-NVMeDriverPatcher.ps1"), "exit 0");

        var first = RunScript();
        Assert.True(first.ExitCode == 0, first.StdErr + Environment.NewLine + first.StdOut);
        var verification = GeneratedArtifactManifestService.VerifyDirectory(_tempRoot);
        Assert.True(verification.Success, first.StdErr + Environment.NewLine + verification.Summary);
        Assert.Equal("intune-source", verification.PayloadType);
        using (var document = JsonDocument.Parse(File.ReadAllText(
                   Path.Combine(_tempRoot, GeneratedArtifactManifestService.ManifestFileName))))
        {
            var roles = document.RootElement.GetProperty("files").EnumerateArray()
                .Select(file => file.GetProperty("role").GetString()).ToArray();
            Assert.Contains("installer", roles);
            Assert.Contains("detection-script", roles);
        }

        File.WriteAllText(Path.Combine(_tempRoot, "Install.ps1"), "exit 0");
        var second = RunScript();
        Assert.True(second.ExitCode == 0, second.StdErr + Environment.NewLine + second.StdOut);
        Assert.True(GeneratedArtifactManifestService.VerifyDirectory(_tempRoot).Success);
        Assert.Empty(Directory.GetFiles(_tempRoot, "ARTIFACT-MANIFEST.json.*.tmp"));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "ARTIFACT-MANIFEST.json.replace-backup")));
    }

    [Fact]
    public void ScriptManifest_RemainsVerifiableWhenPayloadIsZipped()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "NVMeDriverPatcher-5.0.0.msi"), "fake-msi");
        var script = RunScript();
        Assert.True(script.ExitCode == 0, script.StdErr + Environment.NewLine + script.StdOut);
        var zipPath = Path.Combine(Path.GetDirectoryName(_tempRoot)!, $"{Path.GetFileName(_tempRoot)}.zip");
        try
        {
            ZipFile.CreateFromDirectory(_tempRoot, zipPath);
            var verification = GeneratedArtifactManifestService.VerifyZip(zipPath);
            Assert.True(verification.Success, verification.Summary);
            Assert.Equal("intune-source", verification.PayloadType);
        }
        finally { try { File.Delete(zipPath); } catch { } }
    }

    private ScriptResult RunScript()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
            "-PayloadRoot", _tempRoot, "-PayloadType", "intune-source", "-ToolVersion", "5.0.0"
        })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return new(process.ExitCode, stdout, stderr);
    }

    private static string ScriptPath([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "scripts", "New-ArtifactManifest.ps1"));

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private sealed record ScriptResult(int ExitCode, string StdOut, string StdErr);
}
