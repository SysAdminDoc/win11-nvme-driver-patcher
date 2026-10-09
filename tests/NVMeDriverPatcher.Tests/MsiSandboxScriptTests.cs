using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NVMeDriverPatcher.Tests;

// Pins the lifecycle Test-MsiSandbox.ps1 walks in the guest. Running it needs Windows Sandbox on
// an x64 Windows 11 host, so the suite checks the script's contract rather than the run.
public sealed class MsiSandboxScriptTests
{
    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    private static string Script() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "Test-MsiSandbox.ps1"));

    [Fact]
    public void Script_FailsFastWithoutWindowsSandbox_AndUsesTheSystem32Copy()
    {
        var script = Script();
        Assert.Contains("[Environment+SpecialFolder]::System", script, StringComparison.Ordinal);
        Assert.Contains("'WindowsSandbox.exe'", script, StringComparison.Ordinal);
        Assert.Contains("Windows Sandbox is unavailable", script, StringComparison.Ordinal);
        Assert.Contains("Is64BitOperatingSystem", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"Get-Command", script);
    }

    [Fact]
    public void Wsb_MapsTheMsiReadOnly_AndRunsTheBootstrapByAbsolutePath()
    {
        var script = Script();
        var input = Regex.Match(script,
            @"<HostFolder>\$escapedInput</HostFolder>\s*<SandboxFolder>C:\\NVMeMsiInput</SandboxFolder>\s*<ReadOnly>true</ReadOnly>");
        Assert.True(input.Success, "the MSI folder must be mapped read-only");
        Assert.Contains(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\NVMeMsiSmoke\bootstrap.ps1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Guest_WalksInstallCheckUninstallAndResidueInOrder()
    {
        var script = Script();
        var steps = new[]
        {
            "Invoke-Step 'install' $msiexec @('/i'",
            "'/qn'",
            "ADDLOCAL=ALL",
            "NVMeDriverPatcher.Watchdog.exe",           // Program Files content
            "Get-Service -Name $serviceName",           // service registered
            "Invoke-Step 'cli-version' $cliExe @('version')",
            "Invoke-Step 'register-tasks'",
            "task-present",                             // scheduled tasks exist
            "Invoke-Step 'unregister-tasks'",
            "Invoke-Step 'uninstall' $msiexec @('/x'",
            "residue-check",                            // nothing left
        };
        var cursor = 0;
        foreach (var step in steps)
        {
            var at = script.IndexOf(step, cursor, StringComparison.Ordinal);
            Assert.True(at >= 0, $"missing or out of order: {step}");
            cursor = at;
        }

        Assert.Contains(@"SysAdminDoc\NVMePatcher\BootVerify", script, StringComparison.Ordinal);
        Assert.Contains(@"SysAdminDoc\NVMePatcher\WatchdogSweep", script, StringComparison.Ordinal);
        Assert.Contains("NVMeDriverPatcherWatchdog", script, StringComparison.Ordinal);
        Assert.Contains("& $shutdownExe /s /t 0 /f", script, StringComparison.Ordinal); // guest always powers off
    }

    [Fact]
    public void Guest_ResolvesEveryToolByAbsolutePath()
    {
        var script = Script();
        foreach (var tool in new[] { "msiexec.exe", "schtasks.exe", "shutdown.exe" })
            Assert.Contains($"Join-Path $sys32 '{tool}'", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"&\s*(msiexec|schtasks|shutdown|sc)(\.exe)?\b", script);
    }
}
