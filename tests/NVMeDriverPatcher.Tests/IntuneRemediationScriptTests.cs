using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Intune's proactive remediation contract is a pair: a check that exits 1 when the device needs
/// fixing and a remediation that fixes it. The pair drives the CLI, so these tests run both scripts
/// against a fake CLI and pin what the CLI's status JSON tells them about the build policy.
/// </summary>
public sealed class IntuneRemediationScriptTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), $"NVMeDriverPatcher.Intune.Tests.{Guid.NewGuid():N}");

    public IntuneRemediationScriptTests() => Directory.CreateDirectory(_tempRoot);

    [Theory]
    [InlineData("""{"applied":true,"status":"applied","nativeActive":false,"applyAllowed":true}""", 0, "Compliant")]
    [InlineData("""{"applied":false,"status":"not-applied","nativeActive":true,"applyAllowed":false}""", 0, "Compliant")]
    [InlineData("""{"applied":false,"status":"not-applied","nativeActive":false,"applyAllowed":false,"applyBlockedReason":"Build rule 24h2-client-unverified"}""", 0, "Not applicable on this Windows build: Build rule 24h2-client-unverified")]
    [InlineData("""{"applied":false,"status":"not-applied","nativeActive":false,"applyAllowed":true,"componentsApplied":0,"componentsTotal":3}""", 1, "Not compliant: patch is not-applied (0 of 3 components)")]
    [InlineData("""{"applied":false,"status":"partial","nativeActive":false,"applyAllowed":true,"componentsApplied":2,"componentsTotal":3}""", 1, "Not compliant: patch is partial")]
    public void Check_ExitsOneOnlyWhenTheBuildAllowsApplyAndThePatchIsMissing(string data, int expectedExit, string expectedText)
    {
        File.WriteAllText(Path.Combine(_tempRoot, "status.json"), $$"""{"schemaVersion":1,"command":"status","data":{{data}}}""");
        var cli = WriteFakeCli("@type \"%~dp0status.json\"\r\n@exit /b 1\r\n");

        var result = RunScript("Check-NVMeDriverPatcher.ps1", cli);

        Assert.True(result.ExitCode == expectedExit, result.StdOut + result.StdErr);
        Assert.Contains(expectedText, result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ReportsAMissingOrUnreadableCli()
    {
        var missing = RunScript("Check-NVMeDriverPatcher.ps1", Path.Combine(_tempRoot, "absent.cmd"));
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("CLI not found", missing.StdOut, StringComparison.Ordinal);

        var garbled = RunScript("Check-NVMeDriverPatcher.ps1", WriteFakeCli("@echo Access is denied.\r\n@exit /b 3\r\n"));
        Assert.Equal(1, garbled.ExitCode);
        Assert.Contains("Couldn't read status from the CLI: Access is denied.", garbled.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FindsTheEnvelopeAfterALogLineCarryingABracedGuid()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "status.json"),
            """{"schemaVersion":1,"command":"status","data":{"applied":true,"status":"applied","nativeActive":false,"applyAllowed":true}}""");
        var cli = WriteFakeCli(
            "@echo Restored HKLM\\SYSTEM\\CurrentControlSet\\Control\\SafeBoot\\Minimal\\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}\r\n" +
            "@type \"%~dp0status.json\"\r\n@exit /b 0\r\n");

        var result = RunScript("Check-NVMeDriverPatcher.ps1", cli);

        Assert.True(result.ExitCode == 0, result.StdOut + result.StdErr);
        Assert.Contains("Compliant", result.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 0, new[] { "recovery-kit", "apply --unattended --no-restart" })]
    [InlineData(0, 1, 1, new[] { "recovery-kit", "apply --unattended --no-restart" })]
    [InlineData(1, 0, 1, new[] { "recovery-kit" })]
    public void Remediate_RefreshesTheKitThenAppliesWithoutRestart(int kitExit, int applyExit, int expectedExit, string[] expectedCalls)
    {
        // apply refuses without a recovery kit from the last 30 days, so the kit comes first and a
        // failed kit means apply never runs.
        var cli = WriteFakeCli(
            "@echo %*>> \"%~dp0args.txt\"\r\n" +
            $"@if \"%1\"==\"recovery-kit\" (echo Kit written.& exit /b {kitExit})\r\n" +
            $"@echo Apply finished.\r\n@exit /b {applyExit}\r\n");

        var result = RunScript("Remediate-NVMeDriverPatcher.ps1", cli);

        Assert.True(result.ExitCode == expectedExit, result.StdOut + result.StdErr);
        Assert.Equal(expectedCalls, File.ReadAllLines(Path.Combine(_tempRoot, "args.txt")).Select(line => line.Trim()));
        Assert.Equal(kitExit == 0, result.StdOut.Contains("Recovery kit refreshed.", StringComparison.Ordinal));
        Assert.Equal(kitExit != 0, result.StdOut.Contains("apply wasn't attempted", StringComparison.Ordinal));
        Assert.Equal(expectedExit == 0, result.StdOut.Contains("loads after the next restart", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Check-NVMeDriverPatcher.ps1")]
    [InlineData("Remediate-NVMeDriverPatcher.ps1")]
    public void BothScripts_FailWhenTheCliCannotStart(string script)
    {
        // Not a valid program, so CreateProcess refuses it. Nothing runs and no window opens.
        var cli = Path.Combine(_tempRoot, "broken.exe");
        File.WriteAllText(cli, "not a program");

        var result = RunScript(script, cli);

        Assert.True(result.ExitCode == 1, result.StdOut + result.StdErr);
        Assert.Contains("Couldn't run the CLI", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Remediate_NeverOverridesTheCliSafetyRefusals()
    {
        var script = File.ReadAllText(ScriptPath("Remediate-NVMeDriverPatcher.ps1"));
        // The comment-based help names the flags it avoids; the code after it must not pass them.
        var afterHelp = script[(script.IndexOf("#>", StringComparison.Ordinal) + 2)..];
        var code = afterHelp.Split('\n').Where(line => !line.TrimStart().StartsWith('#'));
        Assert.DoesNotContain(code, line => line.Contains("--force", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_GivesThePairTheirOwnRoles()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "NVMeDriverPatcher-5.0.0.msi"), "fake-msi");
        foreach (var name in new[] { "Detect-NVMeDriverPatcher.ps1", "Check-NVMeDriverPatcher.ps1", "Remediate-NVMeDriverPatcher.ps1" })
            File.Copy(ScriptPath(name), Path.Combine(_tempRoot, name));

        var startInfo = PowerShell(RepoPath("scripts", "New-ArtifactManifest.ps1"),
            "-PayloadRoot", _tempRoot, "-PayloadType", "intune-source", "-ToolVersion", "5.0.0");
        var result = TestProcessRunner.Run(startInfo, TimeSpan.FromSeconds(30));
        Assert.True(result.ExitCode == 0, result.StdErr + result.StdOut);

        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_tempRoot, GeneratedArtifactManifestService.ManifestFileName)));
        var roles = document.RootElement.GetProperty("files").EnumerateArray()
            .ToDictionary(file => file.GetProperty("relativePath").GetString()!, file => file.GetProperty("role").GetString());
        Assert.Equal("detection-script", roles["Detect-NVMeDriverPatcher.ps1"]);
        Assert.Equal("remediation-detection-script", roles["Check-NVMeDriverPatcher.ps1"]);
        Assert.Equal("remediation-script", roles["Remediate-NVMeDriverPatcher.ps1"]);
    }

    [Fact]
    public void StatusJson_CarriesTheBuildPolicyVerdict()
    {
        var status = new PatchStatus();
        var blocked = new BuildActionPolicy(PatchActionDisposition.VerifyRollbackOnly, "No known path", "rule-x", "none-known", true, false);
        var allowed = new BuildActionPolicy(PatchActionDisposition.Allowed, "Fine", "rule-y", "registry-override", true, false);

        using var blockedJson = JsonDocument.Parse(CliJson.Serialize("status", CliJson.BuildStatus(status, null, EnablementSource.None, null, null, blocked)));
        var blockedData = blockedJson.RootElement.GetProperty("data");
        Assert.False(blockedData.GetProperty("applyAllowed").GetBoolean());
        Assert.Equal("No known path", blockedData.GetProperty("applyBlockedReason").GetString());

        using var allowedJson = JsonDocument.Parse(CliJson.Serialize("status", CliJson.BuildStatus(status, null, EnablementSource.None, null, null, allowed)));
        var allowedData = allowedJson.RootElement.GetProperty("data");
        Assert.True(allowedData.GetProperty("applyAllowed").GetBoolean());
        Assert.False(allowedData.TryGetProperty("applyBlockedReason", out _));
    }

    private string WriteFakeCli(string body)
    {
        var path = Path.Combine(_tempRoot, $"cli-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, body);
        return path;
    }

    private static BoundedProcessResult RunScript(string script, string cliPath) =>
        TestProcessRunner.Run(PowerShell(ScriptPath(script), "-CliPath", cliPath), TimeSpan.FromSeconds(30));

    private static ProcessStartInfo PowerShell(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(SystemToolPathService.PowerShell)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static string ScriptPath(string name) => RepoPath("packaging", "intune", name);

    private static string RepoPath(string first, string second, string? third = null, [CallerFilePath] string sourceFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return third is null ? Path.Combine(root, first, second) : Path.Combine(root, first, second, third);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }
}
