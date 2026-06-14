using Microsoft.Win32;
using NVMeDriverPatcher.Models;

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

        // .reg file — IDs/keys sourced from AppConfig so a future ID change can't leave the
        // recovery kit deleting stale values (which would defeat the recovery purpose).
        var regContent = BuildRegContent(controlSetNum, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        try
        {
            // .reg files require UTF-16 LE WITH BOM. File.WriteAllText with Encoding.Unicode emits the BOM.
            WriteAllTextAtomic(Path.Combine(stagingDir, "NVMe_Remove_Patch.reg"), regContent, System.Text.Encoding.Unicode);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not write NVMe_Remove_Patch.reg: {ex.Message}");
            DeleteDirectoryBestEffort(stagingDir);
            return null;
        }

        // .bat file — same AppConfig-sourced IDs/keys as the .reg, for both the offline (WinRE
        // ControlSet sweep) and in-Windows removal paths.
        var batContent = BuildBatContent();

        try
        {
            WriteAllTextAtomic(Path.Combine(stagingDir, "Remove_NVMe_Patch.bat"), batContent, System.Text.Encoding.ASCII);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not write Remove_NVMe_Patch.bat: {ex.Message}");
            DeleteDirectoryBestEffort(stagingDir);
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

WHAT THIS KIT CAN AND CANNOT UNDO:
- This kit undoes the REGISTRY OVERRIDE route: the FeatureManagement override
  values and the SafeBoot keys. Those live in the offline SYSTEM hive, so the
  .reg/.bat remove them whether you are in Windows OR in WinRE. On builds where
  the registry route is all that was used, this is a complete recovery.
- It CANNOT undo the native FeatureStore / ViVeTool FALLBACK route. Those feature
  flags live in the live feature-configuration store and can only be cleared by a
  running Windows kernel - there is no offline (WinRE) equivalent. If you used the
  'Try ViVeTool Fallback' button or 'featurestore --write-native', undo it from
  INSIDE Windows with either:
    * the app's Remove Patch button, or
    * NVMeDriverPatcher.Cli featurestore --reset-native
  (The app's Remove Patch already does this automatically.) If the machine will
  not boot, first remove the registry/SafeBoot patch with this kit so Windows
  boots on the legacy stack, THEN run the fallback reset from inside Windows.

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

    // Feature IDs the recovery kit removes = the patch's flag set + the optional Server key,
    // matching PatchService.Uninstall. Sourced from AppConfig so an ID change can't strand the
    // kit deleting the wrong values.
    private static IReadOnlyList<string> RecoveryFeatureIds() =>
        AppConfig.FeatureIDs.Append(AppConfig.ServerFeatureID).ToList();

    // The path under SYSTEM\<controlset>\ where the FeatureManagement overrides live, derived
    // from AppConfig.RegistrySubKey (the SSOT) rather than hardcoded.
    private static string OverridesRelativePath()
    {
        const string prefix = @"SYSTEM\CurrentControlSet\";
        return AppConfig.RegistrySubKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? AppConfig.RegistrySubKey[prefix.Length..]
            : @"Policies\Microsoft\FeatureManagement\Overrides";
    }

    internal static string BuildRegContent(string controlSetNum, string timestamp)
    {
        var ids = RecoveryFeatureIds();
        string overridesRel = OverridesRelativePath();
        string[] controlSets = ["CurrentControlSet", $"ControlSet{controlSetNum}"];

        var sb = new System.Text.StringBuilder();
        sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
        sb.Append("; NVMe Driver Patcher - RECOVERY KIT\r\n");
        sb.Append($"; Generated: {timestamp}\r\n;\r\n");
        sb.Append("; FROM WINDOWS: Double-click this file and confirm.\r\n");
        sb.Append("; FROM WinRE:   Run Remove_NVMe_Patch.bat so the offline SYSTEM hive is loaded.\r\n\r\n");

        foreach (var cs in controlSets)
        {
            sb.Append($@"[HKEY_LOCAL_MACHINE\SYSTEM\{cs}\{overridesRel}]").Append("\r\n");
            foreach (var id in ids) sb.Append($"\"{id}\"=-\r\n");
            sb.Append("\r\n");
        }

        foreach (var leaf in new[] { AppConfig.SafeBootGuid, AppConfig.SafeBootServiceName })
        {
            foreach (var cs in controlSets)
                foreach (var store in new[] { "Minimal", "Network" })
                    sb.Append($@"[-HKEY_LOCAL_MACHINE\SYSTEM\{cs}\Control\SafeBoot\{store}\{leaf}]").Append("\r\n");
            sb.Append("\r\n");
        }

        sb.Append("; NOTE: this .reg covers CurrentControlSet plus the control set that was current when the\r\n");
        sb.Append($"; kit was generated ({controlSetNum}). On systems with additional control sets (after failed\r\n");
        sb.Append("; boots), Remove_NVMe_Patch.bat is the canonical removal path — it sweeps ControlSet001-009.");
        return sb.ToString();
    }

    // reg-delete lines for one registry base (offline-hive control set, or the live %CS%).
    private static string BatDeletes(string regBase, string indent)
    {
        string overridesRel = OverridesRelativePath();
        var sb = new System.Text.StringBuilder();
        foreach (var id in RecoveryFeatureIds())
            sb.Append($"{indent}reg delete \"{regBase}\\{overridesRel}\" /v {id} /f 2>nul\r\n");
        foreach (var leaf in new[] { AppConfig.SafeBootGuid, AppConfig.SafeBootServiceName })
            foreach (var store in new[] { "Minimal", "Network" })
                sb.Append($"{indent}reg delete \"{regBase}\\Control\\SafeBoot\\{store}\\{leaf}\" /f 2>nul\r\n");
        return sb.ToString();
    }

    internal static string BuildBatContent()
    {
        string winreDeletes = BatDeletes(@"HKLM\OFFLINE_SYS\ControlSet00%%N", "    ");
        string windowsDeletes = BatDeletes(@"HKLM\SYSTEM\%CS%", "");

        var bat = @"@echo off
echo ============================================
echo  NVMe Driver Patcher - Recovery Kit
echo  Removes all native NVMe registry patches
echo ============================================
echo.

rem Detect WinRE/WinPE. HKLM\SYSTEM\CurrentControlSet exists in both full Windows
rem and the recovery environment, so it is not a reliable discriminator.
reg query ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WinPE"" >nul 2>&1
if %errorlevel% neq 0 (
    echo Detected: Running in Windows
    set ""CS=CurrentControlSet""
    goto :do_remove
)

echo Detected: Running in WinRE / Recovery Environment
echo Searching for Windows installation...
echo.

set ""WINFOUND=""
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist ""%%D:\Windows\System32\config\SYSTEM"" (
        if /I not ""%%D:""==""%SystemDrive%"" (
            echo Found Windows on %%D:
            set ""WINFOUND=%%D""
            goto :found_win
        )
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
" + winreDeletes + @")

reg unload HKLM\OFFLINE_SYS >nul 2>&1
goto :done

:do_remove
" + windowsDeletes + @"
:done
echo.
echo ============================================
echo  Patch removed. Reboot to restore defaults.
echo ============================================
echo.
pause";

        // Normalize to uniform CRLF — the verbatim boilerplate and the \r\n-built delete blocks
        // would otherwise risk mixed endings depending on how this source file is checked out.
        return bat.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    public static string? GenerateVerificationScript(string workingDir, bool includeServerKey)
    {
        // Compatibility overload for older callers. The historical script checked all core
        // feature flags, which corresponds to Full mode.
        return GenerateVerificationScript(workingDir, PatchProfile.Full, includeServerKey);
    }

    public static string? GenerateVerificationScript(string workingDir, PatchProfile profile, bool includeServerKey)
    {
        if (string.IsNullOrEmpty(workingDir))
            return null;
        try { Directory.CreateDirectory(workingDir); } catch { return null; }

        var outputPath = Path.Combine(workingDir, "Verify_NVMe_Patch.ps1");
        var expectedFeatureIds = AppConfig.GetFeatureIDsForProfile(profile).ToList();
        if (includeServerKey)
            expectedFeatureIds.Add(AppConfig.ServerFeatureID);

        var featureEntries = string.Join(
            ",\r\n",
            expectedFeatureIds.Select(id =>
            {
                var name = AppConfig.FeatureNames.TryGetValue(id, out var friendlyName)
                    ? friendlyName
                    : "Feature flag";
                return $@"    @{{ ID = ""{id}""; Name = ""{EscapePowerShellString(name)}"" }}";
            }));

        var script = $@"#Requires -RunAsAdministrator
$Host.UI.RawUI.WindowTitle = ""NVMe Patch Verification""
Write-Host ""NVMe Driver Patch Verification"" -ForegroundColor Cyan
Write-Host ""=================================="" -ForegroundColor Cyan
Write-Host """"
Write-Host ""Expected profile: {profile}"" -ForegroundColor Yellow
Write-Host """"
$registryPath = ""HKLM:\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides""
$featureIDs = @(
{featureEntries}
)
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

    private static string EscapePowerShellString(string value) =>
        value.Replace("`", "``").Replace("$", "`$").Replace("\"", "`\"");

    private static void WriteAllTextAtomic(string path, string content, System.Text.Encoding encoding)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, encoding))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
