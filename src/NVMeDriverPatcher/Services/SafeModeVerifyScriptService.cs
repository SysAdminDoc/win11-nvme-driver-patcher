using System.IO;
using System.Text;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// Generates a standalone PowerShell script the user can run FROM Safe Mode (after booting
// into it manually) to confirm the SafeBoot registry keys actually load nvmedisk.sys and
// that the system is recoverable. This is distinct from the normal post-reboot verification
// which runs in regular Windows — this one covers the "can I even survive Safe Mode?"
// question that users ask before unchecking the Skip-Warnings box and rebooting.
public static class SafeModeVerifyScriptService
{
    public static string Generate(AppConfig config)
    {
        var dir = string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir;
        var path = Path.Combine(dir, "Verify-NVMeSafeMode.ps1");
        var sb = new StringBuilder();
        sb.AppendLine("# Verify-NVMeSafeMode.ps1");
        sb.AppendLine("# Run FROM Safe Mode after applying the NVMe driver patch. Confirms the");
        sb.AppendLine("# SafeBoot keys actually bound nvmedisk.sys and the storage stack is live.");
        sb.AppendLine("#");
        sb.AppendLine($"# Generated {DateTime.UtcNow:u} by NVMe Driver Patcher v{AppConfig.AppVersion}");
        sb.AppendLine();
        sb.AppendLine("#Requires -RunAsAdministrator");
        sb.AppendLine("Set-StrictMode -Version Latest");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine();
        sb.AppendLine("function Test-SafeBootKey {");
        sb.AppendLine("    param([string]$Scope)");
        sb.AppendLine("    $key = \"HKLM:\\SYSTEM\\CurrentControlSet\\Control\\SafeBoot\\$Scope\\" + AppConfig.SafeBootGuid + "\"");
        sb.AppendLine("    if (-not (Test-Path $key)) { return \"[MISS] $Scope subkey absent\" }");
        sb.AppendLine("    $val = (Get-ItemProperty $key).'(default)'");
        sb.AppendLine("    if ($val -ne '" + AppConfig.SafeBootValue + "') { return \"[FAIL] $Scope default != 'Storage Disks' (got '$val')\" }");
        sb.AppendLine("    return \"[OK]   $Scope key present with correct default\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Write-Host '=== NVMe Safe Mode Verification ===' -ForegroundColor Cyan");
        sb.AppendLine("Write-Host (\"System in Safe Mode: \" + (Get-CimInstance Win32_ComputerSystem).BootupState)");
        sb.AppendLine();
        sb.AppendLine("Test-SafeBootKey -Scope 'Minimal' | Write-Host");
        sb.AppendLine("Test-SafeBootKey -Scope 'Network' | Write-Host");
        sb.AppendLine();
        sb.AppendLine("Write-Host 'Active storage drivers (Win32_SystemDriver):' -ForegroundColor Cyan");
        sb.AppendLine("Get-CimInstance Win32_SystemDriver |");
        sb.AppendLine("    Where-Object { $_.Name -match '^(stornvme|nvmedisk|storport|storahci|disk)$' -and $_.State -eq 'Running' } |");
        sb.AppendLine("    Select-Object Name, DisplayName, State, StartMode |");
        sb.AppendLine("    Format-Table -AutoSize");
        sb.AppendLine();
        sb.AppendLine("Write-Host 'NVMe physical disks visible in Safe Mode:' -ForegroundColor Cyan");
        sb.AppendLine("Get-PhysicalDisk | Where-Object { $_.BusType -eq 'NVMe' } |");
        sb.AppendLine("    Select-Object DeviceId, FriendlyName, HealthStatus, OperationalStatus, Size |");
        sb.AppendLine("    Format-Table -AutoSize");
        sb.AppendLine();
        sb.AppendLine("Write-Host 'If nvmedisk.sys is Running and physical disks are visible, Safe Mode is survivable.' -ForegroundColor Green");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
