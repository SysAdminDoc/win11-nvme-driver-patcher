using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NVMeDriverPatcher.Tests;

public sealed class LegacyPowerShellBoundaryTests
{
    [Fact]
    public void LiveArtifact_PassesReadRecoverOnlyReleaseGate()
    {
        var result = RunPowerShell("-File", ValidatorPath(), "-ScriptPath", LegacyScriptPath());

        Assert.True(result.ExitCode == 0, result.StdOut + Environment.NewLine + result.StdErr);
        Assert.Contains("boundary check passed", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_ExitsFiveBeforeElevationWithMaintainedHandoff()
    {
        var result = RunPowerShell(
            "-File", LegacyScriptPath(), "-Silent", "-Apply", "-Force");

        Assert.Equal(5, result.ExitCode);
        var output = result.StdOut + result.StdErr;
        Assert.Contains("MUTATION RETIRED", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NVMeDriverPatcher.exe", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NVMeDriverPatcher.Cli.exe apply --safe", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseGate_RejectsAnyRestoredInstallFunctionOrRegistryWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMePatcher.LegacyBoundary.{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "unsafe.ps1");
        try
        {
            File.WriteAllText(script, """
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
                function Install-NVMePatch {
                    Set-ItemProperty -Path HKLM:\unsafe -Name enabled -Value 1
                }
                function Uninstall-NVMePatch { return $true }
                function Export-SystemDiagnostics { return $null }
                function New-VerificationScript { return $null }
                function Export-RecoveryKit { return $null }
                """);

            var result = RunPowerShell("-File", ValidatorPath(), "-ScriptPath", script);

            Assert.NotEqual(0, result.ExitCode);
            var output = result.StdOut + result.StdErr;
            Assert.Contains("Install-NVMePatch", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Set-ItemProperty", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static ScriptResult RunPowerShell(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.Environment.Remove("PSModulePath");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15_000), "PowerShell boundary test timed out.");
        return new ScriptResult(process.ExitCode, stdOut, stdErr);
    }

    private static string ValidatorPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(RepoRoot(sourceFile), "scripts", "Validate-LegacyPowerShellBoundary.ps1");

    private static string LegacyScriptPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(RepoRoot(sourceFile), "NVMe_Driver_Patcher.ps1");

    private static string RepoRoot(string sourceFile) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    private sealed record ScriptResult(int ExitCode, string StdOut, string StdErr);
}
