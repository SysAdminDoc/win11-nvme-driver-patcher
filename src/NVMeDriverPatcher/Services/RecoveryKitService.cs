using Microsoft.Win32;

namespace NVMeDriverPatcher.Services;

public static class RecoveryKitService
{
    public static string? Export(string outputDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            log?.Invoke("[ERROR] Recovery kit export needs a destination folder.");
            return null;
        }

        string kitDir;
        string stagingDir;
        try
        {
            kitDir = Path.Combine(outputDir, "NVMe_Recovery_Kit");
            stagingDir = Path.Combine(outputDir, $"NVMe_Recovery_Kit.{Guid.NewGuid():N}.tmp");
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not create recovery kit folder: {ex.Message}");
            return null;
        }

        // Detect control set number
        string controlSetNum = "001";
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\Select");
            if (key?.GetValue("Current") is int current)
                controlSetNum = current.ToString("D3");
        }
        catch { }

        // .reg file
        var regContent = $@"Windows Registry Editor Version 5.00

; NVMe Driver Patcher - RECOVERY KIT
; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
;
; FROM WINDOWS: Double-click this file and confirm.
; FROM WinRE:   regedit /s NVMe_Remove_Patch.reg

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides]
""735209102""=-
""1853569164""=-
""156965516""=-
""1176759950""=-

[HKEY_LOCAL_MACHINE\SYSTEM\ControlSet{controlSetNum}\Policies\Microsoft\FeatureManagement\Overrides]
""735209102""=-
""1853569164""=-
""156965516""=-
""1176759950""=-

[-HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}}]
[-HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}}]
[-HKEY_LOCAL_MACHINE\SYSTEM\ControlSet{controlSetNum}\Control\SafeBoot\Minimal\{{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}}]
[-HKEY_LOCAL_MACHINE\SYSTEM\ControlSet{controlSetNum}\Control\SafeBoot\Network\{{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}}]";

        try
        {
            // .reg files require UTF-16 LE WITH BOM. File.WriteAllText with Encoding.Unicode emits the BOM.
            WriteAllTextAtomic(Path.Combine(stagingDir, "NVMe_Remove_Patch.reg"), regContent, System.Text.Encoding.Unicode);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not write NVMe_Remove_Patch.reg: {ex.Message}");
            return null;
        }

        // .bat file
        var batContent = @"@echo off
echo ============================================
echo  NVMe Driver Patcher - Recovery Kit
echo  Removes all native NVMe registry patches
echo ============================================
echo.

reg query ""HKLM\SYSTEM\CurrentControlSet"" >nul 2>&1
if %errorlevel%==0 (
    echo Detected: Running in Windows
    set ""CS=CurrentControlSet""
    goto :do_remove
)

echo Detected: Running in WinRE / Recovery Environment
echo Searching for Windows installation...
echo.

set ""WINFOUND=""
for %%D in (C D E F G H) do (
    if exist ""%%D:\Windows\System32\config\SYSTEM"" (
        echo Found Windows on %%D:
        set ""WINFOUND=%%D""
        goto :found_win
    )
)

echo ERROR: Could not find Windows installation.
pause
exit /b 1

:found_win
echo Loading offline registry hive...
reg load HKLM\OFFLINE_SYS ""%WINFOUND%:\Windows\System32\config\SYSTEM"" >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Failed to load registry hive.
    pause
    exit /b 1
)

rem Sweep ControlSet001..ControlSet009 -- covers boxes where the index has rolled past 003.
for /L %%N in (1,1,9) do (
    reg delete ""HKLM\OFFLINE_SYS\ControlSet00%%N\Policies\Microsoft\FeatureManagement\Overrides"" /v 735209102 /f 2>nul
    reg delete ""HKLM\OFFLINE_SYS\ControlSet00%%N\Policies\Microsoft\FeatureManagement\Overrides"" /v 1853569164 /f 2>nul
    reg delete ""HKLM\OFFLINE_SYS\ControlSet00%%N\Policies\Microsoft\FeatureManagement\Overrides"" /v 156965516 /f 2>nul
    reg delete ""HKLM\OFFLINE_SYS\ControlSet00%%N\Policies\Microsoft\FeatureManagement\Overrides"" /v 1176759950 /f 2>nul
    reg delete ""HKLM\OFFLINE_SYS\ControlSet00%%N\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"" /f 2>nul
    reg delete ""HKLM\OFFLINE_SYS\ControlSet00%%N\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"" /f 2>nul
)

reg unload HKLM\OFFLINE_SYS >nul 2>&1
goto :done

:do_remove
reg delete ""HKLM\SYSTEM\%CS%\Policies\Microsoft\FeatureManagement\Overrides"" /v 735209102 /f 2>nul
reg delete ""HKLM\SYSTEM\%CS%\Policies\Microsoft\FeatureManagement\Overrides"" /v 1853569164 /f 2>nul
reg delete ""HKLM\SYSTEM\%CS%\Policies\Microsoft\FeatureManagement\Overrides"" /v 156965516 /f 2>nul
reg delete ""HKLM\SYSTEM\%CS%\Policies\Microsoft\FeatureManagement\Overrides"" /v 1176759950 /f 2>nul
reg delete ""HKLM\SYSTEM\%CS%\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"" /f 2>nul
reg delete ""HKLM\SYSTEM\%CS%\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"" /f 2>nul

:done
echo.
echo ============================================
echo  Patch removed. Reboot to restore defaults.
echo ============================================
echo.
pause";

        try
        {
            WriteAllTextAtomic(Path.Combine(stagingDir, "Remove_NVMe_Patch.bat"), batContent, System.Text.Encoding.ASCII);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not write Remove_NVMe_Patch.bat: {ex.Message}");
            return null;
        }

        // README
        var readme = $@"NVMe Driver Patcher - Recovery Kit
===================================
Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

USE THIS KIT IF:
- Your system won't boot after enabling the native NVMe driver
- You need to undo the patch from WinRE (Windows Recovery Environment)

HOW TO USE FROM WINDOWS:
1. Double-click NVMe_Remove_Patch.reg and confirm
2. Restart your computer

HOW TO USE FROM WinRE (system won't boot):
1. Boot to Windows Recovery Environment
2. Go to: Troubleshoot > Advanced options > Command Prompt
3. Find this folder (try D:\, E:\, F:\)
4. Run: Remove_NVMe_Patch.bat
5. Type 'exit' and restart

FILES:
- NVMe_Remove_Patch.reg    - Registry file
- Remove_NVMe_Patch.bat    - Smart batch script (auto-detects Windows vs WinRE)
- README.txt               - This file";

        try
        {
            WriteAllTextAtomic(Path.Combine(stagingDir, "README.txt"), readme, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not write README.txt: {ex.Message}");
            // README is informational; don't bail out the whole kit just for this.
        }

        string? previousKitBackupDir = null;
        try
        {
            if (Directory.Exists(kitDir))
            {
                previousKitBackupDir = Path.Combine(outputDir, $"NVMe_Recovery_Kit.{Guid.NewGuid():N}.bak");
                Directory.Move(kitDir, previousKitBackupDir);
            }

            Directory.Move(stagingDir, kitDir);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not finalize recovery kit folder: {ex.Message}");
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
            try
            {
                if (!string.IsNullOrWhiteSpace(previousKitBackupDir) &&
                    Directory.Exists(previousKitBackupDir) &&
                    !Directory.Exists(kitDir))
                {
                    Directory.Move(previousKitBackupDir, kitDir);
                }
            }
            catch { }
            return null;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(previousKitBackupDir) && Directory.Exists(previousKitBackupDir))
                Directory.Delete(previousKitBackupDir, recursive: true);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[WARNING] New recovery kit is ready, but the previous backup copy could not be cleaned up: {ex.Message}");
        }

        log?.Invoke($"Recovery kit saved to: {kitDir}");
        return kitDir;
    }

    public static string? GenerateVerificationScript(string workingDir, bool includeServerKey)
    {
        if (string.IsNullOrEmpty(workingDir))
            return null;
        try { Directory.CreateDirectory(workingDir); } catch { return null; }

        var outputPath = Path.Combine(workingDir, "Verify_NVMe_Patch.ps1");
        var serverKeyValue = includeServerKey ? "$true" : "$false";

        var script = $@"#Requires -RunAsAdministrator
$Host.UI.RawUI.WindowTitle = ""NVMe Patch Verification""
Write-Host ""NVMe Driver Patch Verification"" -ForegroundColor Cyan
Write-Host ""=================================="" -ForegroundColor Cyan
Write-Host """"
$registryPath = ""HKLM:\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides""
$featureIDs = @(
    @{{ ID = ""735209102""; Name = ""NativeNVMeStackForGeClient"" }},
    @{{ ID = ""1853569164""; Name = ""UxAccOptimization"" }},
    @{{ ID = ""156965516""; Name = ""Standalone_Future"" }}
)
if ({serverKeyValue}) {{ $featureIDs += @{{ ID = ""1176759950""; Name = ""Server 2025 key"" }} }}
$passCount = 0; $totalChecks = $featureIDs.Count + 2
foreach ($feat in $featureIDs) {{
    $val = Get-ItemProperty -Path $registryPath -Name $feat.ID -ErrorAction SilentlyContinue
    if ($val -and $val.($feat.ID) -eq 1) {{ Write-Host ""  [PASS] $($feat.ID)"" -ForegroundColor Green; $passCount++ }}
    else {{ Write-Host ""  [FAIL] $($feat.ID)"" -ForegroundColor Red }}
}}
$sbMin = ""HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}}""
$sbNet = ""HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}}""
if (Test-Path -LiteralPath $sbMin) {{ Write-Host ""  [PASS] SafeBoot Minimal"" -ForegroundColor Green; $passCount++ }}
else {{ Write-Host ""  [FAIL] SafeBoot Minimal"" -ForegroundColor Red }}
if (Test-Path -LiteralPath $sbNet) {{ Write-Host ""  [PASS] SafeBoot Network"" -ForegroundColor Green; $passCount++ }}
else {{ Write-Host ""  [FAIL] SafeBoot Network"" -ForegroundColor Red }}
Write-Host """"; Write-Host ""Result: $passCount/$totalChecks"" -ForegroundColor $(if ($passCount -eq $totalChecks) {{ 'Green' }} else {{ 'Yellow' }})
Write-Host """"; Write-Host ""Press any key...""; $null = $Host.UI.RawUI.ReadKey(""NoEcho,IncludeKeyDown"")";

        try
        {
            // Write with UTF-8 BOM so PowerShell 5.1 (powershell.exe) parses any non-ASCII content
            // correctly without resorting to its legacy ANSI fallback.
            WriteAllTextAtomic(outputPath, script, new System.Text.UTF8Encoding(true));
            return outputPath;
        }
        catch { return null; }
    }

    private static void WriteAllTextAtomic(string path, string content, System.Text.Encoding encoding)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, content, encoding);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }
}
