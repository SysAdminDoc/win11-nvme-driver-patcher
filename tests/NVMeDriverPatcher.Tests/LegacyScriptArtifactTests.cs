using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Defects in what the legacy read/recover-only script writes, launches and promises:
/// the recovery kit .reg had no line break after its last entry (and, like the .bat, inherited
/// the script file's LF endings), the removal dialog promised a BitLocker suspension the script
/// no longer performs, four tool launches resolved bare names from an elevated process, and
/// fsutil's English-only BypassIO text made every non-English system read as unsupported.
/// </summary>
public sealed class LegacyScriptArtifactTests
{
    [Fact]
    public void RecoveryKitFiles_UseCrlfAndEndWithALineBreak()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMePatcher.LegacyKit.{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = RunLegacyFunction(dir, "Export-RecoveryKit -OutputDir $WorkDir | Out-Null");
            Assert.True(result.ExitCode == 0, result.StdOut + Environment.NewLine + result.StdErr);

            var kit = Path.Combine(dir, "NVMe_Recovery_Kit");
            var regBytes = File.ReadAllBytes(Path.Combine(kit, "NVMe_Remove_Patch.reg"));
            Assert.True(regBytes.Length > 2 && regBytes[0] == 0xFF && regBytes[1] == 0xFE, "the .reg must be UTF-16 LE with a BOM");
            var reg = Encoding.Unicode.GetString(regBytes, 2, regBytes.Length - 2);
            Assert.StartsWith("Windows Registry Editor Version 5.00\r\n", reg, StringComparison.Ordinal);
            AssertCrlfOnly(reg, ".reg");
            Assert.EndsWith("@=-\r\n", reg, StringComparison.Ordinal);

            var bat = File.ReadAllText(Path.Combine(kit, "Remove_NVMe_Patch.bat"), Encoding.ASCII);
            AssertCrlfOnly(bat, ".bat");
            Assert.EndsWith("\r\n", bat, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BypassIOParser_ReportsUnknownForOutputItCannotRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMePatcher.LegacyBypassIO.{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "supported.txt"),
                "BypassIO on \"C:\\\" is currently supported.\r\n    Storage Type:   NVMe\r\n    Storage Driver: BypassIO compatible\r\n");
            File.WriteAllText(Path.Combine(dir, "unsupported.txt"),
                "BypassIO on \"C:\\\" is not currently supported.\r\n    Storage Type:   NVMe\r\n    Storage Driver: Not BypassIO compatible\r\n    Driver Name:    nvmedisk.sys\r\n");
            File.WriteAllText(Path.Combine(dir, "german.txt"),
                "BypassIO auf \"C:\\\" wird derzeit nicht unterstuetzt.\r\n    Speichertyp:      NVMe\r\n    Speichertreiber:  Nicht BypassIO-kompatibel\r\n", Encoding.UTF8);

            var result = RunLegacyFunction(dir, """
                $out = [ordered]@{}
                foreach ($name in 'supported', 'unsupported', 'german') {
                    $text = [System.IO.File]::ReadAllText((Join-Path $WorkDir "$name.txt"))
                    $out[$name] = ConvertFrom-BypassIOState -Output $text
                }
                $out | ConvertTo-Json -Depth 3 -Compress
                """);
            Assert.True(result.ExitCode == 0, result.StdOut + Environment.NewLine + result.StdErr);

            using var json = JsonDocument.Parse(result.StdOut.Trim());
            var supported = json.RootElement.GetProperty("supported");
            Assert.True(supported.GetProperty("Supported").GetBoolean());
            Assert.Equal("NVMe", supported.GetProperty("StorageType").GetString());

            var unsupported = json.RootElement.GetProperty("unsupported");
            Assert.False(unsupported.GetProperty("Supported").GetBoolean());
            Assert.Equal("nvmedisk.sys", unsupported.GetProperty("BlockedBy").GetString());
            Assert.Contains("DirectStorage", unsupported.GetProperty("Warning").GetString(), StringComparison.Ordinal);

            // Non-English output is unknown, not "unsupported".
            var german = json.RootElement.GetProperty("german");
            Assert.Equal(JsonValueKind.Null, german.GetProperty("Supported").ValueKind);
            Assert.Contains("English", german.GetProperty("Warning").GetString(), StringComparison.Ordinal);
            Assert.Contains("wird derzeit", german.GetProperty("RawOutput").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BackgroundPreflightRunspace_CarriesEveryScriptFunctionItsFunctionsCall()
    {
        // The startup preflight runs in a separate runspace that only knows the functions named in
        // $funcNames. A helper missing from that list fails there and nowhere else.
        var result = RunPowerShellScript(Path.GetTempPath(), """
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
            $defs = @{}
            foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) { $defs[$f.Name] = $f }
            $assign = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$funcNames' }, $true) | Select-Object -First 1
            if (-not $assign) { 'NO-LIST'; exit 1 }
            $listed = @($assign.Right.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object { $_.Value })
            "LISTED $($listed.Count)"
            foreach ($name in $listed) {
                if (-not $defs.ContainsKey($name)) { "GAP $name is not defined"; continue }
                $calls = $defs[$name].Body.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) |
                    ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | Sort-Object -Unique
                foreach ($call in $calls) {
                    # Write-Log is supplied to the runspace as a separate collecting stub.
                    if ($defs.ContainsKey($call) -and $listed -notcontains $call -and $call -ne 'Write-Log') { "GAP $name calls $call" }
                }
            }
            """);
        Assert.True(result.ExitCode == 0, result.StdOut + Environment.NewLine + result.StdErr);
        Assert.Matches(@"LISTED \d{2,}", result.StdOut);
        Assert.DoesNotContain("GAP ", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshAndStartup_BothRunPreflightThroughTheBackgroundRunspace()
    {
        // Refresh used to run the DISM/CIM/fsutil probes inline on the dispatcher thread. Both entry
        // points must go through Start-BackgroundPreflight, and neither handler may probe directly.
        var result = RunPowerShellScript(Path.GetTempPath(), """
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
            $calls = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true)
            foreach ($pair in @(@('Add_Click', 'BtnRefresh'), @('Add_ContentRendered', 'window'))) {
                $handler = $calls | Where-Object { $_.Member.Value -eq $pair[0] -and $_.Expression.Extent.Text -match $pair[1] } | Select-Object -First 1
                if (-not $handler) { "MISSING $($pair[0]) handler for $($pair[1])"; continue }
                $text = $handler.Extent.Text
                if ($text -notmatch 'Start-BackgroundPreflight') { "SYNC $($pair[1]) handler does not call Start-BackgroundPreflight" }
                if ($text -match 'Invoke-PreflightChecks|Get-NVMeHealthData|Get-StorageDiskMigration') { "SYNC $($pair[1]) handler probes inline" }
            }
            """);
        Assert.True(result.ExitCode == 0, result.StdOut + Environment.NewLine + result.StdErr);
        Assert.DoesNotContain("MISSING ", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNC ", result.StdOut, StringComparison.Ordinal);
    }

    private const string Tool =
        @"(?<tool>powershell|pwsh|shutdown|explorer|fsutil|reg|regedit|cmd|notepad|bcdedit|pnputil|dism|manage-bde|wevtutil|sc|schtasks)(?:\.exe)?";

    // PowerShell launch shapes, in the script itself and in the scripts it generates.
    private static readonly Regex BarePowerShellLaunch = new(
        @"Start-Process\s+(?:-FilePath\s+)?['""]?" + Tool + @"['""]?(?=[\s)]|$)" +
        @"|&\s*['""]?" + Tool + @"['""]?(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A batch command line that starts with a bare tool name.
    private static readonly Regex BareBatchLaunch = new(
        @"^\s*@?" + Tool + @"\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void LegacyScript_LaunchesNoSystemToolByBareName()
    {
        // Self-check against each original shape, and stay quiet on the fixed ones.
        Assert.Matches(BarePowerShellLaunch, "        Start-Process powershell.exe -ArgumentList $argList -Verb RunAs");
        Assert.Matches(BarePowerShellLaunch, "        $output = & fsutil bypassio state $systemDrive 2>&1 | Out-String");
        Assert.Matches(BarePowerShellLaunch, "                Start-Process \"shutdown.exe\" -ArgumentList \"/r /t 30\"");
        Assert.Matches(BarePowerShellLaunch, "        Start-Process \"explorer.exe\" -ArgumentList $script:Config.WorkingDir");
        Assert.Matches(BareBatchLaunch, "reg query \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\WinPE\" >nul 2>&1");
        Assert.DoesNotMatch(BarePowerShellLaunch, "        Start-Process -FilePath $powerShellPath -ArgumentList $argList -Verb RunAs");
        Assert.DoesNotMatch(BarePowerShellLaunch, "        $output = & $fsutil bypassio state $systemDrive 2>&1 | Out-String");
        Assert.DoesNotMatch(BarePowerShellLaunch, "Start-Process $e.Uri.AbsoluteUri");
        Assert.DoesNotMatch(BareBatchLaunch, "\"%SystemRoot%\\System32\\reg.exe\" query \"HKLM\\SOFTWARE\" >nul 2>&1");
        Assert.DoesNotMatch(BareBatchLaunch, "echo   reg load HKLM\\OFFLINE_SYS C:\\Windows\\System32\\config\\SYSTEM");

        var lines = File.ReadAllLines(LegacyScriptPath());
        var offenders = new List<string>();
        var inBatch = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // The kit .bat body: an elevated recovery script, so its commands count too.
            if (line.Contains("$batContent = @\"", StringComparison.Ordinal)) { inBatch = true; continue; }
            if (inBatch && trimmed.StartsWith("\"@", StringComparison.Ordinal)) { inBatch = false; continue; }
            if (inBatch)
            {
                if (!trimmed.StartsWith("::", StringComparison.Ordinal) && BareBatchLaunch.IsMatch(line))
                    offenders.Add($"NVMe_Driver_Patcher.ps1:{i + 1} (kit .bat) {trimmed}");
                continue;
            }

            if (trimmed.StartsWith('#')) continue;
            if (BarePowerShellLaunch.IsMatch(line))
                offenders.Add($"NVMe_Driver_Patcher.ps1:{i + 1} {trimmed}");
        }

        Assert.True(offenders.Count == 0,
            "The legacy script runs elevated, so a bare tool name resolves through PATH or the current " +
            "directory with administrator rights. Use an absolute System32/Windows path:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void LegacyScript_PromisesNoBitLockerSuspensionItDoesNotPerform()
    {
        var promise = new Regex(@"BitLocker.{0,60}\b(?:will|would|is going to)\s+be\s+(?:automatically\s+)?suspended",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.Matches(promise,
            "[void]$warnings.Add(\"[!] BITLOCKER ACTIVE - Will be automatically suspended for one reboot to prevent recovery key prompt.\")");

        var source = File.ReadAllText(LegacyScriptPath());
        var suspends = Regex.IsMatch(source, @"^\s*[^#\r\n]*\bSuspend-BitLocker\b", RegexOptions.Multiline) ||
                       Regex.IsMatch(source, @"^\s*[^#\r\n]*manage-bde[^\r\n]*-protectors\s+-disable", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (suspends) return; // The promise would be true again.

        var lines = source.Split('\n');
        var offenders = lines
            .Select((line, index) => (line, number: index + 1))
            .Where(entry => promise.IsMatch(entry.line))
            .Select(entry => $"NVMe_Driver_Patcher.ps1:{entry.number} {entry.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "The script never suspends BitLocker, so no dialog may say it will:\n" + string.Join("\n", offenders));
    }

    private static void AssertCrlfOnly(string text, string label)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
            {
                var line = text.AsSpan(0, i).Count('\n') + 1;
                Assert.Fail($"{label} has a bare LF line ending at line {line}.");
            }
        }
    }

    // Loads the named legacy-script function(s) from the real file into a fresh Windows PowerShell
    // 5.1 process (the script itself is never run) and executes the body against them.
    private static BoundedProcessResult RunLegacyFunction(string workDir, string body) =>
        RunPowerShellScript(workDir, """
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
            function Write-Log { param([string]$Message, [string]$Level = 'INFO') }
            foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $n.Name -in 'Export-RecoveryKit', 'ConvertFrom-BypassIOState' }, $true)) {
                . ([ScriptBlock]::Create($f.Extent.Text))
            }
            """ + "\n" + body);

    private static BoundedProcessResult RunPowerShellScript(string workDir, string body)
    {
        var driver = Path.Combine(Path.GetTempPath(), $"NVMePatcher.LegacyDriver.{Guid.NewGuid():N}.ps1");
        File.WriteAllText(driver,
            "param([string]$ScriptPath, [string]$WorkDir)\n$ErrorActionPreference = 'Stop'\n" + body + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var startInfo = new ProcessStartInfo(SystemToolPathService.PowerShell)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.Environment.Remove("PSModulePath");
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", driver, "-ScriptPath", LegacyScriptPath(), "-WorkDir", workDir
                     })
                startInfo.ArgumentList.Add(argument);

            var result = TestProcessRunner.Run(startInfo, TimeSpan.FromSeconds(60));
            Assert.False(result.TimedOut, "Legacy script function test timed out.");
            return result;
        }
        finally
        {
            try { File.Delete(driver); } catch { }
        }
    }

    private static string LegacyScriptPath([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "NVMe_Driver_Patcher.ps1"));
}
