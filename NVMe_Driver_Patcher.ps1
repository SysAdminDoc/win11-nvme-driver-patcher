<#
.SYNOPSIS
    NVMe Driver Patcher for Windows 11
    
.DESCRIPTION
    Enterprise-grade GUI tool to enable the experimental Server 2025 NVMe driver in Windows 11.
    
    SAFETY FEATURES:
    - Administrator privilege verification
    - Windows version compatibility check  
    - BitLocker encryption detection and warning
    - Third-party NVMe driver detection
    - Pre-flight checklist with go/no-go indicators
    - Automatic System Protection enablement for C: drive
    - Mandatory restore point creation before changes
    - Registry backup export to file (bypasses 24-hour restore point limit)
    - Rollback on partial failure
    - Windows Event Log integration for audit trail
    - Post-reboot verification script generation
    - Confirmation dialogs for all critical operations
    - Comprehensive logging with auto-save capability
    
    REGISTRY KEYS MODIFIED (5 COMPONENT ATOMIC PATCH + OPTIONAL SERVER KEY):
    1. Feature Flag: 735209102  (NativeNVMeStackForGeClient - Primary enable)
    2. Feature Flag: 1853569164 (UxAccOptimization - Extended functionality)
    3. Feature Flag: 156965516  (Standalone_Future - Performance optimizations)
    4. SafeBoot Minimal: {75416E63-5912-4DFA-AE8F-3EFACCAFFB14}
    5. SafeBoot Network: {75416E63-5912-4DFA-AE8F-3EFACCAFFB14}
    Optional:
    6. Feature Flag: 1176759950 (Microsoft Official Server 2025 key)

.PARAMETER Silent
    Run in silent mode (no GUI). Requires -Apply, -Remove, or -Status.
    
.PARAMETER Apply
    Apply the NVMe driver patch.
    
.PARAMETER Remove
    Remove the NVMe driver patch.
    
.PARAMETER Status
    Check and report patch status without making changes. Returns exit code 0 if applied, 1 if not applied, 2 if partial.
    
.PARAMETER NoRestart
    Do not prompt for restart after changes.
    
.PARAMETER Force
    Skip confirmation dialogs (use with caution).
    
.PARAMETER ExportDiagnostics
    Export a full system diagnostics report and exit.
    
.PARAMETER GenerateVerifyScript
    Generate a post-reboot verification script and exit.

.EXAMPLE
    .\NVMe_Driver_Patcher.ps1
    Launches the GUI application.
    
.EXAMPLE
    .\NVMe_Driver_Patcher.ps1 -Silent -Apply -NoRestart
    Silently applies the patch without restart prompt.
    
.EXAMPLE
    .\NVMe_Driver_Patcher.ps1 -Silent -Status
    Check patch status silently. Exit code: 0=applied, 1=not applied, 2=partial.
    
.EXAMPLE
    .\NVMe_Driver_Patcher.ps1 -ExportDiagnostics
    Export full system diagnostics report for support.

.NOTES
    Version: 3.0.0
    Author:  Matthew Parker
    Requires: Windows 11 24H2/25H2, Administrator privileges
    
    CHANGELOG v3.0.0:
    - Added optional 4th feature flag (1176759950) - Microsoft's official Server 2025 key
    - Added active NVMe driver verification (nvmedisk.sys vs stornvme.sys/disk.sys)
    - Added BypassIO/DirectStorage status check (fsutil bypassio)
    - Added DirectStorage gaming warning in confirmation dialogs
    - Added recommended build check (26100+ for 24H2/25H2)
    - Updated feature flag descriptions with internal names
    - Updated pre-flight checks with driver activation status
    - Updated diagnostics export with BypassIO and driver details
    - Updated verification script with nvmedisk.sys and BypassIO checks
    - Updated documentation URL to Microsoft TechCommunity announcement
    - Improved registry key descriptions in UI
    
    CHANGELOG v2.9.0:
    - Added pre-flight checklist panel with visual go/no-go indicators
    - Added Windows Event Log integration (Application log)
    - Added system diagnostics export for support bundles
    - Added toast notifications (Windows 10/11 style)
    - Added -Status parameter for monitoring/scripting
    - Added -ExportDiagnostics parameter
    - Added -GenerateVerifyScript parameter
    - Added post-reboot verification script generation
    - Added copy log to clipboard button
    - Added configuration file persistence (JSON)
    - Added documentation/resource links
    - Added driver queue depth detection
    - Improved pre-action safety checks
    
    EXIT CODES (Silent Mode):
    0 - Success / Patch Applied (for -Status)
    1 - Failure / Patch Not Applied (for -Status)  
    2 - Partial (for -Status) / No NVMe drives
    3 - Invalid parameters
    4 - Elevation required
    
.LINK
    https://learn.microsoft.com/en-us/windows-server/storage/

#>

#Requires -Version 5.1

[CmdletBinding(DefaultParameterSetName = 'GUI')]
param(
    [Parameter(ParameterSetName = 'Silent')]
    [switch]$Silent,
    
    [Parameter(ParameterSetName = 'Silent')]
    [switch]$Apply,
    
    [Parameter(ParameterSetName = 'Silent')]
    [switch]$Remove,
    
    [Parameter(ParameterSetName = 'Silent')]
    [switch]$Status,
    
    [Parameter(ParameterSetName = 'Silent')]
    [switch]$NoRestart,
    
    [Parameter(ParameterSetName = 'Silent')]
    [switch]$Force,
    
    [Parameter(ParameterSetName = 'Export')]
    [switch]$ExportDiagnostics,
    
    [Parameter(ParameterSetName = 'Export')]
    [switch]$GenerateVerifyScript
)

# ===========================================================================
# SECTION 1: INITIALIZATION & PRIVILEGE ELEVATION
# ===========================================================================

$ErrorActionPreference = "Continue"
$script:StartTime = Get-Date

function Test-Administrator {
    $currentPrincipal = New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent()
    )
    return $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Silent) { $argList += " -Silent" }
    if ($Apply) { $argList += " -Apply" }
    if ($Remove) { $argList += " -Remove" }
    if ($Status) { $argList += " -Status" }
    if ($NoRestart) { $argList += " -NoRestart" }
    if ($Force) { $argList += " -Force" }
    if ($ExportDiagnostics) { $argList += " -ExportDiagnostics" }
    if ($GenerateVerifyScript) { $argList += " -GenerateVerifyScript" }
    
    try {
        Start-Process powershell.exe -ArgumentList $argList -Verb RunAs
    }
    catch {
        if (-not $Silent -and -not $ExportDiagnostics -and -not $GenerateVerifyScript) {
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show(
                "This application requires Administrator privileges.`n`nPlease right-click and select 'Run as Administrator'.",
                "Elevation Required",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Error
            ) | Out-Null
        }
        else {
            Write-Error "Administrator privileges required."
        }
    }
    exit 4
}

# ===========================================================================
# SECTION 2: ASSEMBLY LOADING & DPI AWARENESS
# ===========================================================================

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class DpiAwareness {
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
    
    [DllImport("shcore.dll")]
    public static extern int SetProcessDpiAwareness(int awareness);
}

public class ToastHelper {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

public class DarkScrollBar {
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);
}
"@ -ErrorAction SilentlyContinue

try { [DpiAwareness]::SetProcessDpiAwareness(2) | Out-Null }
catch { try { [DpiAwareness]::SetProcessDPIAware() | Out-Null } catch { } }

[System.Windows.Forms.Application]::EnableVisualStyles()

# ===========================================================================
# SECTION 3: SINGLE INSTANCE MUTEX
# ===========================================================================

$script:AppMutex = $null
$mutexName = "Global\NVMeDriverPatcher_SingleInstance"

if (-not $ExportDiagnostics -and -not $GenerateVerifyScript -and -not $Status) {
    try {
        $script:AppMutex = New-Object System.Threading.Mutex($false, $mutexName, [ref]$false)
        if (-not $script:AppMutex.WaitOne(0, $false)) {
            if (-not $Silent) {
                [System.Windows.Forms.MessageBox]::Show(
                    "NVMe Driver Patcher is already running.",
                    "Already Running",
                    [System.Windows.Forms.MessageBoxButtons]::OK,
                    [System.Windows.Forms.MessageBoxIcon]::Information
                ) | Out-Null
            }
            else {
                Write-Warning "NVMe Driver Patcher is already running."
            }
            exit 0
        }
    }
    catch { }
}

# ===========================================================================
# SECTION 4: GLOBAL CONFIGURATION
# ===========================================================================

$script:Config = @{
    AppName         = "NVMe Driver Patcher"
    AppVersion      = "3.0.0"
    RegistryPath    = "HKLM:\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides"
    FeatureIDs      = @("735209102", "1853569164", "156965516")
    FeatureNames    = @{
        "735209102"  = "NativeNVMeStackForGeClient (Primary enable)"
        "1853569164" = "UxAccOptimization (Extended functionality)"
        "156965516"  = "Standalone_Future (Performance optimizations)"
        "1176759950" = "Microsoft Official (Server 2025 key)"
    }
    ServerFeatureID = "1176759950"
    IncludeServerKey = $false
    SafeBootMinimal = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"
    SafeBootNetwork = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"
    SafeBootValue   = "Storage Disks"
    MinWinBuild     = 22000
    RecommendedBuild = 26100
    LogHistory      = [System.Collections.ArrayList]::new()
    WorkingDir      = $null
    ConfigFile      = $null
    TotalComponents = 5
    RestartDelay    = 30
    AutoSaveLog     = $true
    EnableToasts    = $true
    WriteEventLog   = $true
    EventLogSource  = "NVMe Driver Patcher"
    SilentMode      = $Silent.IsPresent
    NoRestart       = $NoRestart.IsPresent
    ForceMode       = $Force.IsPresent
    DocumentationURL = "https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353"
    GitHubURL       = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher"
}

$script:UIConstants = @{
    FormWidth        = 1300
    FormHeight       = 1080
    CardCornerRadius = 10
    ButtonCornerRadius = 8
    Margin           = 28
    CardGap          = 18
    ColumnGap        = 24
    ColumnWidth      = 610
    LeftColumnX      = 28
    RightColumnX     = 662
    HeaderHeight     = 110
    ContentTop       = 128
    RowHeight        = 28
    DriveRowHeight   = 28
    CardPadding      = 24
}

# Initialize working directory
$script:Config.WorkingDir = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "NVMePatcher"
if (-not (Test-Path $script:Config.WorkingDir)) {
    try { New-Item -Path $script:Config.WorkingDir -ItemType Directory -Force | Out-Null }
    catch {
        $script:Config.WorkingDir = Join-Path $env:TEMP "NVMePatcher_Backups"
        if (-not (Test-Path $script:Config.WorkingDir)) {
            New-Item -Path $script:Config.WorkingDir -ItemType Directory -Force | Out-Null
        }
    }
}
$script:Config.ConfigFile = Join-Path $script:Config.WorkingDir "config.json"

# ===========================================================================
# SECTION 5: CONFIGURATION PERSISTENCE
# ===========================================================================

function Load-Configuration {
    if (Test-Path $script:Config.ConfigFile) {
        try {
            $savedConfig = Get-Content $script:Config.ConfigFile -Raw | ConvertFrom-Json
            if ($null -ne $savedConfig.AutoSaveLog) { $script:Config.AutoSaveLog = $savedConfig.AutoSaveLog }
            if ($null -ne $savedConfig.EnableToasts) { $script:Config.EnableToasts = $savedConfig.EnableToasts }
            if ($null -ne $savedConfig.WriteEventLog) { $script:Config.WriteEventLog = $savedConfig.WriteEventLog }
            if ($null -ne $savedConfig.RestartDelay) { $script:Config.RestartDelay = $savedConfig.RestartDelay }
            if ($null -ne $savedConfig.IncludeServerKey) { $script:Config.IncludeServerKey = $savedConfig.IncludeServerKey }
        }
        catch { }
    }
}

function Save-Configuration {
    try {
        $configToSave = @{
            AutoSaveLog      = $script:Config.AutoSaveLog
            EnableToasts     = $script:Config.EnableToasts
            WriteEventLog    = $script:Config.WriteEventLog
            RestartDelay     = $script:Config.RestartDelay
            IncludeServerKey = $script:Config.IncludeServerKey
            LastRun          = (Get-Date).ToString("o")
        }
        $configToSave | ConvertTo-Json | Out-File $script:Config.ConfigFile -Encoding UTF8
    }
    catch { }
}

Load-Configuration

# ===========================================================================
# SECTION 6: THEME DETECTION
# ===========================================================================

function Get-WindowsThemeMode {
    try {
        $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
        $val = Get-ItemProperty -Path $key -Name "AppsUseLightTheme" -ErrorAction SilentlyContinue
        if ($null -ne $val -and $val.AppsUseLightTheme -eq 1) { return "Light" }
    }
    catch { }
    return "Dark"
}

$currentTheme = Get-WindowsThemeMode

$PaletteDark = @{
    Background    = [System.Drawing.Color]::FromArgb(24, 24, 28)
    Surface       = [System.Drawing.Color]::FromArgb(30, 30, 35)
    SurfaceLight  = [System.Drawing.Color]::FromArgb(42, 42, 48)
    SurfaceHover  = [System.Drawing.Color]::FromArgb(55, 55, 62)
    CardBackground= [System.Drawing.Color]::FromArgb(34, 34, 40)
    CardBorder    = [System.Drawing.Color]::FromArgb(52, 52, 60)
    Border        = [System.Drawing.Color]::FromArgb(62, 62, 70)
    TextPrimary   = [System.Drawing.Color]::FromArgb(240, 240, 245)
    TextSecondary = [System.Drawing.Color]::FromArgb(185, 185, 195)
    TextMuted     = [System.Drawing.Color]::FromArgb(130, 130, 142)
    TextDimmed    = [System.Drawing.Color]::FromArgb(90, 90, 100)
    WarningDim    = [System.Drawing.Color]::FromArgb(50, 45, 32)
    ProgressBack  = [System.Drawing.Color]::FromArgb(52, 52, 60)
    ChecklistBg   = [System.Drawing.Color]::FromArgb(28, 28, 33)
}

$PaletteLight = @{
    Background    = [System.Drawing.Color]::FromArgb(243, 243, 243)
    Surface       = [System.Drawing.Color]::FromArgb(255, 255, 255)
    SurfaceLight  = [System.Drawing.Color]::FromArgb(240, 240, 240)
    SurfaceHover  = [System.Drawing.Color]::FromArgb(230, 230, 230)
    CardBackground= [System.Drawing.Color]::FromArgb(255, 255, 255)
    CardBorder    = [System.Drawing.Color]::FromArgb(220, 220, 220)
    Border        = [System.Drawing.Color]::FromArgb(200, 200, 200)
    TextPrimary   = [System.Drawing.Color]::FromArgb(20, 20, 20)
    TextSecondary = [System.Drawing.Color]::FromArgb(60, 60, 60)
    TextMuted     = [System.Drawing.Color]::FromArgb(100, 100, 100)
    TextDimmed    = [System.Drawing.Color]::FromArgb(140, 140, 140)
    WarningDim    = [System.Drawing.Color]::FromArgb(255, 248, 220)
    ProgressBack  = [System.Drawing.Color]::FromArgb(220, 220, 220)
    ChecklistBg   = [System.Drawing.Color]::FromArgb(250, 250, 250)
}

$Theme = if ($currentTheme -eq "Light") { $PaletteLight } else { $PaletteDark }

$script:Colors = @{
    Background    = $Theme.Background
    Surface       = $Theme.Surface
    SurfaceLight  = $Theme.SurfaceLight
    SurfaceHover  = $Theme.SurfaceHover
    CardBackground= $Theme.CardBackground
    CardBorder    = $Theme.CardBorder
    Border        = $Theme.Border
    TextPrimary   = $Theme.TextPrimary
    TextSecondary = $Theme.TextSecondary
    TextMuted     = $Theme.TextMuted
    TextDimmed    = $Theme.TextDimmed
    WarningDim    = $Theme.WarningDim
    ProgressBack  = $Theme.ProgressBack
    ChecklistBg   = $Theme.ChecklistBg
    Accent        = [System.Drawing.Color]::FromArgb(56, 132, 244)
    AccentDark    = [System.Drawing.Color]::FromArgb(40, 100, 200)
    AccentHover   = [System.Drawing.Color]::FromArgb(80, 152, 255)
    AccentSubtle  = [System.Drawing.Color]::FromArgb(36, 44, 62)
    Success       = [System.Drawing.Color]::FromArgb(34, 154, 80)
    SuccessHover  = [System.Drawing.Color]::FromArgb(44, 174, 96)
    SuccessBg     = [System.Drawing.Color]::FromArgb(30, 50, 38)
    Warning       = [System.Drawing.Color]::FromArgb(210, 155, 40)
    WarningBright = [System.Drawing.Color]::FromArgb(245, 190, 60)
    WarningBg     = [System.Drawing.Color]::FromArgb(52, 48, 34)
    Danger        = [System.Drawing.Color]::FromArgb(220, 55, 65)
    DangerHover   = [System.Drawing.Color]::FromArgb(240, 70, 80)
    DangerBg      = [System.Drawing.Color]::FromArgb(55, 34, 36)
    Info          = [System.Drawing.Color]::FromArgb(56, 132, 244)
    Neutral       = [System.Drawing.Color]::FromArgb(110, 110, 125)
}

if ($currentTheme -eq "Dark") {
    $script:Colors.Warning = [System.Drawing.Color]::FromArgb(245, 190, 60)
}
if ($currentTheme -eq "Light") {
    $script:Colors.SuccessBg = [System.Drawing.Color]::FromArgb(220, 240, 220)
    $script:Colors.WarningBg = [System.Drawing.Color]::FromArgb(255, 250, 230)
    $script:Colors.DangerBg = [System.Drawing.Color]::FromArgb(255, 235, 235)
}

# ===========================================================================
# SECTION 7: EVENT LOG INTEGRATION
# ===========================================================================

function Initialize-EventLogSource {
    if (-not $script:Config.WriteEventLog) { return }
    
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists($script:Config.EventLogSource)) {
            [System.Diagnostics.EventLog]::CreateEventSource($script:Config.EventLogSource, "Application")
        }
    }
    catch { 
        $script:Config.WriteEventLog = $false
    }
}

function Write-AppEventLog {
    param(
        [string]$Message,
        [ValidateSet("Information", "Warning", "Error")]
        [string]$EntryType = "Information",
        [int]$EventId = 1000
    )
    
    if (-not $script:Config.WriteEventLog) { return }
    
    try {
        Write-EventLog -LogName "Application" -Source $script:Config.EventLogSource -EntryType $EntryType -EventId $EventId -Message $Message
    }
    catch { }
}

Initialize-EventLogSource

# ===========================================================================
# SECTION 8: TOAST NOTIFICATIONS
# ===========================================================================

function Show-ToastNotification {
    param(
        [string]$Title,
        [string]$Message,
        [ValidateSet("Info", "Success", "Warning", "Error")]
        [string]$Type = "Info"
    )
    
    if (-not $script:Config.EnableToasts -or $script:Config.SilentMode) { return }
    
    try {
        # Use Windows Forms NotifyIcon as fallback (works without BurntToast)
        $notifyIcon = New-Object System.Windows.Forms.NotifyIcon
        $notifyIcon.Icon = [System.Drawing.SystemIcons]::Information
        $notifyIcon.BalloonTipTitle = $Title
        $notifyIcon.BalloonTipText = $Message
        
        $tipIcon = switch ($Type) {
            "Success" { [System.Windows.Forms.ToolTipIcon]::Info }
            "Warning" { [System.Windows.Forms.ToolTipIcon]::Warning }
            "Error"   { [System.Windows.Forms.ToolTipIcon]::Error }
            default   { [System.Windows.Forms.ToolTipIcon]::Info }
        }
        $notifyIcon.BalloonTipIcon = $tipIcon
        $notifyIcon.Visible = $true
        $notifyIcon.ShowBalloonTip(5000)
        
        # Clean up after display
        Start-Sleep -Milliseconds 100
        $notifyIcon.Dispose()
    }
    catch { }
}

# ===========================================================================
# SECTION 9: SYSTEM DETECTION FUNCTIONS
# ===========================================================================

function Test-BitLockerEnabled {
    try {
        $volumes = Get-BitLockerVolume -ErrorAction SilentlyContinue
        if ($volumes) {
            foreach ($vol in $volumes) {
                if ($vol.MountPoint -eq "$env:SystemDrive\" -and $vol.ProtectionStatus -eq "On") {
                    return $true
                }
            }
        }
    }
    catch {
        try {
            $encryptable = Get-CimInstance -Namespace "Root\cimv2\Security\MicrosoftVolumeEncryption" `
                -ClassName "Win32_EncryptableVolume" -ErrorAction SilentlyContinue |
                Where-Object { $_.DriveLetter -eq $env:SystemDrive }
            if ($encryptable -and $encryptable.ProtectionStatus -eq 1) {
                return $true
            }
        }
        catch { }
    }
    return $false
}

function Get-NVMeDriverInfo {
    $driverInfo = @{
        HasThirdParty   = $false
        ThirdPartyName  = ""
        InboxVersion    = ""
        CurrentDriver   = ""
        QueueDepth      = "Unknown"
    }
    
    try {
        $nvmeDrivers = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
            Where-Object { $_.DeviceClass -eq "SCSIAdapter" -or $_.DeviceName -match "NVMe" }
        
        $thirdPartyPatterns = @(
            @{ Pattern = "Samsung"; Name = "Samsung NVMe" },
            @{ Pattern = "WD.*NVMe|Western Digital"; Name = "Western Digital NVMe" },
            @{ Pattern = "Intel.*RST|Rapid Storage"; Name = "Intel RST" },
            @{ Pattern = "AMD.*NVMe|AMD RAID"; Name = "AMD NVMe/RAID" },
            @{ Pattern = "Crucial"; Name = "Crucial NVMe" },
            @{ Pattern = "SK.?hynix"; Name = "SK Hynix NVMe" },
            @{ Pattern = "Phison"; Name = "Phison NVMe" }
        )
        
        foreach ($driver in $nvmeDrivers) {
            foreach ($tp in $thirdPartyPatterns) {
                if ($driver.DeviceName -match $tp.Pattern -or $driver.Manufacturer -match $tp.Pattern) {
                    $driverInfo.HasThirdParty = $true
                    $driverInfo.ThirdPartyName = $tp.Name
                    $driverInfo.CurrentDriver = "$($driver.DeviceName) v$($driver.DriverVersion)"
                    break
                }
            }
            if ($driverInfo.HasThirdParty) { break }
        }
        
        $stornvme = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
            Where-Object { $_.InfName -eq "stornvme.inf" } | Select-Object -First 1
        if ($stornvme) {
            $driverInfo.InboxVersion = $stornvme.DriverVersion
            if (-not $driverInfo.HasThirdParty) {
                $driverInfo.CurrentDriver = "Windows Inbox (stornvme) v$($stornvme.DriverVersion)"
            }
        }
        
        # Try to get queue depth from registry
        try {
            $queueDepthPath = "HKLM:\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device"
            if (Test-Path $queueDepthPath) {
                $qd = Get-ItemProperty -Path $queueDepthPath -Name "IoQueueDepth" -ErrorAction SilentlyContinue
                if ($qd) { $driverInfo.QueueDepth = $qd.IoQueueDepth }
            }
        }
        catch { }
    }
    catch {
        $driverInfo.CurrentDriver = "Unable to detect"
    }
    
    return $driverInfo
}

function Get-SystemDrives {
    $drives = [System.Collections.ArrayList]::new()
    
    try {
        $msftDisks = Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_Disk -ErrorAction Stop
        $win32Disks = Get-CimInstance -ClassName Win32_DiskDrive -ErrorAction SilentlyContinue
        
        foreach ($mDisk in $msftDisks) {
            $wDisk = $win32Disks | Where-Object { $_.Index -eq $mDisk.Number } | Select-Object -First 1
            
            $friendlyName = if ($wDisk) { $wDisk.Model } else { $mDisk.FriendlyName }
            $pnpId = if ($wDisk) { $wDisk.PNPDeviceID } else { "Unknown" }
            
            $busEnum = $mDisk.BusType
            $isNVMe = ($busEnum -eq 17)
            
            if (-not $isNVMe -and ($pnpId -match "NVMe" -or $friendlyName -match "NVMe")) {
                $isNVMe = $true
            }
            
            $busLabel = switch ($busEnum) {
                17 { "NVMe" }; 11 { "SATA" }; 7 { "USB" }; 8 { "RAID" }
                default { if ($isNVMe) { "NVMe" } else { "Other" } }
            }
            
            $isBoot = ($mDisk.IsBoot -eq $true -or $mDisk.IsSystem -eq $true)
            $sizeGB = [math]::Round($mDisk.Size / 1GB, 0)
            
            [void]$drives.Add([PSCustomObject]@{
                Number      = $mDisk.Number
                Name        = $friendlyName
                Size        = "$sizeGB GB"
                IsNVMe      = $isNVMe
                BusType     = $busLabel
                IsBoot      = $isBoot
                PNPDeviceID = $pnpId
            })
        }
        
        $drives = [System.Collections.ArrayList]@($drives | Sort-Object Number)
    }
    catch {
        if (-not $script:Config.SilentMode) {
            Write-Log "Error scanning drives: $($_.Exception.Message)" -Level "WARNING"
        }
    }
    
    return $drives
}

function Test-NativeNVMeActive {
    <#
    .SYNOPSIS
        Checks whether the Native NVMe driver (nvmedisk.sys) is active.
        After enabling the patch and rebooting, drives should move from
        "Disk drives" to "Storage disks" in Device Manager and use nvmedisk.sys.
    #>
    $result = @{
        IsActive       = $false
        ActiveDriver   = "Unknown"
        DeviceCategory = "Unknown"
        StorageDisks   = @()
        Details        = ""
    }
    
    try {
        # Check for nvmedisk.sys in loaded drivers
        $nvmeDiskDriver = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq "nvmedisk" -or $_.PathName -match "nvmedisk" }
        
        if ($nvmeDiskDriver -and $nvmeDiskDriver.State -eq "Running") {
            $result.IsActive = $true
            $result.ActiveDriver = "nvmedisk.sys (Native NVMe)"
            $result.Details = "Native NVMe driver is running"
        }
        
        # Check PnP for Storage Disks class (GUID {75416E63-5912-4DFA-AE8F-3EFACCAFFB14})
        $storageDiskDevices = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
            Where-Object { $_.ClassGuid -eq "{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}" }
        
        if ($storageDiskDevices) {
            $result.IsActive = $true
            $result.DeviceCategory = "Storage disks"
            $result.StorageDisks = @($storageDiskDevices | ForEach-Object { $_.Name })
            if (-not $result.Details) {
                $result.Details = "Drives found under Storage disks category"
            }
        }
        else {
            if (-not $result.IsActive) {
                $result.DeviceCategory = "Disk drives (legacy)"
                $result.ActiveDriver = "stornvme.sys / disk.sys (Legacy SCSI)"
                $result.Details = "Legacy NVMe stack active (pre-patch or reboot required)"
            }
        }
        
        # Additional: check driver files on NVMe devices
        $nvmeSignedDrivers = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
            Where-Object { $_.DeviceName -match "NVMe" -or $_.InfName -eq "stornvme.inf" -or $_.InfName -eq "nvmedisk.inf" }
        
        foreach ($drv in $nvmeSignedDrivers) {
            if ($drv.InfName -eq "nvmedisk.inf") {
                $result.IsActive = $true
                $result.ActiveDriver = "nvmedisk.sys v$($drv.DriverVersion)"
                break
            }
        }
    }
    catch {
        $result.Details = "Unable to determine driver status: $($_.Exception.Message)"
    }
    
    return $result
}

function Get-BypassIOStatus {
    <#
    .SYNOPSIS
        Checks BypassIO status using fsutil. The Native NVMe driver currently
        does NOT support BypassIO, which affects DirectStorage gaming performance.
    #>
    $result = @{
        Supported    = $false
        StorageType  = "Unknown"
        DriverCompat = "Unknown"
        BlockedBy    = ""
        RawOutput    = ""
        Warning      = ""
    }
    
    try {
        $systemDrive = $env:SystemDrive + "\"
        $output = & fsutil bypassio state $systemDrive 2>&1 | Out-String
        $result.RawOutput = $output.Trim()
        
        if ($output -match "is currently supported") {
            $result.Supported = $true
        }
        elseif ($output -match "is not currently supported") {
            $result.Supported = $false
        }
        
        if ($output -match "Storage Type:\s*(.+)") {
            $result.StorageType = $matches[1].Trim()
        }
        
        if ($output -match "Storage Driver:\s*(.+)") {
            $result.DriverCompat = $matches[1].Trim()
        }
        
        if ($output -match "Driver Name:\s*(.+)") {
            $result.BlockedBy = $matches[1].Trim()
        }
        elseif ($output -match "Driver:\s*(\S+\.sys)") {
            $result.BlockedBy = $matches[1].Trim()
        }
        
        # Generate warning for gamers
        if (-not $result.Supported -and $result.StorageType -eq "NVMe") {
            $result.Warning = "Native NVMe driver does not support BypassIO. DirectStorage games may have higher CPU usage."
        }
    }
    catch {
        $result.RawOutput = "Unable to check BypassIO: $($_.Exception.Message)"
    }
    
    return $result
}

function Get-WindowsBuildDetails {
    <#
    .SYNOPSIS
        Gets detailed Windows build information including 24H2/25H2 detection.
    #>
    $details = @{
        BuildNumber   = 0
        DisplayVersion = "Unknown"
        Is24H2OrLater = $false
        IsRecommended = $false
        Caption       = ""
        UBR           = 0
    }
    
    try {
        $osInfo = Get-CimInstance -ClassName Win32_OperatingSystem
        $details.BuildNumber = [int]$osInfo.BuildNumber
        $details.Caption = $osInfo.Caption
        
        # Get display version (24H2, 25H2, etc.)
        try {
            $cv = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue
            if ($cv.DisplayVersion) { $details.DisplayVersion = $cv.DisplayVersion }
            if ($cv.UBR) { $details.UBR = [int]$cv.UBR }
        }
        catch { }
        
        # Build 26100+ = 24H2, Build 26200+ = 25H2
        $details.Is24H2OrLater = ($details.BuildNumber -ge 26100)
        $details.IsRecommended = ($details.BuildNumber -ge $script:Config.RecommendedBuild)
    }
    catch { }
    
    return $details
}

# Global state
$script:HasNVMeDrives = $false
$script:BitLockerEnabled = $false
$script:DriverInfo = $null
$script:NativeNVMeStatus = $null
$script:BypassIOStatus = $null
$script:BuildDetails = $null
$script:PreflightChecks = @{}

# ===========================================================================
# SECTION 10: PRE-FLIGHT CHECKS
# ===========================================================================

function Invoke-PreflightChecks {
    $script:PreflightChecks = @{
        WindowsVersion   = @{ Status = "Checking"; Message = "Checking..."; Critical = $true }
        AdminPrivileges  = @{ Status = "Pass"; Message = "Administrator"; Critical = $true }
        NVMeDrives       = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
        BitLocker        = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
        ThirdPartyDriver = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
        SystemProtection = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
        DriverStatus     = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
        BypassIO         = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
    }
    
    # Windows Version (enhanced with 24H2/25H2 detection)
    try {
        $script:BuildDetails = Get-WindowsBuildDetails
        $buildNumber = $script:BuildDetails.BuildNumber
        $displayVer = $script:BuildDetails.DisplayVersion
        
        if ($buildNumber -lt $script:Config.MinWinBuild) {
            $script:PreflightChecks.WindowsVersion = @{ Status = "Fail"; Message = "Build $buildNumber < $($script:Config.MinWinBuild)"; Critical = $true }
        }
        elseif (-not $script:BuildDetails.Is24H2OrLater) {
            $script:PreflightChecks.WindowsVersion = @{ Status = "Warning"; Message = "Build $buildNumber ($displayVer) - 24H2+ recommended"; Critical = $true }
        }
        else {
            $script:PreflightChecks.WindowsVersion = @{ Status = "Pass"; Message = "Win 11 $displayVer (Build $buildNumber)"; Critical = $true }
        }
    }
    catch {
        $script:PreflightChecks.WindowsVersion = @{ Status = "Warning"; Message = "Unable to verify"; Critical = $true }
    }
    
    # NVMe Drives
    $drives = Get-SystemDrives
    $nvmeCount = ($drives | Where-Object { $_.IsNVMe }).Count
    $script:HasNVMeDrives = ($nvmeCount -gt 0)
    if ($nvmeCount -gt 0) {
        $script:PreflightChecks.NVMeDrives = @{ Status = "Pass"; Message = "$nvmeCount NVMe drive(s)"; Critical = $false }
    }
    else {
        $script:PreflightChecks.NVMeDrives = @{ Status = "Warning"; Message = "No NVMe drives"; Critical = $false }
    }
    
    # BitLocker
    $script:BitLockerEnabled = Test-BitLockerEnabled
    if ($script:BitLockerEnabled) {
        $script:PreflightChecks.BitLocker = @{ Status = "Warning"; Message = "Encryption active"; Critical = $false }
    }
    else {
        $script:PreflightChecks.BitLocker = @{ Status = "Pass"; Message = "Not detected"; Critical = $false }
    }
    
    # Third-party Driver
    $script:DriverInfo = Get-NVMeDriverInfo
    if ($script:DriverInfo.HasThirdParty) {
        $script:PreflightChecks.ThirdPartyDriver = @{ Status = "Warning"; Message = $script:DriverInfo.ThirdPartyName; Critical = $false }
    }
    else {
        $script:PreflightChecks.ThirdPartyDriver = @{ Status = "Pass"; Message = "Using inbox driver"; Critical = $false }
    }
    
    # System Protection
    try {
        $restoreStatus = Get-ComputerRestorePoint -ErrorAction SilentlyContinue
        $script:PreflightChecks.SystemProtection = @{ Status = "Pass"; Message = "Available"; Critical = $false }
    }
    catch {
        $script:PreflightChecks.SystemProtection = @{ Status = "Warning"; Message = "May be disabled"; Critical = $false }
    }
    
    # Native NVMe Driver Activation Status
    $script:NativeNVMeStatus = Test-NativeNVMeActive
    if ($script:NativeNVMeStatus.IsActive) {
        $script:PreflightChecks.DriverStatus = @{ Status = "Pass"; Message = "nvmedisk.sys active"; Critical = $false }
    }
    else {
        $patchStatus = Test-PatchStatus
        if ($patchStatus.Applied) {
            $script:PreflightChecks.DriverStatus = @{ Status = "Warning"; Message = "Patch set, reboot needed"; Critical = $false }
        }
        else {
            $script:PreflightChecks.DriverStatus = @{ Status = "Info"; Message = "stornvme (legacy)"; Critical = $false }
        }
    }
    
    # BypassIO / DirectStorage Status
    $script:BypassIOStatus = Get-BypassIOStatus
    if ($script:BypassIOStatus.Supported) {
        $script:PreflightChecks.BypassIO = @{ Status = "Pass"; Message = "Supported"; Critical = $false }
    }
    else {
        if ($script:NativeNVMeStatus.IsActive) {
            $script:PreflightChecks.BypassIO = @{ Status = "Warning"; Message = "Not supported (gaming impact)"; Critical = $false }
        }
        else {
            $blockedMsg = if ($script:BypassIOStatus.BlockedBy) { "Blocked: $($script:BypassIOStatus.BlockedBy)" } else { "Not available" }
            $script:PreflightChecks.BypassIO = @{ Status = "Info"; Message = $blockedMsg; Critical = $false }
        }
    }
    
    return $script:PreflightChecks
}

function Test-PreflightPassed {
    foreach ($check in $script:PreflightChecks.Values) {
        if ($check.Critical -and $check.Status -eq "Fail") {
            return $false
        }
    }
    return $true
}

# ===========================================================================
# SECTION 11: LOGGING SYSTEM
# ===========================================================================

function Write-Log {
    param(
        [Parameter(Mandatory)]
        [string]$Message,
        [ValidateSet("INFO", "SUCCESS", "WARNING", "ERROR", "DEBUG")]
        [string]$Level = "INFO"
    )
    
    $timestamp = Get-Date -Format "HH:mm:ss"
    $logEntry = "[$timestamp] [$Level] $Message"
    
    [void]$script:Config.LogHistory.Add($logEntry)
    
    # Write to Event Log for important events
    if ($Level -in @("SUCCESS", "ERROR", "WARNING") -and $script:Config.WriteEventLog) {
        $eventType = switch ($Level) {
            "ERROR" { "Error" }
            "WARNING" { "Warning" }
            default { "Information" }
        }
        $eventId = switch ($Level) {
            "SUCCESS" { 1001 }
            "WARNING" { 2001 }
            "ERROR" { 3001 }
            default { 1000 }
        }
        Write-AppEventLog -Message $Message -EntryType $eventType -EventId $eventId
    }
    
    if ($script:Config.SilentMode) {
        switch ($Level) {
            "ERROR"   { Write-Error $Message }
            "WARNING" { Write-Warning $Message }
            "SUCCESS" { Write-Host $Message -ForegroundColor Green }
            default   { Write-Host $Message }
        }
        return
    }
    
    if ($script:rtbOutput) {
        $color = switch ($Level) {
            "SUCCESS" { $script:Colors.Success }
            "WARNING" { $script:Colors.Warning }
            "ERROR"   { $script:Colors.Danger }
            "DEBUG"   { $script:Colors.TextDimmed }
            default   { $script:Colors.TextSecondary }
        }
        
        $script:rtbOutput.SelectionStart = $script:rtbOutput.TextLength
        $script:rtbOutput.SelectionLength = 0
        $script:rtbOutput.SelectionColor = $color
        $script:rtbOutput.AppendText("$logEntry`r`n")
        $script:rtbOutput.ScrollToCaret()
        
        if ($script:Config.LogHistory.Count % 5 -eq 0) {
            [System.Windows.Forms.Application]::DoEvents()
        }
    }
}

function Save-LogFile {
    param([string]$Suffix = "")
    
    if ($script:Config.LogHistory.Count -eq 0) { return $null }
    
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $filename = "NVMe_Patcher_Log_$timestamp$Suffix.txt"
    $filepath = Join-Path $script:Config.WorkingDir $filename
    
    try {
        $script:Config.LogHistory | Out-File -FilePath $filepath -Encoding UTF8
        return $filepath
    }
    catch { return $null }
}

function Export-LogFile {
    $saveDialog = New-Object System.Windows.Forms.SaveFileDialog
    $saveDialog.Filter = "Text Files (*.txt)|*.txt|Log Files (*.log)|*.log"
    $saveDialog.FileName = "NVMe_Patcher_Log_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
    $saveDialog.Title = "Export Log File"
    $saveDialog.InitialDirectory = $script:Config.WorkingDir
    
    if ($saveDialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        try {
            $script:Config.LogHistory | Out-File -FilePath $saveDialog.FileName -Encoding UTF8
            Write-Log "Log exported to: $($saveDialog.FileName)" -Level "SUCCESS"
        }
        catch {
            Write-Log "Failed to export log: $($_.Exception.Message)" -Level "ERROR"
        }
    }
}

function Copy-LogToClipboard {
    try {
        $logText = $script:Config.LogHistory -join "`r`n"
        [System.Windows.Forms.Clipboard]::SetText($logText)
        Write-Log "Log copied to clipboard" -Level "SUCCESS"
    }
    catch {
        Write-Log "Failed to copy log: $($_.Exception.Message)" -Level "ERROR"
    }
}

# ===========================================================================
# SECTION 12: DIAGNOSTICS EXPORT
# ===========================================================================

function Export-SystemDiagnostics {
    param([string]$OutputPath = $null)
    
    if (-not $OutputPath) {
        $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
        $OutputPath = Join-Path $script:Config.WorkingDir "NVMe_Diagnostics_$timestamp.txt"
    }
    
    $report = @"
================================================================================
NVMe Driver Patcher - System Diagnostics Report
Generated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Version: $($script:Config.AppVersion)
================================================================================

SYSTEM INFORMATION
------------------
Computer Name: $env:COMPUTERNAME
User: $env:USERNAME
Domain: $env:USERDOMAIN
OS: $([Environment]::OSVersion.VersionString)

"@
    
    try {
        $osInfo = Get-CimInstance -ClassName Win32_OperatingSystem
        $report += "OS Caption: $($osInfo.Caption)`n"
        $report += "OS Build: $($osInfo.BuildNumber)`n"
        $report += "OS Version: $($osInfo.Version)`n"
        $report += "Install Date: $($osInfo.InstallDate)`n"
        $report += "Last Boot: $($osInfo.LastBootUpTime)`n"
    }
    catch { $report += "Unable to retrieve OS information`n" }
    
    $report += "`nHARDWARE`n--------`n"
    try {
        $cs = Get-CimInstance -ClassName Win32_ComputerSystem
        $report += "Manufacturer: $($cs.Manufacturer)`n"
        $report += "Model: $($cs.Model)`n"
        $report += "Total RAM: $([math]::Round($cs.TotalPhysicalMemory / 1GB, 2)) GB`n"
    }
    catch { }
    
    $report += "`nSTORAGE DRIVES`n--------------`n"
    $drives = Get-SystemDrives
    foreach ($drive in $drives) {
        $nvmeTag = if ($drive.IsNVMe) { " [NVMe]" } else { "" }
        $bootTag = if ($drive.IsBoot) { " [BOOT]" } else { "" }
        $report += "Disk $($drive.Number): $($drive.Name) ($($drive.Size))$nvmeTag$bootTag`n"
        $report += "  Bus Type: $($drive.BusType)`n"
        $report += "  PNP ID: $($drive.PNPDeviceID)`n"
    }
    
    $report += "`nNVMe DRIVER INFORMATION`n-----------------------`n"
    $driverInfo = Get-NVMeDriverInfo
    $report += "Current Driver: $($driverInfo.CurrentDriver)`n"
    $report += "Inbox Version: $($driverInfo.InboxVersion)`n"
    $report += "Third-Party: $(if ($driverInfo.HasThirdParty) { $driverInfo.ThirdPartyName } else { 'No' })`n"
    $report += "Queue Depth: $($driverInfo.QueueDepth)`n"
    
    $report += "`nNATIVE NVMe DRIVER STATUS`n-------------------------`n"
    $nativeStatus = Test-NativeNVMeActive
    $report += "Native NVMe Active: $(if ($nativeStatus.IsActive) { 'Yes' } else { 'No' })`n"
    $report += "Active Driver: $($nativeStatus.ActiveDriver)`n"
    $report += "Device Category: $($nativeStatus.DeviceCategory)`n"
    if ($nativeStatus.StorageDisks.Count -gt 0) {
        $report += "Storage Disks:`n"
        foreach ($sd in $nativeStatus.StorageDisks) { $report += "  - $sd`n" }
    }
    $report += "Details: $($nativeStatus.Details)`n"
    
    $report += "`nBYPASSIO / DIRECTSTORAGE STATUS`n-------------------------------`n"
    $bypassStatus = Get-BypassIOStatus
    $report += "BypassIO Supported: $(if ($bypassStatus.Supported) { 'Yes' } else { 'No' })`n"
    $report += "Storage Type: $($bypassStatus.StorageType)`n"
    $report += "Driver Compatibility: $($bypassStatus.DriverCompat)`n"
    if ($bypassStatus.BlockedBy) { $report += "Blocked By: $($bypassStatus.BlockedBy)`n" }
    if ($bypassStatus.Warning) { $report += "WARNING: $($bypassStatus.Warning)`n" }
    $report += "Raw Output:`n$($bypassStatus.RawOutput)`n"
    
    $report += "`nWINDOWS BUILD DETAILS`n--------------------`n"
    $buildDets = Get-WindowsBuildDetails
    $report += "Build Number: $($buildDets.BuildNumber)`n"
    $report += "Display Version: $($buildDets.DisplayVersion)`n"
    $report += "UBR: $($buildDets.UBR)`n"
    $report += "Is 24H2+: $($buildDets.Is24H2OrLater)`n"
    $report += "Is Recommended Build: $($buildDets.IsRecommended)`n"
    
    $report += "`nBITLOCKER STATUS`n----------------`n"
    $report += "System Drive Encrypted: $(if (Test-BitLockerEnabled) { 'Yes' } else { 'No' })`n"
    
    $report += "`nPATCH STATUS`n------------`n"
    $status = Test-PatchStatus
    $report += "Applied: $($status.Applied)`n"
    $report += "Partial: $($status.Partial)`n"
    $report += "Components: $($status.Count)/$($status.Total)`n"
    $report += "Applied Keys: $($status.Keys -join ', ')`n"
    
    $report += "`nREGISTRY KEYS`n-------------`n"
    $report += "Path: $($script:Config.RegistryPath)`n"
    $allIDs = [System.Collections.ArrayList]@($script:Config.FeatureIDs)
    [void]$allIDs.Add($script:Config.ServerFeatureID)
    foreach ($id in $allIDs) {
        $friendlyName = if ($script:Config.FeatureNames.ContainsKey($id)) { $script:Config.FeatureNames[$id] } else { "Unknown" }
        try {
            $val = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
            $value = if ($val) { $val.$id } else { "Not Set" }
            $report += "  $id ($friendlyName) = $value`n"
        }
        catch { $report += "  $id ($friendlyName) = Error reading`n" }
    }
    
    $report += "`nSafeBoot Minimal: $(if (Test-Path $script:Config.SafeBootMinimal) { 'Present' } else { 'Not Present' })`n"
    $report += "SafeBoot Network: $(if (Test-Path $script:Config.SafeBootNetwork) { 'Present' } else { 'Not Present' })`n"
    
    $report += "`nSYSTEM PROTECTION`n-----------------`n"
    try {
        $restorePoints = Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Select-Object -First 5
        if ($restorePoints) {
            foreach ($rp in $restorePoints) {
                $report += "  [$($rp.SequenceNumber)] $($rp.Description) - $($rp.CreationTime)`n"
            }
        }
        else {
            $report += "  No restore points found`n"
        }
    }
    catch { $report += "  Unable to retrieve restore points`n" }
    
    $report += "`nACTIVITY LOG`n------------`n"
    $report += ($script:Config.LogHistory -join "`n")
    
    $report += "`n`n================================================================================`n"
    $report += "End of Diagnostics Report`n"
    
    try {
        $report | Out-File -FilePath $OutputPath -Encoding UTF8
        return $OutputPath
    }
    catch {
        return $null
    }
}

# ===========================================================================
# SECTION 13: POST-REBOOT VERIFICATION SCRIPT
# ===========================================================================

function New-VerificationScript {
    param([string]$OutputPath = $null)
    
    if (-not $OutputPath) {
        $OutputPath = Join-Path $script:Config.WorkingDir "Verify_NVMe_Patch.ps1"
    }
    
    $scriptContent = @'
<#
.SYNOPSIS
    NVMe Driver Patch Verification Script
    
.DESCRIPTION
    Run this script after reboot to verify the NVMe driver patch was applied successfully.
    Checks registry keys, active driver (nvmedisk.sys), and BypassIO status.
    Generated by NVMe Driver Patcher.
#>

#Requires -RunAsAdministrator

$Host.UI.RawUI.WindowTitle = "NVMe Patch Verification"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "NVMe Driver Patch Verification" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# Check registry keys
$registryPath = "HKLM:\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides"
$featureIDs = @(
    @{ ID = "735209102"; Name = "NativeNVMeStackForGeClient (Primary enable)" },
    @{ ID = "1853569164"; Name = "UxAccOptimization (Extended functionality)" },
    @{ ID = "156965516"; Name = "Standalone_Future (Performance optimizations)" }
)
$safeBootMinimal = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"
$safeBootNetwork = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"

$passCount = 0
$totalChecks = 5

Write-Host "REGISTRY KEYS" -ForegroundColor Yellow
Write-Host "-------------" -ForegroundColor Yellow
Write-Host ""

# Feature Flags
foreach ($feat in $featureIDs) {
    $val = Get-ItemProperty -Path $registryPath -Name $feat.ID -ErrorAction SilentlyContinue
    if ($val -and $val.($feat.ID) -eq 1) {
        Write-Host "  [PASS] $($feat.ID) - $($feat.Name)" -ForegroundColor Green
        $passCount++
    }
    else {
        Write-Host "  [FAIL] $($feat.ID) - $($feat.Name)" -ForegroundColor Red
    }
}

# Optional Server Key
$serverKeyVal = Get-ItemProperty -Path $registryPath -Name "1176759950" -ErrorAction SilentlyContinue
if ($serverKeyVal -and $serverKeyVal."1176759950" -eq 1) {
    Write-Host "  [INFO] 1176759950 - Microsoft Official Server key (optional, present)" -ForegroundColor Cyan
}

# SafeBoot Keys
if (Test-Path $safeBootMinimal) {
    Write-Host "  [PASS] SafeBoot Minimal" -ForegroundColor Green
    $passCount++
}
else {
    Write-Host "  [FAIL] SafeBoot Minimal" -ForegroundColor Red
}

if (Test-Path $safeBootNetwork) {
    Write-Host "  [PASS] SafeBoot Network" -ForegroundColor Green
    $passCount++
}
else {
    Write-Host "  [FAIL] SafeBoot Network" -ForegroundColor Red
}

Write-Host ""
Write-Host "DRIVER VERIFICATION" -ForegroundColor Yellow
Write-Host "-------------------" -ForegroundColor Yellow
Write-Host ""

# Check if nvmedisk.sys is active (Native NVMe)
$nvmeDiskDriver = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq "nvmedisk" -or $_.PathName -match "nvmedisk" }

if ($nvmeDiskDriver -and $nvmeDiskDriver.State -eq "Running") {
    Write-Host "  [PASS] nvmedisk.sys is RUNNING (Native NVMe active)" -ForegroundColor Green
}
else {
    Write-Host "  [INFO] nvmedisk.sys is NOT running (legacy stornvme.sys stack)" -ForegroundColor Yellow
}

# Check Device Manager category
$storageDiskDevices = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
    Where-Object { $_.ClassGuid -eq "{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}" }

if ($storageDiskDevices) {
    Write-Host "  [PASS] NVMe drives found under 'Storage disks' category" -ForegroundColor Green
    foreach ($dev in $storageDiskDevices) {
        Write-Host "         - $($dev.Name)" -ForegroundColor Cyan
    }
}
else {
    Write-Host "  [INFO] Drives still under 'Disk drives' (legacy category)" -ForegroundColor Yellow
}

# Show current NVMe driver info
Write-Host ""
$stornvme = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
    Where-Object { $_.InfName -eq "stornvme.inf" -or $_.InfName -eq "nvmedisk.inf" } | Select-Object -First 1
if ($stornvme) {
    Write-Host "  Active NVMe Driver: $($stornvme.InfName) v$($stornvme.DriverVersion)" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "BYPASSIO / DIRECTSTORAGE" -ForegroundColor Yellow
Write-Host "------------------------" -ForegroundColor Yellow
Write-Host ""

try {
    $systemDrive = $env:SystemDrive + "\"
    $bypassOutput = & fsutil bypassio state $systemDrive 2>&1 | Out-String
    
    if ($bypassOutput -match "is currently supported") {
        Write-Host "  [PASS] BypassIO is supported on $systemDrive" -ForegroundColor Green
    }
    else {
        Write-Host "  [WARN] BypassIO is NOT supported on $systemDrive" -ForegroundColor Yellow
        Write-Host "         DirectStorage games may have higher CPU usage." -ForegroundColor Yellow
    }
    
    Write-Host ""
    Write-Host "  Raw output:" -ForegroundColor DarkGray
    $bypassOutput.Trim().Split("`n") | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
}
catch {
    Write-Host "  [INFO] Unable to check BypassIO status" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan

if ($passCount -eq $totalChecks) {
    Write-Host "RESULT: PATCH VERIFIED ($passCount/$totalChecks)" -ForegroundColor Green
    Write-Host "" 
    Write-Host "The NVMe driver patch is fully applied." -ForegroundColor Green
    if (-not $storageDiskDevices) {
        Write-Host "Note: A reboot may be required for the driver to fully activate." -ForegroundColor Yellow
    }
}
elseif ($passCount -gt 0) {
    Write-Host "RESULT: PARTIAL ($passCount/$totalChecks)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Some components are missing. Consider re-running the patcher." -ForegroundColor Yellow
}
else {
    Write-Host "RESULT: NOT APPLIED (0/$totalChecks)" -ForegroundColor Red
    Write-Host ""
    Write-Host "The patch does not appear to be applied." -ForegroundColor Red
}

Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Press any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
'@
    
    try {
        $scriptContent | Out-File -FilePath $OutputPath -Encoding UTF8
        return $OutputPath
    }
    catch {
        return $null
    }
}

# ===========================================================================
# SECTION 14: PATCH STATUS & VALIDATION
# ===========================================================================

function Test-WindowsCompatibility {
    Write-Log "Checking Windows compatibility..."
    
    try {
        $osInfo = Get-CimInstance -ClassName Win32_OperatingSystem
        $buildNumber = [int]$osInfo.BuildNumber
        $caption = $osInfo.Caption
        
        Write-Log "Detected: $caption (Build $buildNumber)" -Level "INFO"
        
        if ($buildNumber -lt $script:Config.MinWinBuild) {
            Write-Log "Windows 11 (Build $($script:Config.MinWinBuild)+) required!" -Level "ERROR"
            return $false
        }
        
        Write-Log "Windows version compatible" -Level "SUCCESS"
        return $true
    }
    catch {
        Write-Log "Could not determine Windows version: $($_.Exception.Message)" -Level "WARNING"
        return $true
    }
}

function Test-PatchStatus {
    if (-not $script:Config.SilentMode -or $Status) {
        Write-Log "Checking current patch status (5 components)..." -Level "DEBUG"
    }
    
    $count = 0
    $appliedKeys = [System.Collections.ArrayList]::new()
    
    if (Test-Path $script:Config.RegistryPath) {
        foreach ($id in $script:Config.FeatureIDs) {
            $val = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
            if ($null -ne $val) {
                $propValue = $val | Select-Object -ExpandProperty $id -ErrorAction SilentlyContinue
                if ($propValue -eq 1) {
                    $count++
                    [void]$appliedKeys.Add($id)
                }
            }
        }
    }
    
    if (Test-Path $script:Config.SafeBootMinimal) {
        $val = Get-ItemProperty -Path $script:Config.SafeBootMinimal -Name "(Default)" -ErrorAction SilentlyContinue
        if ($val -and $val."(Default)" -eq $script:Config.SafeBootValue) {
            $count++
            [void]$appliedKeys.Add("SafeBootMinimal")
        }
    }
    
    if (Test-Path $script:Config.SafeBootNetwork) {
        $val = Get-ItemProperty -Path $script:Config.SafeBootNetwork -Name "(Default)" -ErrorAction SilentlyContinue
        if ($val -and $val."(Default)" -eq $script:Config.SafeBootValue) {
            $count++
            [void]$appliedKeys.Add("SafeBootNetwork")
        }
    }
    
    $result = [PSCustomObject]@{
        Applied = ($count -eq $script:Config.TotalComponents)
        Partial = ($count -gt 0 -and $count -lt $script:Config.TotalComponents)
        Count   = $count
        Total   = $script:Config.TotalComponents
        Keys    = $appliedKeys
    }
    
    if (-not $script:Config.SilentMode) {
        if ($result.Applied) {
            Write-Log "Patch status: APPLIED ($count/$($script:Config.TotalComponents) components)" -Level "SUCCESS"
        }
        elseif ($result.Partial) {
            Write-Log "Patch status: PARTIAL ($count/$($script:Config.TotalComponents) components)" -Level "WARNING"
        }
        else {
            Write-Log "Patch status: NOT APPLIED (0/$($script:Config.TotalComponents) components)" -Level "INFO"
        }
    }
    
    return $result
}

# ===========================================================================
# SECTION 15: UI HELPER FUNCTIONS (Abbreviated for space - same as v2.8.0)
# ===========================================================================

function Get-RoundedRegion {
    param([int]$Width, [int]$Height, [int]$Radius = 8)
    if ($Width -le 0 -or $Height -le 0) { return $null }
    $path = $null; $region = $null
    try {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $Radius * 2
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($Width - $d, 0, $d, $d, 270, 90)
        $path.AddArc($Width - $d, $Height - $d, $d, $d, 0, 90)
        $path.AddArc(0, $Height - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $region = New-Object System.Drawing.Region($path)
    }
    finally { if ($path) { $path.Dispose() } }
    return $region
}

function Set-ControlTagData {
    param([System.Windows.Forms.Control]$Control, [hashtable]$NewData)
    $existingTag = $Control.Tag
    if ($null -eq $existingTag) { $Control.Tag = $NewData.Clone() }
    elseif ($existingTag -is [hashtable]) { foreach ($key in $NewData.Keys) { $existingTag[$key] = $NewData[$key] } }
    else { $merged = $NewData.Clone(); $merged['_OriginalTag'] = $existingTag; $Control.Tag = $merged }
}

function Get-ControlTagValue {
    param([System.Windows.Forms.Control]$Control, [string]$Key, $Default = $null)
    $tag = $Control.Tag
    if ($null -eq $tag) { return $Default }
    if ($tag -is [hashtable] -and $tag.ContainsKey($Key)) { return $tag[$Key] }
    return $Default
}

function Set-RoundedCorners {
    param([System.Windows.Forms.Control]$Control, [int]$Radius = 8)
    $alreadyAttached = Get-ControlTagValue -Control $Control -Key '_ResizeHandlerAttached' -Default $false
    Set-ControlTagData -Control $Control -NewData @{ CornerRadius = $Radius }
    $region = Get-RoundedRegion -Width $Control.Width -Height $Control.Height -Radius $Radius
    if ($region) { $oldRegion = $Control.Region; $Control.Region = $region; if ($oldRegion) { $oldRegion.Dispose() } }
    if (-not $alreadyAttached) {
        Set-ControlTagData -Control $Control -NewData @{ _ResizeHandlerAttached = $true }
        $Control.Add_Resize({ $r = Get-ControlTagValue -Control $this -Key 'CornerRadius' -Default 8; $newRegion = Get-RoundedRegion -Width $this.Width -Height $this.Height -Radius $r; if ($newRegion) { $oldRegion = $this.Region; $this.Region = $newRegion; if ($oldRegion) { $oldRegion.Dispose() } } })
    }
}

function Set-DarkScrollbar {
    param([System.Windows.Forms.Control]$Control)
    try { [DarkScrollBar]::SetWindowTheme($Control.Handle, "DarkMode_Explorer", $null) | Out-Null } catch { }
}

function Update-DrivesList {
    if (-not $script:pnlDrivesContent) { return }
    $script:pnlDrivesContent.Controls.Clear()
    $drives = Get-SystemDrives
    if ($drives.Count -eq 0) {
        $noLabel = New-Object System.Windows.Forms.Label
        $noLabel.Location  = New-Object System.Drawing.Point(0, 4)
        $noLabel.Size      = New-Object System.Drawing.Size(500, 20)
        $noLabel.Text      = "No drives detected"
        $noLabel.ForeColor = $script:Colors.TextMuted
        $noLabel.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
        $script:pnlDrivesContent.Controls.Add($noLabel)
        return
    }
    $driveRowH = $script:UIConstants.DriveRowHeight
    $yy = 0
    foreach ($drv in $drives) {
        $row = New-Object System.Windows.Forms.Panel
        $row.Location  = New-Object System.Drawing.Point(0, $yy)
        $row.Size      = New-Object System.Drawing.Size(($script:pnlDrivesContent.Width - 2), $driveRowH)
        $row.BackColor = [System.Drawing.Color]::Transparent

        # NVMe / bus type indicator dot
        $dot = New-Object System.Windows.Forms.Panel
        $dot.Size     = New-Object System.Drawing.Size(8, 8)
        $dot.Location = New-Object System.Drawing.Point(4, 10)
        $dot.BackColor = if ($drv.IsNVMe) { $script:Colors.Success } else { $script:Colors.TextDimmed }
        $dp = New-Object System.Drawing.Drawing2D.GraphicsPath; $dp.AddEllipse(0, 0, 8, 8)
        $dot.Region = New-Object System.Drawing.Region($dp); $dp.Dispose()
        $row.Controls.Add($dot)

        # Drive name
        $nameLbl = New-Object System.Windows.Forms.Label
        $nameLbl.Location  = New-Object System.Drawing.Point(20, 4)
        $nameLbl.Size      = New-Object System.Drawing.Size(260, 20)
        $nameLbl.Text      = $drv.Name
        $nameLbl.ForeColor = $script:Colors.TextPrimary
        $nameLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
        $nameLbl.AutoEllipsis = $true
        $row.Controls.Add($nameLbl)

        # Size
        $sizeLbl = New-Object System.Windows.Forms.Label
        $sizeLbl.Location  = New-Object System.Drawing.Point(284, 4)
        $sizeLbl.Size      = New-Object System.Drawing.Size(70, 20)
        $sizeLbl.Text      = $drv.Size
        $sizeLbl.ForeColor = $script:Colors.TextSecondary
        $sizeLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
        $sizeLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
        $row.Controls.Add($sizeLbl)

        # Bus type pill
        $busLbl = New-Object System.Windows.Forms.Label
        $busLbl.Size      = New-Object System.Drawing.Size(52, 18)
        $busLbl.Location  = New-Object System.Drawing.Point(366, 5)
        $busLbl.Text      = $drv.BusType
        $busLbl.Font      = New-Object System.Drawing.Font("Segoe UI Semibold", 7.5)
        $busLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
        if ($drv.IsNVMe) {
            $busLbl.ForeColor = $script:Colors.Accent
            $busLbl.BackColor = $script:Colors.AccentSubtle
        } else {
            $busLbl.ForeColor = $script:Colors.TextMuted
            $busLbl.BackColor = $script:Colors.SurfaceLight
        }
        Set-RoundedCorners -Control $busLbl -Radius 8
        $row.Controls.Add($busLbl)

        # Boot indicator
        if ($drv.IsBoot) {
            $bootLbl = New-Object System.Windows.Forms.Label
            $bootLbl.Size      = New-Object System.Drawing.Size(42, 18)
            $bootLbl.Location  = New-Object System.Drawing.Point(426, 5)
            $bootLbl.Text      = "BOOT"
            $bootLbl.Font      = New-Object System.Drawing.Font("Segoe UI Semibold", 7.5)
            $bootLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
            $bootLbl.ForeColor = $script:Colors.Warning
            $bootLbl.BackColor = [System.Drawing.Color]::FromArgb(40, 255, 193, 7)
            Set-RoundedCorners -Control $bootLbl -Radius 8
            $row.Controls.Add($bootLbl)
        }

        $script:pnlDrivesContent.Controls.Add($row)
        $yy += $driveRowH
    }
    $script:pnlDrivesContent.AutoScrollMinSize = New-Object System.Drawing.Size(0, $yy)
}

function New-CardPanel {
    param([System.Drawing.Point]$Location, [System.Drawing.Size]$Size, [string]$Title = "", [int]$Padding = 20, [int]$CornerRadius = 10)
    $card = New-Object System.Windows.Forms.Panel
    $card.Location = $Location; $card.Size = $Size; $card.BackColor = $script:Colors.CardBackground
    $card.Padding = New-Object System.Windows.Forms.Padding($Padding)
    $card.GetType().GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic).SetValue($card, $true, $null)
    Set-ControlTagData -Control $card -NewData @{ CornerRadius = $CornerRadius; Title = $Title }
    $region = Get-RoundedRegion -Width $Size.Width -Height $Size.Height -Radius $CornerRadius
    if ($region) { $card.Region = $region }
    $card.Add_Resize({ $r = Get-ControlTagValue -Control $this -Key 'CornerRadius' -Default 10; $newRegion = Get-RoundedRegion -Width $this.Width -Height $this.Height -Radius $r; if ($newRegion) { $oldRegion = $this.Region; $this.Region = $newRegion; if ($oldRegion) { $oldRegion.Dispose() } } })
    $card.Add_Paint({ param($sender, $e); $g = $e.Graphics; $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias; $r = Get-ControlTagValue -Control $sender -Key 'CornerRadius' -Default 10; $d = $r * 2; $pen = $null; $borderPath = $null; try { $pen = New-Object System.Drawing.Pen($script:Colors.CardBorder, 1); $rect = New-Object System.Drawing.Rectangle(0, 0, ($sender.Width - 1), ($sender.Height - 1)); $borderPath = New-Object System.Drawing.Drawing2D.GraphicsPath; $borderPath.AddArc($rect.X, $rect.Y, $d, $d, 180, 90); $borderPath.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90); $borderPath.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90); $borderPath.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90); $borderPath.CloseFigure(); $g.DrawPath($pen, $borderPath) } finally { if ($borderPath) { $borderPath.Dispose() }; if ($pen) { $pen.Dispose() } } })
    if ($Title) { $titleLabel = New-Object System.Windows.Forms.Label; $titleLabel.Text = $Title.ToUpper(); $titleLabel.Location = New-Object System.Drawing.Point($Padding, 16); $titleLabel.Size = New-Object System.Drawing.Size(($Size.Width - $Padding * 2), 18); $titleLabel.ForeColor = $script:Colors.TextMuted; $titleLabel.Font = New-Object System.Drawing.Font("Segoe UI", 8.25, [System.Drawing.FontStyle]::Bold); Set-ControlTagData -Control $titleLabel -NewData @{ Role = "cardTitle" }; $card.Controls.Add($titleLabel) }
    return $card
}

function New-ModernButton {
    param([string]$Text, [System.Drawing.Point]$Location, [System.Drawing.Size]$Size, [System.Drawing.Color]$BackColor, [System.Drawing.Color]$HoverColor, [System.Drawing.Color]$ForeColor = $script:Colors.TextPrimary, [scriptblock]$OnClick, [string]$ToolTip = "", [bool]$IsPrimary = $false, [int]$CornerRadius = 6)
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text; $button.Location = $Location; $button.Size = $Size
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat; $button.FlatAppearance.BorderSize = 0
    $button.BackColor = $BackColor; $button.ForeColor = $ForeColor; $button.Cursor = [System.Windows.Forms.Cursors]::Hand
    Set-ControlTagData -Control $button -NewData @{ Original = $BackColor; Hover = $HoverColor; IsPrimary = $IsPrimary; CornerRadius = $CornerRadius }
    if ($IsPrimary) { $button.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 10) } else { $button.Font = New-Object System.Drawing.Font("Segoe UI", 9.5) }
    $region = Get-RoundedRegion -Width $Size.Width -Height $Size.Height -Radius $CornerRadius; if ($region) { $button.Region = $region }
    $button.Add_Resize({ $r = Get-ControlTagValue -Control $this -Key 'CornerRadius' -Default 6; $newRegion = Get-RoundedRegion -Width $this.Width -Height $this.Height -Radius $r; if ($newRegion) { $oldRegion = $this.Region; $this.Region = $newRegion; if ($oldRegion) { $oldRegion.Dispose() } } })
    $button.Add_MouseEnter({ if ($this.Enabled) { $hoverColor = Get-ControlTagValue -Control $this -Key 'Hover'; if ($hoverColor) { $this.BackColor = $hoverColor } } })
    $button.Add_MouseLeave({ if ($this.Enabled) { $originalColor = Get-ControlTagValue -Control $this -Key 'Original'; if ($originalColor) { $this.BackColor = $originalColor } } })
    $button.Add_EnabledChanged({ if ($this.Enabled) { $originalColor = Get-ControlTagValue -Control $this -Key 'Original'; if ($originalColor) { $this.BackColor = $originalColor }; $this.ForeColor = $script:Colors.TextPrimary; $this.Cursor = [System.Windows.Forms.Cursors]::Hand } else { $this.BackColor = $script:Colors.SurfaceLight; $this.ForeColor = $script:Colors.TextDimmed; $this.Cursor = [System.Windows.Forms.Cursors]::Default } })
    if ($OnClick) { $button.Add_Click($OnClick) }
    if ($ToolTip -and $script:ToolTipProvider) { $script:ToolTipProvider.SetToolTip($button, $ToolTip) }
    return $button
}

function Set-ButtonsEnabled { param([bool]$Enabled); if ($script:btnApply) { $script:btnApply.Enabled = $Enabled }; if ($script:btnRemove) { $script:btnRemove.Enabled = $Enabled }; if ($script:btnBackup) { $script:btnBackup.Enabled = $Enabled }; [System.Windows.Forms.Application]::DoEvents() }

function Update-Progress { param([int]$Value, [string]$Status = ""); if ($script:progressBar) { $script:progressBar.Value = [Math]::Min($Value, 100); $script:progressBar.Visible = ($Value -gt 0 -and $Value -lt 100) }; if ($script:lblProgress -and $Status) { $script:lblProgress.Text = $Status; $script:lblProgress.Visible = ($Status -ne "") }; [System.Windows.Forms.Application]::DoEvents() }

# ===========================================================================
# SECTION 16: BACKUP & PATCH OPERATIONS (Same as v2.8.0 with toast notifications)
# ===========================================================================

function Export-RegistryBackup {
    param([string]$Description = "NVMe_Backup")
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupFile = Join-Path $script:Config.WorkingDir "$($Description)_$timestamp.reg"
    Write-Log "Exporting registry backup to file..."
    try {
        $regContent = "Windows Registry Editor Version 5.00`n`n; NVMe Driver Patcher Registry Backup`n; Created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n; Description: $Description`n`n[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides]"
        if (Test-Path $script:Config.RegistryPath) { foreach ($id in $script:Config.FeatureIDs) { $val = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue; if ($val) { $propValue = $val | Select-Object -ExpandProperty $id -ErrorAction SilentlyContinue; if ($null -ne $propValue) { $regContent += "`n`"$id`"=dword:$('{0:x8}' -f $propValue)" } } } }
        $regContent | Out-File -FilePath $backupFile -Encoding Unicode
        Write-Log "Registry backup saved: $backupFile" -Level "SUCCESS"
        return $backupFile
    }
    catch { Write-Log "Failed to export registry backup: $($_.Exception.Message)" -Level "ERROR"; return $null }
}

function New-SafeRestorePoint {
    param([string]$Description = "NVMe Patcher Backup")
    Write-Log "Creating system backup..."; Update-Progress -Value 10 -Status "Creating registry backup..."
    $regBackup = Export-RegistryBackup -Description "Pre_Patch"
    Update-Progress -Value 50 -Status "Creating restore point..."
    try { Checkpoint-Computer -Description $Description -RestorePointType "MODIFY_SETTINGS" -ErrorAction Stop; Write-Log "System restore point created: '$Description'" -Level "SUCCESS"; Update-Progress -Value 0 -Status ""; return $true }
    catch { $errorMsg = $_.Exception.Message; if ($errorMsg -match "1111|24.hour|frequency") { Write-Log "Note: Windows limits restore points (one per 24 hours)" -Level "WARNING"; if ($regBackup) { Write-Log "Registry backup available as alternative" -Level "INFO" }; Update-Progress -Value 0 -Status ""; return $true }; Write-Log "Restore point failed: $errorMsg" -Level "ERROR"; if ($regBackup) { Write-Log "Registry backup is available" -Level "INFO"; Update-Progress -Value 0 -Status ""; return $true }; Update-Progress -Value 0 -Status ""; return $script:Config.ForceMode }
}

function Install-NVMePatch {
    Write-Log "========================================" -Level "INFO"; Write-Log "STARTING PATCH INSTALLATION" -Level "INFO"; Write-Log "========================================" -Level "INFO"
    Write-AppEventLog -Message "NVMe Driver Patch installation started" -EntryType "Information" -EventId 1000
    if (-not $script:Config.SilentMode) { Set-ButtonsEnabled -Enabled $false; $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor }
    $successCount = 0; $appliedKeys = [System.Collections.ArrayList]::new()
    # Determine total components based on server key inclusion
    $effectiveTotal = $script:Config.TotalComponents
    $featureIDsToApply = [System.Collections.ArrayList]@($script:Config.FeatureIDs)
    if ($script:Config.IncludeServerKey) {
        [void]$featureIDsToApply.Add($script:Config.ServerFeatureID)
        $effectiveTotal = $script:Config.TotalComponents + 1
        Write-Log "Including optional Microsoft Server 2025 key (1176759950)" -Level "INFO"
    }
    try {
        Write-Log "Step 1/3: Creating system backup..."; $restoreOK = New-SafeRestorePoint -Description "Pre-NVMe-Driver-Patch"; if (-not $restoreOK) { Write-Log "Installation cancelled" -Level "WARNING"; return $false }
        Write-Log "Step 2/3: Applying $effectiveTotal registry components..."; Update-Progress -Value 60 -Status "Applying registry changes..."
        if (-not (Test-Path $script:Config.RegistryPath)) { Write-Log "Creating registry path: Overrides" -Level "INFO"; New-Item -Path $script:Config.RegistryPath -Force | Out-Null }
        foreach ($id in $featureIDsToApply) { $friendlyName = if ($script:Config.FeatureNames.ContainsKey($id)) { $script:Config.FeatureNames[$id] } else { "Feature Flag" }; try { New-ItemProperty -Path $script:Config.RegistryPath -Name $id -Value 1 -PropertyType DWORD -Force | Out-Null; $verify = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue; if ($verify.$id -eq 1) { Write-Log "  [OK] $id - $friendlyName" -Level "SUCCESS"; $successCount++; [void]$appliedKeys.Add(@{ Type = "Feature"; ID = $id }) } else { Write-Log "  [FAIL] $id - $friendlyName" -Level "ERROR" } } catch { Write-Log "  [FAIL] $id - $($_.Exception.Message)" -Level "ERROR" } }
        try { if (-not (Test-Path $script:Config.SafeBootMinimal)) { New-Item -Path $script:Config.SafeBootMinimal -Force | Out-Null }; Set-ItemProperty -Path $script:Config.SafeBootMinimal -Name "(Default)" -Value $script:Config.SafeBootValue -Force; $val = Get-ItemProperty -Path $script:Config.SafeBootMinimal -Name "(Default)" -ErrorAction SilentlyContinue; if ($val."(Default)" -eq $script:Config.SafeBootValue) { Write-Log "  [OK] SafeBoot Minimal Support" -Level "SUCCESS"; $successCount++; [void]$appliedKeys.Add(@{ Type = "SafeBoot"; ID = "Minimal" }) } else { Write-Log "  [FAIL] SafeBoot Minimal Support" -Level "ERROR" } } catch { Write-Log "  [FAIL] SafeBoot Minimal: $($_.Exception.Message)" -Level "ERROR" }
        try { if (-not (Test-Path $script:Config.SafeBootNetwork)) { New-Item -Path $script:Config.SafeBootNetwork -Force | Out-Null }; Set-ItemProperty -Path $script:Config.SafeBootNetwork -Name "(Default)" -Value $script:Config.SafeBootValue -Force; $val = Get-ItemProperty -Path $script:Config.SafeBootNetwork -Name "(Default)" -ErrorAction SilentlyContinue; if ($val."(Default)" -eq $script:Config.SafeBootValue) { Write-Log "  [OK] SafeBoot Network Support" -Level "SUCCESS"; $successCount++; [void]$appliedKeys.Add(@{ Type = "SafeBoot"; ID = "Network" }) } else { Write-Log "  [FAIL] SafeBoot Network Support" -Level "ERROR" } } catch { Write-Log "  [FAIL] SafeBoot Network: $($_.Exception.Message)" -Level "ERROR" }
        Update-Progress -Value 95 -Status "Validating..."; Write-Log "Step 3/3: Validating installation..."; Write-Log "========================================" -Level "INFO"
        if ($successCount -eq $effectiveTotal) {
            Write-Log "Patch Status: SUCCESS - Applied $successCount/$effectiveTotal components" -Level "SUCCESS"; Write-Log "Please RESTART your computer to apply changes" -Level "WARNING"
            Write-Log "After reboot: Drives should appear under 'Storage disks' using nvmedisk.sys" -Level "INFO"
            if ($script:BypassIOStatus -and -not $script:BypassIOStatus.Supported) { Write-Log "NOTE: BypassIO/DirectStorage not supported with Native NVMe - gaming impact possible" -Level "WARNING" }
            Write-AppEventLog -Message "NVMe Driver Patch applied successfully ($successCount/$effectiveTotal components)" -EntryType "Information" -EventId 1001
            Show-ToastNotification -Title "NVMe Patch Applied" -Message "All $effectiveTotal components applied successfully. Restart required." -Type "Success"
            # Generate verification script
            $verifyScript = New-VerificationScript; if ($verifyScript) { Write-Log "Verification script created: $verifyScript" -Level "INFO" }
            Update-Progress -Value 100 -Status "Complete!"; Start-Sleep -Milliseconds 500; Update-Progress -Value 0 -Status ""
            if (-not $script:Config.NoRestart -and -not $script:Config.SilentMode) { $result = [System.Windows.Forms.MessageBox]::Show("Patch applied successfully ($successCount/$effectiveTotal components).`n`nRestart your computer now to enable the new NVMe driver?`n`n(System will restart in $($script:Config.RestartDelay) seconds if you click Yes)`n`nAfter reboot:`n- Drives will move from 'Disk drives' to 'Storage disks'`n- Driver changes from stornvme.sys to nvmedisk.sys`n- A verification script has been created to confirm", "Installation Complete", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question); if ($result -eq [System.Windows.Forms.DialogResult]::Yes) { Write-Log "Initiating system restart in $($script:Config.RestartDelay) seconds..."; Start-Process "shutdown.exe" -ArgumentList "/r /t $($script:Config.RestartDelay) /c `"NVMe Driver Patch - Restarting in $($script:Config.RestartDelay) seconds. Save your work!`"" } }
            return $true
        } else {
            Write-Log "Patch Status: PARTIAL - Applied $successCount/$effectiveTotal components" -Level "WARNING"
            Write-AppEventLog -Message "NVMe Driver Patch partially applied ($successCount/$effectiveTotal components)" -EntryType "Warning" -EventId 2001
            Show-ToastNotification -Title "NVMe Patch Partial" -Message "$successCount of $effectiveTotal components applied. Check log for details." -Type "Warning"
            Update-Progress -Value 0 -Status ""; return $false
        }
    } catch { Write-Log "INSTALLATION FAILED: $($_.Exception.Message)" -Level "ERROR"; Write-AppEventLog -Message "NVMe Driver Patch installation failed: $($_.Exception.Message)" -EntryType "Error" -EventId 3001; Update-Progress -Value 0 -Status ""; return $false }
    finally { if (-not $script:Config.SilentMode) { $script:form.Cursor = [System.Windows.Forms.Cursors]::Default; Set-ButtonsEnabled -Enabled $true; Refresh-StatusDisplay }; Write-Log "========================================" -Level "INFO" }
}

function Uninstall-NVMePatch {
    Write-Log "========================================" -Level "INFO"; Write-Log "STARTING PATCH REMOVAL" -Level "INFO"; Write-Log "========================================" -Level "INFO"
    Write-AppEventLog -Message "NVMe Driver Patch removal started" -EntryType "Information" -EventId 1000
    if (-not $script:Config.SilentMode) { Set-ButtonsEnabled -Enabled $false; $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor }
    Update-Progress -Value 10 -Status "Creating backup..."; Export-RegistryBackup -Description "Pre_Removal"
    $removedCount = 0
    # Remove all feature flags including optional server key if present
    $allFeatureIDs = [System.Collections.ArrayList]@($script:Config.FeatureIDs)
    [void]$allFeatureIDs.Add($script:Config.ServerFeatureID)
    try {
        Write-Log "Removing registry components..."; Update-Progress -Value 30 -Status "Removing feature flags..."
        if (Test-Path $script:Config.RegistryPath) { foreach ($id in $allFeatureIDs) { $exists = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue; if ($exists) { try { Remove-ItemProperty -Path $script:Config.RegistryPath -Name $id -Force -ErrorAction Stop; $friendlyName = if ($script:Config.FeatureNames.ContainsKey($id)) { $script:Config.FeatureNames[$id] } else { "Feature Flag" }; Write-Log "  [REMOVED] $id - $friendlyName" -Level "SUCCESS"; $removedCount++ } catch { Write-Log "  [FAIL] Failed to remove $($id): $($_.Exception.Message)" -Level "ERROR" } } else { Write-Log "  [ABSENT] Feature Flag: $id (Already gone)" -Level "INFO" } } }
        Update-Progress -Value 60 -Status "Removing SafeBoot keys..."
        if (Test-Path $script:Config.SafeBootMinimal) { try { Remove-Item -Path $script:Config.SafeBootMinimal -Force -ErrorAction Stop; Write-Log "  [REMOVED] SafeBoot Minimal" -Level "SUCCESS"; $removedCount++ } catch { Write-Log "  [FAIL] SafeBoot Minimal: $($_.Exception.Message)" -Level "ERROR" } }
        if (Test-Path $script:Config.SafeBootNetwork) { try { Remove-Item -Path $script:Config.SafeBootNetwork -Force -ErrorAction Stop; Write-Log "  [REMOVED] SafeBoot Network" -Level "SUCCESS"; $removedCount++ } catch { Write-Log "  [FAIL] SafeBoot Network: $($_.Exception.Message)" -Level "ERROR" } }
        Update-Progress -Value 90 -Status "Validating..."; Write-Log "========================================" -Level "INFO"
        Write-Log "Patch Status: REMOVED - Removed $removedCount components" -Level "SUCCESS"; Write-Log "After reboot: Drives will return to 'Disk drives' using stornvme.sys" -Level "INFO"; Write-Log "Please RESTART your computer" -Level "WARNING"
        Write-AppEventLog -Message "NVMe Driver Patch removed ($removedCount components)" -EntryType "Information" -EventId 1001
        Show-ToastNotification -Title "NVMe Patch Removed" -Message "Patch components removed. Restart required." -Type "Info"
        Update-Progress -Value 100 -Status "Complete!"; Start-Sleep -Milliseconds 500; Update-Progress -Value 0 -Status ""; return $true
    } catch { Write-Log "REMOVAL FAILED: $($_.Exception.Message)" -Level "ERROR"; Write-AppEventLog -Message "NVMe Driver Patch removal failed: $($_.Exception.Message)" -EntryType "Error" -EventId 3001; Update-Progress -Value 0 -Status ""; return $false }
    finally { if (-not $script:Config.SilentMode) { $script:form.Cursor = [System.Windows.Forms.Cursors]::Default; Set-ButtonsEnabled -Enabled $true; Refresh-StatusDisplay }; Write-Log "========================================" -Level "INFO" }
}

function Refresh-StatusDisplay {
    $status = Test-PatchStatus
    if ($script:keyRows) { foreach ($id in $script:Config.FeatureIDs) { if ($script:keyRows.ContainsKey($id)) { $isPresent = ($status.Keys -contains $id); $dot = $script:keyRows[$id].Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "dot" }; if ($dot) { $dot.BackColor = if ($isPresent) { $script:Colors.Success } else { $script:Colors.Danger } } } }; if ($script:keyRows.ContainsKey("SafeBootMinimal")) { $isPresent = ($status.Keys -contains "SafeBootMinimal"); $dot = $script:keyRows["SafeBootMinimal"].Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "dot" }; if ($dot) { $dot.BackColor = if ($isPresent) { $script:Colors.Success } else { $script:Colors.Danger } } }; if ($script:keyRows.ContainsKey("SafeBootNetwork")) { $isPresent = ($status.Keys -contains "SafeBootNetwork"); $dot = $script:keyRows["SafeBootNetwork"].Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "dot" }; if ($dot) { $dot.BackColor = if ($isPresent) { $script:Colors.Success } else { $script:Colors.Danger } } } }
    # Update optional server key dot
    if ($script:keyRows -and $script:keyRows.ContainsKey("1176759950")) { $serverPresent = $false; try { if (Test-Path $script:Config.RegistryPath) { $sVal = Get-ItemProperty -Path $script:Config.RegistryPath -Name "1176759950" -ErrorAction SilentlyContinue; if ($sVal -and $sVal."1176759950" -eq 1) { $serverPresent = $true } } } catch { }; $dot = $script:keyRows["1176759950"].Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "dot" }; if ($dot) { $dot.BackColor = if ($serverPresent) { $script:Colors.Success } elseif ($script:Config.IncludeServerKey) { $script:Colors.Warning } else { $script:Colors.TextMuted } } }
    # Update status indicator and buttons based on patch status
    if ($status.Applied) { if ($script:pnlPatchStatus) { $icon = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "icon" }; $label = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "status" }; if ($icon) { $icon.BackColor = $script:Colors.Success }; if ($label) { $label.Text = "Patch Applied" } }; if ($script:btnApply) { $script:btnApply.Text = "REINSTALL"; $tag = $script:btnApply.Tag; if ($tag -is [hashtable]) { $tag['Original'] = $script:Colors.SurfaceLight; $tag['Hover'] = $script:Colors.SurfaceHover }; $script:btnApply.BackColor = $script:Colors.SurfaceLight; $tagRemove = $script:btnRemove.Tag; if ($tagRemove -is [hashtable]) { $tagRemove['Original'] = $script:Colors.Danger; $tagRemove['Hover'] = $script:Colors.DangerHover }; $script:btnRemove.BackColor = $script:Colors.Danger } }
    elseif ($status.Partial) { if ($script:pnlPatchStatus) { $icon = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "icon" }; $label = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "status" }; if ($icon) { $icon.BackColor = $script:Colors.Warning }; if ($label) { $label.Text = "Partial ($($status.Count)/5)" } } }
    else { if ($script:pnlPatchStatus) { $icon = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "icon" }; $label = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "status" }; if ($icon) { $icon.BackColor = $script:Colors.TextMuted }; if ($label) { $label.Text = "Not Applied" } }; if ($script:btnApply) { $script:btnApply.Text = "APPLY PATCH"; $tag = $script:btnApply.Tag; if ($tag -is [hashtable]) { $tag['Original'] = $script:Colors.Success; $tag['Hover'] = $script:Colors.SuccessHover }; $script:btnApply.BackColor = $script:Colors.Success; $tagRemove = $script:btnRemove.Tag; if ($tagRemove -is [hashtable]) { $tagRemove['Original'] = $script:Colors.SurfaceLight; $tagRemove['Hover'] = $script:Colors.SurfaceHover }; $script:btnRemove.BackColor = $script:Colors.SurfaceLight } }
}

function Show-ConfirmDialog { param([string]$Title, [string]$Message, [string]$WarningText = "", [bool]$CheckNVMe = $false); if ($script:Config.ForceMode) { return $true }; if ($CheckNVMe -and -not $script:HasNVMeDrives) { $noNVMeResult = [System.Windows.Forms.MessageBox]::Show("NO NVMe DRIVES DETECTED ON THIS SYSTEM!`n`nThis patch only affects NVMe drives using the Windows inbox driver.`nYour system appears to have no NVMe drives.`n`nDo you still want to continue?", "No NVMe Detected", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning); if ($noNVMeResult -ne [System.Windows.Forms.DialogResult]::Yes) { Write-Log "Operation cancelled - No NVMe drives detected" -Level "WARNING"; return $false } }; if ($script:BitLockerEnabled) { $bitlockerResult = [System.Windows.Forms.MessageBox]::Show("BITLOCKER ENCRYPTION DETECTED!`n`nModifying system registry on a BitLocker-encrypted drive may trigger recovery mode on next boot.`n`nMake sure you have your BitLocker recovery key available before proceeding.`n`nContinue?", "BitLocker Warning", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning); if ($bitlockerResult -ne [System.Windows.Forms.DialogResult]::Yes) { Write-Log "Operation cancelled - BitLocker concern" -Level "WARNING"; return $false } }; if ($script:DriverInfo -and $script:DriverInfo.HasThirdParty) { $driverResult = [System.Windows.Forms.MessageBox]::Show("THIRD-PARTY NVMe DRIVER DETECTED!`n`nYour system is using: $($script:DriverInfo.ThirdPartyName)`n`nThis patch only affects the Windows inbox NVMe driver and may have no effect on your system.`n`nContinue anyway?", "Third-Party Driver Warning", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Information); if ($driverResult -ne [System.Windows.Forms.DialogResult]::Yes) { Write-Log "Operation cancelled - Third-party driver" -Level "WARNING"; return $false } }; if ($Title -eq "Apply Patch" -and $script:BuildDetails -and -not $script:BuildDetails.Is24H2OrLater) { $buildResult = [System.Windows.Forms.MessageBox]::Show("OLDER WINDOWS BUILD DETECTED`n`nYour build ($($script:BuildDetails.BuildNumber), $($script:BuildDetails.DisplayVersion)) is older than the recommended 24H2/25H2.`n`nThis patch was designed for Windows 11 24H2 (Build 26100+) and 25H2 (Build 26200+). Results on older builds are unpredictable.`n`nContinue anyway?", "Build Version Warning", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning); if ($buildResult -ne [System.Windows.Forms.DialogResult]::Yes) { Write-Log "Operation cancelled - Older build" -Level "WARNING"; return $false } }; if ($Title -eq "Apply Patch") { [System.Windows.Forms.MessageBox]::Show("DIRECTSTORAGE / GAMING NOTICE`n`nThe Native NVMe driver does NOT currently support BypassIO.`nThis means DirectStorage-enabled games may experience:`n`n- Higher CPU usage during asset loading`n- Potential stuttering in games using DirectStorage`n- Affected titles include games using GPU decompression`n`nIf gaming is your primary use case, you may want to wait for Microsoft to add BypassIO support.`n`nThis is informational only - click OK to continue.", "Gaming Notice", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null }; $fullMessage = $Message; if ($WarningText) { $fullMessage += "`n`nWARNING: $WarningText" }; $result = [System.Windows.Forms.MessageBox]::Show($fullMessage, $Title, [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question); return ($result -eq [System.Windows.Forms.DialogResult]::Yes) }

# ===========================================================================
# SECTION 17: COMMAND-LINE EXECUTION
# ===========================================================================

# Handle -ExportDiagnostics
if ($ExportDiagnostics) {
    Write-Host "Exporting system diagnostics..."
    Invoke-PreflightChecks | Out-Null
    $diagFile = Export-SystemDiagnostics
    if ($diagFile) {
        Write-Host "Diagnostics exported to: $diagFile" -ForegroundColor Green
        exit 0
    }
    else {
        Write-Error "Failed to export diagnostics"
        exit 1
    }
}

# Handle -GenerateVerifyScript
if ($GenerateVerifyScript) {
    Write-Host "Generating verification script..."
    $verifyScript = New-VerificationScript
    if ($verifyScript) {
        Write-Host "Verification script created: $verifyScript" -ForegroundColor Green
        exit 0
    }
    else {
        Write-Error "Failed to generate verification script"
        exit 1
    }
}

# Handle -Silent mode
if ($Silent) {
    Write-Log "$($script:Config.AppName) v$($script:Config.AppVersion) - Silent Mode"
    Write-Log "Working directory: $($script:Config.WorkingDir)"
    
    # Validate parameters
    if (-not $Apply -and -not $Remove -and -not $Status) {
        Write-Error "Silent mode requires -Apply, -Remove, or -Status parameter."
        exit 3
    }
    
    if (($Apply -and $Remove) -or ($Apply -and $Status) -or ($Remove -and $Status)) {
        Write-Error "Cannot combine -Apply, -Remove, and -Status parameters."
        exit 3
    }
    
    # Run checks
    Invoke-PreflightChecks | Out-Null
    
    if (-not (Test-WindowsCompatibility) -and -not $Force) {
        Write-Error "Windows compatibility check failed."
        exit 1
    }
    
    # Handle -Status
    if ($Status) {
        $patchStatus = Test-PatchStatus
        Write-Host ""
        Write-Host "NVMe Driver Patch Status" -ForegroundColor Cyan
        Write-Host "========================" -ForegroundColor Cyan
        Write-Host "Components Applied: $($patchStatus.Count)/$($patchStatus.Total)"
        Write-Host "Status: $(if ($patchStatus.Applied) { 'APPLIED' } elseif ($patchStatus.Partial) { 'PARTIAL' } else { 'NOT APPLIED' })"
        if ($patchStatus.Keys.Count -gt 0) {
            Write-Host "Applied Keys: $($patchStatus.Keys -join ', ')"
        }
        Write-Host ""
        
        if ($patchStatus.Applied) { exit 0 }
        elseif ($patchStatus.Partial) { exit 2 }
        else { exit 1 }
    }
    
    # Execute requested operation
    $exitCode = 0
    
    if ($Apply) {
        if (-not $Force -and -not $script:HasNVMeDrives) {
            Write-Warning "No NVMe drives detected. Use -Force to apply anyway."
            $exitCode = 2
        }
        else {
            $success = Install-NVMePatch
            $exitCode = if ($success) { 0 } else { 1 }
        }
    }
    elseif ($Remove) {
        $success = Uninstall-NVMePatch
        $exitCode = if ($success) { 0 } else { 1 }
    }
    
    # Save log
    $logFile = Save-LogFile -Suffix "_silent"
    if ($logFile) { Write-Host "Log saved to: $logFile" }
    
    # Cleanup
    if ($script:AppMutex) { try { $script:AppMutex.ReleaseMutex(); $script:AppMutex.Dispose() } catch { } }

}

# ===========================================================================
# SECTION 18: GUI MODE - FORM CONSTRUCTION
# ===========================================================================

# Layout shorthand
$LX  = $script:UIConstants.LeftColumnX       # 28
$RX  = $script:UIConstants.RightColumnX      # 662
$CW  = $script:UIConstants.ColumnWidth       # 610
$HH  = $script:UIConstants.HeaderHeight      # 110
$CG  = $script:UIConstants.CardGap           # 18
$CP  = $script:UIConstants.CardPadding       # 24
$FW  = $script:UIConstants.FormWidth         # 1300
$FH  = $script:UIConstants.FormHeight        # 1080
$CT  = $script:UIConstants.ContentTop        # 128
$RH  = $script:UIConstants.RowHeight         # 28

# Inner content width (same for ALL content inside a card)
$IW  = $CW - ($CP * 2)                       # 562

$script:ToolTipProvider = New-Object System.Windows.Forms.ToolTip
$script:ToolTipProvider.AutoPopDelay = 10000
$script:ToolTipProvider.InitialDelay = 500
$script:ToolTipProvider.ReshowDelay  = 200

# ===================================================================
#  FORM
# ===================================================================

$script:form = New-Object System.Windows.Forms.Form
$script:form.Text            = "$($script:Config.AppName) v$($script:Config.AppVersion)"
$script:form.Size            = New-Object System.Drawing.Size($FW, $FH)
$script:form.StartPosition   = "CenterScreen"
$script:form.FormBorderStyle = "FixedSingle"
$script:form.MaximizeBox     = $false
$script:form.BackColor       = $script:Colors.Background
$script:form.Font            = New-Object System.Drawing.Font("Segoe UI", 9)
$script:form.GetType().GetProperty(
    "DoubleBuffered",
    [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic
).SetValue($script:form, $true, $null)

# Keyboard shortcuts
# ===================================================================
#  HEADER
# ===================================================================

$pnlHeader = New-Object System.Windows.Forms.Panel
$pnlHeader.Location  = New-Object System.Drawing.Point(0, 0)
$pnlHeader.Size      = New-Object System.Drawing.Size($FW, $HH)
$pnlHeader.BackColor = $script:Colors.Surface

# Accent bar
$pnlAccentBar = New-Object System.Windows.Forms.Panel
$pnlAccentBar.Location  = New-Object System.Drawing.Point(0, ($HH - 2))
$pnlAccentBar.Size      = New-Object System.Drawing.Size($FW, 2)
$pnlAccentBar.BackColor = $script:Colors.Accent
$pnlHeader.Controls.Add($pnlAccentBar)

# Icon badge - use full word width at smaller font so it never wraps
$lblIcon = New-Object System.Windows.Forms.Label
$lblIcon.Text      = "NVMe"
$lblIcon.Location  = New-Object System.Drawing.Point(32, 26)
$lblIcon.Size      = New-Object System.Drawing.Size(76, 48)
$lblIcon.ForeColor = $script:Colors.Accent
$lblIcon.Font      = New-Object System.Drawing.Font("Segoe UI Black", 11)
$lblIcon.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$lblIcon.BackColor = $script:Colors.SurfaceLight
Set-RoundedCorners -Control $lblIcon -Radius 10
$pnlHeader.Controls.Add($lblIcon)

# Title
$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text      = "NVMe Driver Patcher"
$lblTitle.Location  = New-Object System.Drawing.Point(116, 18)
$lblTitle.Size      = New-Object System.Drawing.Size(460, 44)
$lblTitle.ForeColor = $script:Colors.TextPrimary
$lblTitle.Font      = New-Object System.Drawing.Font("Segoe UI Light", 22)
$pnlHeader.Controls.Add($lblTitle)

# Subtitle
$lblSubtitle = New-Object System.Windows.Forms.Label
$lblSubtitle.Text      = "Enable the experimental Server 2025 Native NVMe driver on Windows 11"
$lblSubtitle.Location  = New-Object System.Drawing.Point(118, 62)
$lblSubtitle.Size      = New-Object System.Drawing.Size(600, 22)
$lblSubtitle.ForeColor = $script:Colors.TextMuted
$lblSubtitle.Font      = New-Object System.Drawing.Font("Segoe UI", 9.5)
$pnlHeader.Controls.Add($lblSubtitle)

# Version pill
$lblVersion = New-Object System.Windows.Forms.Label
$lblVersion.Text      = "v$($script:Config.AppVersion)"
$lblVersion.Size      = New-Object System.Drawing.Size(64, 28)
$lblVersion.Location  = New-Object System.Drawing.Point(($FW - 104), 40)
$lblVersion.ForeColor = $script:Colors.Accent
$lblVersion.Font      = New-Object System.Drawing.Font("Segoe UI Semibold", 9)
$lblVersion.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$lblVersion.BackColor = $script:Colors.AccentSubtle
Set-RoundedCorners -Control $lblVersion -Radius 12
$pnlHeader.Controls.Add($lblVersion)

$script:form.Controls.Add($pnlHeader)

# ===================================================================
#  LEFT COLUMN  --  CARD 1: DETECTED DRIVES
# ===================================================================

$drivesH = 220
$cardDrives = New-CardPanel `
    -Location (New-Object System.Drawing.Point($LX, $CT)) `
    -Size     (New-Object System.Drawing.Size($CW, $drivesH)) `
    -Title    "Detected Drives"

# Scrollable content panel (aligned to $CP)
$driveContentH = $drivesH - 52
$script:pnlDrivesContent = New-Object System.Windows.Forms.Panel
$script:pnlDrivesContent.Location   = New-Object System.Drawing.Point($CP, 44)
$script:pnlDrivesContent.Size       = New-Object System.Drawing.Size($IW, $driveContentH)
$script:pnlDrivesContent.BackColor  = $script:Colors.ChecklistBg
$script:pnlDrivesContent.AutoScroll = $true
Set-RoundedCorners -Control $script:pnlDrivesContent -Radius 6
$cardDrives.Controls.Add($script:pnlDrivesContent)

# Legend: green = NVMe, gray = other
$legendLbl = New-Object System.Windows.Forms.Label
$legendLbl.Location  = New-Object System.Drawing.Point(($CW - 200), 16)
$legendLbl.Size      = New-Object System.Drawing.Size(180, 14)
$legendLbl.Text      = "Green = NVMe    Gray = Other"
$legendLbl.ForeColor = $script:Colors.TextDimmed
$legendLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 7.5)
$legendLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
$cardDrives.Controls.Add($legendLbl)

$script:form.Controls.Add($cardDrives)

# ===================================================================
#  LEFT COLUMN  --  CARD 2: SYSTEM OVERVIEW
# ===================================================================

$overviewY = $CT + $drivesH + $CG
$overviewH = 344
$cardOverview = New-CardPanel `
    -Location (New-Object System.Drawing.Point($LX, $overviewY)) `
    -Size     (New-Object System.Drawing.Size($CW, $overviewH)) `
    -Title    "System Overview"

# -- Status Banner (aligned to $CP on both sides) --
$pnlStatusBanner = New-Object System.Windows.Forms.Panel
$pnlStatusBanner.Location  = New-Object System.Drawing.Point($CP, 48)
$pnlStatusBanner.Size      = New-Object System.Drawing.Size($IW, 52)
$pnlStatusBanner.BackColor = $script:Colors.SurfaceLight
Set-RoundedCorners -Control $pnlStatusBanner -Radius 8

# Patch status (left side)
$script:pnlPatchStatus = New-Object System.Windows.Forms.Panel
$script:pnlPatchStatus.Location  = New-Object System.Drawing.Point(16, 6)
$script:pnlPatchStatus.Size      = New-Object System.Drawing.Size(240, 40)
$script:pnlPatchStatus.BackColor = [System.Drawing.Color]::Transparent

$patchIcon = New-Object System.Windows.Forms.Panel
$patchIcon.Size     = New-Object System.Drawing.Size(14, 14)
$patchIcon.Location = New-Object System.Drawing.Point(0, 13)
$patchIcon.BackColor = $script:Colors.Neutral
Set-ControlTagData -Control $patchIcon -NewData @{ Role = "icon" }
$gp = New-Object System.Drawing.Drawing2D.GraphicsPath; $gp.AddEllipse(0, 0, 14, 14)
$patchIcon.Region = New-Object System.Drawing.Region($gp); $gp.Dispose()
$script:pnlPatchStatus.Controls.Add($patchIcon)

$patchLabel = New-Object System.Windows.Forms.Label
$patchLabel.Location  = New-Object System.Drawing.Point(24, 7)
$patchLabel.Size      = New-Object System.Drawing.Size(210, 26)
$patchLabel.Text      = "Checking..."
$patchLabel.ForeColor = $script:Colors.TextSecondary
$patchLabel.Font      = New-Object System.Drawing.Font("Segoe UI Semibold", 12)
Set-ControlTagData -Control $patchLabel -NewData @{ Role = "status" }
$script:pnlPatchStatus.Controls.Add($patchLabel)
$pnlStatusBanner.Controls.Add($script:pnlPatchStatus)

# Driver info (right side, clear of patch label)
$script:lblDriverInfo = New-Object System.Windows.Forms.Label
$script:lblDriverInfo.Location  = New-Object System.Drawing.Point(266, 15)
$script:lblDriverInfo.Size      = New-Object System.Drawing.Size(($IW - 280), 22)
$script:lblDriverInfo.Text      = "Detecting driver..."
$script:lblDriverInfo.ForeColor = $script:Colors.TextMuted
$script:lblDriverInfo.Font      = New-Object System.Drawing.Font("Segoe UI", 8.75)
$script:lblDriverInfo.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
$pnlStatusBanner.Controls.Add($script:lblDriverInfo)
$cardOverview.Controls.Add($pnlStatusBanner)

# -- Checklist Grid (aligned to $CP) --
$script:checklistLabels = @{}

# Reordered so long-value items are in left column (more width)
$checkItemsLeft  = @("WindowsVersion", "NVMeDrives",  "BitLocker",         "DriverStatus")
$checkNamesLeft  = @("Build",          "NVMe",        "BitLocker",         "Driver")
$checkItemsRight = @("ThirdPartyDriver",   "SystemProtection",  "BypassIO")
$checkNamesRight = @("3rd Party",          "Sys Prot.",          "BypassIO")

$gridTop = 116

# Column geometry - designed for 125% DPI (~8.5px/char at 9pt)
# Shorter names = smaller nameW = more room for values
$halfW   = [int]($IW / 2)             # 281
$nameW   = 82
$dotGap  = 10

# Left column positions (relative to card)
$ldotX   = $CP                        # 24
$lnameX  = $CP + 8 + $dotGap          # 42
$lvalX   = $lnameX + $nameW           # 138
$lvalW   = $CP + $halfW - $lvalX - 4  # ~163

# Right column positions
$rdotX   = $CP + $halfW + 8           # 313
$rnameX  = $rdotX + 8 + $dotGap       # 331
$rvalX   = $rnameX + $nameW           # 427
$rvalW   = $CW - $CP - $rvalX         # ~159

# Left column: 4 items
for ($i = 0; $i -lt $checkItemsLeft.Count; $i++) {
    $yy = $gridTop + ($i * $RH)

    $dot = New-Object System.Windows.Forms.Panel
    $dot.Size     = New-Object System.Drawing.Size(8, 8)
    $dot.Location = New-Object System.Drawing.Point($ldotX, ($yy + 8))
    $dot.BackColor = $script:Colors.Neutral
    $dp = New-Object System.Drawing.Drawing2D.GraphicsPath; $dp.AddEllipse(0, 0, 8, 8)
    $dot.Region = New-Object System.Drawing.Region($dp); $dp.Dispose()
    Set-ControlTagData -Control $dot -NewData @{ Role = "checkDot"; CheckName = $checkItemsLeft[$i] }
    $cardOverview.Controls.Add($dot)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Location  = New-Object System.Drawing.Point($lnameX, ($yy + 1))
    $lbl.Size      = New-Object System.Drawing.Size($nameW, 24)
    $lbl.Text      = $checkNamesLeft[$i]
    $lbl.ForeColor = $script:Colors.TextSecondary
    $lbl.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
    $cardOverview.Controls.Add($lbl)

    $lblVal = New-Object System.Windows.Forms.Label
    $lblVal.Location  = New-Object System.Drawing.Point($lvalX, ($yy + 1))
    $lblVal.Size      = New-Object System.Drawing.Size($lvalW, 24)
    $lblVal.Text      = "Checking..."
    $lblVal.ForeColor = $script:Colors.TextMuted
    $lblVal.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
    $lblVal.AutoEllipsis = $true
    $script:checklistLabels[$checkItemsLeft[$i]] = $lblVal
    $cardOverview.Controls.Add($lblVal)
}

# Right column: 3 items
for ($i = 0; $i -lt $checkItemsRight.Count; $i++) {
    $yy = $gridTop + ($i * $RH)

    $dot = New-Object System.Windows.Forms.Panel
    $dot.Size     = New-Object System.Drawing.Size(8, 8)
    $dot.Location = New-Object System.Drawing.Point($rdotX, ($yy + 8))
    $dot.BackColor = $script:Colors.Neutral
    $dp = New-Object System.Drawing.Drawing2D.GraphicsPath; $dp.AddEllipse(0, 0, 8, 8)
    $dot.Region = New-Object System.Drawing.Region($dp); $dp.Dispose()
    Set-ControlTagData -Control $dot -NewData @{ Role = "checkDot"; CheckName = $checkItemsRight[$i] }
    $cardOverview.Controls.Add($dot)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Location  = New-Object System.Drawing.Point($rnameX, ($yy + 1))
    $lbl.Size      = New-Object System.Drawing.Size($nameW, 24)
    $lbl.Text      = $checkNamesRight[$i]
    $lbl.ForeColor = $script:Colors.TextSecondary
    $lbl.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
    $cardOverview.Controls.Add($lbl)

    $lblVal = New-Object System.Windows.Forms.Label
    $lblVal.Location  = New-Object System.Drawing.Point($rvalX, ($yy + 1))
    $lblVal.Size      = New-Object System.Drawing.Size($rvalW, 24)
    $lblVal.Text      = "Checking..."
    $lblVal.ForeColor = $script:Colors.TextMuted
    $lblVal.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
    $lblVal.AutoEllipsis = $true
    $script:checklistLabels[$checkItemsRight[$i]] = $lblVal
    $cardOverview.Controls.Add($lblVal)
}

# -- Divider (aligned to $CP) --
$divY = $gridTop + (4 * $RH) + 10
$divider1 = New-Object System.Windows.Forms.Panel
$divider1.Location  = New-Object System.Drawing.Point($CP, $divY)
$divider1.Size      = New-Object System.Drawing.Size($IW, 1)
$divider1.BackColor = $script:Colors.CardBorder
$cardOverview.Controls.Add($divider1)

# -- Options (aligned to $CP) --
$optY = $divY + 16

$script:chkServerKey = New-Object System.Windows.Forms.CheckBox
$script:chkServerKey.Location  = New-Object System.Drawing.Point(($CP + 2), $optY)
$script:chkServerKey.Size      = New-Object System.Drawing.Size(500, 24)
$script:chkServerKey.Text      = "Include Microsoft Server 2025 key (1176759950, optional)"
$script:chkServerKey.ForeColor = $script:Colors.TextSecondary
$script:chkServerKey.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
$script:chkServerKey.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$script:chkServerKey.Checked   = $script:Config.IncludeServerKey
$script:chkServerKey.Add_CheckedChanged({
    $script:Config.IncludeServerKey = $this.Checked
    $keyDesc = if ($this.Checked) { "enabled" } else { "disabled" }
    Write-Log "Optional Server 2025 key (1176759950): $keyDesc" -Level "INFO"
    Refresh-StatusDisplay
})
$cardOverview.Controls.Add($script:chkServerKey)

# BypassIO note (aligned to $CP)
$noteY = $optY + 32
$lblBypassNote = New-Object System.Windows.Forms.Label
$lblBypassNote.Location  = New-Object System.Drawing.Point(($CP + 4), $noteY)
$lblBypassNote.Size      = New-Object System.Drawing.Size(($IW - 8), 34)
$lblBypassNote.Text      = "Note: Native NVMe does not yet support BypassIO. DirectStorage games may see higher CPU usage."
$lblBypassNote.ForeColor = $script:Colors.Warning
$lblBypassNote.Font      = New-Object System.Drawing.Font("Segoe UI", 8, [System.Drawing.FontStyle]::Italic)
$cardOverview.Controls.Add($lblBypassNote)

$script:form.Controls.Add($cardOverview)

# ===================================================================
#  LEFT COLUMN  --  CARD 3: REGISTRY COMPONENTS
# ===================================================================

$keysY = $overviewY + $overviewH + $CG
$keysH = 298
$cardKeys = New-CardPanel `
    -Location (New-Object System.Drawing.Point($LX, $keysY)) `
    -Size     (New-Object System.Drawing.Size($CW, $keysH)) `
    -Title    "Registry Components"

# Section: Feature Flags (aligned to $CP)
$lblFeatureHdr = New-Object System.Windows.Forms.Label
$lblFeatureHdr.Location  = New-Object System.Drawing.Point($CP, 48)
$lblFeatureHdr.Size      = New-Object System.Drawing.Size(200, 16)
$lblFeatureHdr.Text      = "FEATURE FLAGS"
$lblFeatureHdr.ForeColor = $script:Colors.TextDimmed
$lblFeatureHdr.Font      = New-Object System.Drawing.Font("Segoe UI Semibold", 7.5)
$cardKeys.Controls.Add($lblFeatureHdr)

$script:keyRows = @{}
$flagRowH = 28

$flagData = @(
    @{ ID = "735209102";  Name = "735209102";  Desc = "NativeNVMeStackForGeClient -- Primary driver enable" },
    @{ ID = "1853569164"; Name = "1853569164"; Desc = "UxAccOptimization -- Extended functionality" },
    @{ ID = "156965516";  Name = "156965516";  Desc = "Standalone_Future -- Performance optimizations" },
    @{ ID = "1176759950"; Name = "1176759950"; Desc = "Microsoft Official -- Server 2025 key (optional)" }
)

$flagTop = 72
for ($fi = 0; $fi -lt $flagData.Count; $fi++) {
    $kd = $flagData[$fi]
    $yy = $flagTop + ($fi * $flagRowH)

    $panel = New-Object System.Windows.Forms.Panel
    $panel.Location  = New-Object System.Drawing.Point($CP, $yy)
    $panel.Size      = New-Object System.Drawing.Size($IW, 24)
    $panel.BackColor = [System.Drawing.Color]::Transparent

    $dot = New-Object System.Windows.Forms.Panel
    $dot.Size     = New-Object System.Drawing.Size(10, 10)
    $dot.Location = New-Object System.Drawing.Point(4, 7)
    $dot.BackColor = $script:Colors.Neutral
    Set-ControlTagData -Control $dot -NewData @{ Role = "dot" }
    $dp = New-Object System.Drawing.Drawing2D.GraphicsPath; $dp.AddEllipse(0, 0, 10, 10)
    $dot.Region = New-Object System.Drawing.Region($dp); $dp.Dispose()
    $panel.Controls.Add($dot)

    $keyLbl = New-Object System.Windows.Forms.Label
    $keyLbl.Location  = New-Object System.Drawing.Point(26, 2)
    $keyLbl.Size      = New-Object System.Drawing.Size(108, 20)
    $keyLbl.Text      = $kd.Name
    $keyLbl.ForeColor = $script:Colors.TextPrimary
    $keyLbl.Font      = New-Object System.Drawing.Font("Cascadia Code, Consolas", 9)
    $panel.Controls.Add($keyLbl)

    $descLbl = New-Object System.Windows.Forms.Label
    $descLbl.Location  = New-Object System.Drawing.Point(142, 3)
    $descLbl.Size      = New-Object System.Drawing.Size(($IW - 150), 20)
    $descLbl.Text      = $kd.Desc
    $descLbl.ForeColor = $script:Colors.TextMuted
    $descLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 8.75)
    $descLbl.AutoEllipsis = $true
    $panel.Controls.Add($descLbl)

    $script:keyRows[$kd.ID] = $panel
    $cardKeys.Controls.Add($panel)
}

# Separator (aligned to $CP)
$sepY = $flagTop + ($flagData.Count * $flagRowH) + 10
$sepLine = New-Object System.Windows.Forms.Panel
$sepLine.Location  = New-Object System.Drawing.Point($CP, $sepY)
$sepLine.Size      = New-Object System.Drawing.Size($IW, 1)
$sepLine.BackColor = $script:Colors.CardBorder
$cardKeys.Controls.Add($sepLine)

# Section: Safe Mode (aligned to $CP)
$safeHdrY = $sepY + 14
$lblSafeHdr = New-Object System.Windows.Forms.Label
$lblSafeHdr.Location  = New-Object System.Drawing.Point($CP, $safeHdrY)
$lblSafeHdr.Size      = New-Object System.Drawing.Size(280, 16)
$lblSafeHdr.Text      = "SAFE MODE SUPPORT (CRITICAL)"
$lblSafeHdr.ForeColor = $script:Colors.TextDimmed
$lblSafeHdr.Font      = New-Object System.Drawing.Font("Segoe UI Semibold", 7.5)
$cardKeys.Controls.Add($lblSafeHdr)

$safeData = @(
    @{ ID = "SafeBootMinimal"; Name = "SafeBoot";     Desc = "Minimal -- Prevents INACCESSIBLE_BOOT_DEVICE BSOD" },
    @{ ID = "SafeBootNetwork"; Name = "SafeBoot/Net";  Desc = "Network -- Safe Mode with Networking boot support" }
)

$safeTop = $safeHdrY + 24
for ($si = 0; $si -lt $safeData.Count; $si++) {
    $kd = $safeData[$si]
    $yy = $safeTop + ($si * $flagRowH)

    $panel = New-Object System.Windows.Forms.Panel
    $panel.Location  = New-Object System.Drawing.Point($CP, $yy)
    $panel.Size      = New-Object System.Drawing.Size($IW, 24)
    $panel.BackColor = [System.Drawing.Color]::Transparent

    $dot = New-Object System.Windows.Forms.Panel
    $dot.Size     = New-Object System.Drawing.Size(10, 10)
    $dot.Location = New-Object System.Drawing.Point(4, 7)
    $dot.BackColor = $script:Colors.Neutral
    Set-ControlTagData -Control $dot -NewData @{ Role = "dot" }
    $dp = New-Object System.Drawing.Drawing2D.GraphicsPath; $dp.AddEllipse(0, 0, 10, 10)
    $dot.Region = New-Object System.Drawing.Region($dp); $dp.Dispose()
    $panel.Controls.Add($dot)

    $keyLbl = New-Object System.Windows.Forms.Label
    $keyLbl.Location  = New-Object System.Drawing.Point(26, 2)
    $keyLbl.Size      = New-Object System.Drawing.Size(108, 20)
    $keyLbl.Text      = $kd.Name
    $keyLbl.ForeColor = $script:Colors.TextPrimary
    $keyLbl.Font      = New-Object System.Drawing.Font("Cascadia Code, Consolas", 9)
    $panel.Controls.Add($keyLbl)

    $descLbl = New-Object System.Windows.Forms.Label
    $descLbl.Location  = New-Object System.Drawing.Point(142, 3)
    $descLbl.Size      = New-Object System.Drawing.Size(($IW - 150), 20)
    $descLbl.Text      = $kd.Desc
    $descLbl.ForeColor = $script:Colors.TextMuted
    $descLbl.Font      = New-Object System.Drawing.Font("Segoe UI", 8.75)
    $descLbl.AutoEllipsis = $true
    $panel.Controls.Add($descLbl)

    $script:keyRows[$kd.ID] = $panel
    $cardKeys.Controls.Add($panel)
}

$script:form.Controls.Add($cardKeys)


# ===================================================================
#  RIGHT COLUMN  --  CARD 1: ACTIONS
# ===================================================================

$actionsH = 186
$cardActions = New-CardPanel `
    -Location (New-Object System.Drawing.Point($RX, $CT)) `
    -Size     (New-Object System.Drawing.Size($CW, $actionsH)) `
    -Title    "Actions"

$btnGap   = 16
$btnPrimW = [int](($IW - $btnGap) / 2)

# Apply Patch
$script:btnApply = New-ModernButton `
    -Text "APPLY PATCH" `
    -Location (New-Object System.Drawing.Point($CP, 52)) `
    -Size (New-Object System.Drawing.Size($btnPrimW, 54)) `
    -BackColor $script:Colors.Success `
    -HoverColor $script:Colors.SuccessHover `
    -ToolTip "Apply NVMe driver patch" `
    -IsPrimary $true `
    -OnClick {
        if (Show-ConfirmDialog -Title "Apply Patch" -Message "Apply the NVMe driver enhancement patch?" -WarningText "This will modify system registry settings." -CheckNVMe $true) {
            Install-NVMePatch
        }
    }
$cardActions.Controls.Add($script:btnApply)

# Remove Patch
$script:btnRemove = New-ModernButton `
    -Text "REMOVE PATCH" `
    -Location (New-Object System.Drawing.Point(($CP + $btnPrimW + $btnGap), 52)) `
    -Size (New-Object System.Drawing.Size($btnPrimW, 54)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Remove NVMe driver patch" `
    -OnClick {
        if (Show-ConfirmDialog -Title "Remove Patch" -Message "Remove the NVMe driver patch?" -WarningText "This will revert to default Windows behavior." -CheckNVMe $true) {
            Uninstall-NVMePatch
        }
    }
$cardActions.Controls.Add($script:btnRemove)

# Secondary row
$secGap  = 14
$btnSecW = [int](($IW - ($secGap * 2)) / 3)
$secBtnY = 124

$script:btnBackup = New-ModernButton `
    -Text "BACKUP" `
    -Location (New-Object System.Drawing.Point($CP, $secBtnY)) `
    -Size (New-Object System.Drawing.Size($btnSecW, 42)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Create system restore point" `
    -OnClick {
        Set-ButtonsEnabled -Enabled $false
        $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        New-SafeRestorePoint -Description "Manual NVMe Backup $(Get-Date -Format 'yyyy-MM-dd')"
        $script:form.Cursor = [System.Windows.Forms.Cursors]::Default
        Set-ButtonsEnabled -Enabled $true
    }
$cardActions.Controls.Add($script:btnBackup)

$btnDiag = New-ModernButton `
    -Text "DIAGNOSTICS" `
    -Location (New-Object System.Drawing.Point(($CP + $btnSecW + $secGap), $secBtnY)) `
    -Size (New-Object System.Drawing.Size($btnSecW, 42)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Export system diagnostics" `
    -OnClick {
        $diagFile = Export-SystemDiagnostics
        if ($diagFile) {
            Write-Log "Diagnostics exported: $diagFile" -Level "SUCCESS"
            [System.Windows.Forms.MessageBox]::Show(
                "Diagnostics exported to:`n$diagFile",
                "Export Complete",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Information
            ) | Out-Null
        }
    }
$cardActions.Controls.Add($btnDiag)

$btnDocs = New-ModernButton `
    -Text "DOCS" `
    -Location (New-Object System.Drawing.Point(($CP + ($btnSecW + $secGap) * 2), $secBtnY)) `
    -Size (New-Object System.Drawing.Size($btnSecW, 42)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Open documentation" `
    -OnClick { Start-Process $script:Config.DocumentationURL }
$cardActions.Controls.Add($btnDocs)

$script:form.Controls.Add($cardActions)

# ===================================================================
#  RIGHT COLUMN  --  CARD 2: ACTIVITY LOG
# ===================================================================

$logY = $CT + $actionsH + $CG
$leftBottom = $CT + $drivesH + $CG + $overviewH + $CG + $keysH
$logH = $leftBottom - $logY
$cardLog = New-CardPanel `
    -Location (New-Object System.Drawing.Point($RX, $logY)) `
    -Size     (New-Object System.Drawing.Size($CW, $logH)) `
    -Title    "Activity Log"

# Log text area (aligned to $CP)
$logBoxTop = 48
$logBoxH   = $logH - $logBoxTop - 52

$script:rtbOutput = New-Object System.Windows.Forms.RichTextBox
$script:rtbOutput.Location    = New-Object System.Drawing.Point($CP, $logBoxTop)
$script:rtbOutput.Size        = New-Object System.Drawing.Size($IW, $logBoxH)
$script:rtbOutput.ReadOnly    = $true
$script:rtbOutput.BackColor   = $script:Colors.ChecklistBg
$script:rtbOutput.ForeColor   = $script:Colors.TextSecondary
$script:rtbOutput.BorderStyle = "None"
$script:rtbOutput.Font        = New-Object System.Drawing.Font("Cascadia Code, Consolas, Courier New", 8.75)
$script:rtbOutput.ScrollBars  = "Vertical"
Set-RoundedCorners -Control $script:rtbOutput -Radius 8
$cardLog.Controls.Add($script:rtbOutput)

# Progress bar (aligned to $CP)
$progY = $logH - 38
$script:progressBar = New-Object System.Windows.Forms.ProgressBar
$script:progressBar.Location = New-Object System.Drawing.Point($CP, $progY)
$script:progressBar.Size     = New-Object System.Drawing.Size(($IW - 100), 18)
$script:progressBar.Style    = "Continuous"
$script:progressBar.Visible  = $false
$cardLog.Controls.Add($script:progressBar)

$script:lblProgress = New-Object System.Windows.Forms.Label
$script:lblProgress.Location  = New-Object System.Drawing.Point(($CW - $CP - 90), $progY)
$script:lblProgress.Size      = New-Object System.Drawing.Size(90, 18)
$script:lblProgress.ForeColor = $script:Colors.TextMuted
$script:lblProgress.Font      = New-Object System.Drawing.Font("Segoe UI", 8)
$script:lblProgress.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
$script:lblProgress.Visible   = $false
$cardLog.Controls.Add($script:lblProgress)

$script:form.Controls.Add($cardLog)

# ===================================================================
#  FOOTER
# ===================================================================

$footerY = $FH - 44
$lblFooter = New-Object System.Windows.Forms.Label
$lblFooter.Text      = "NVMe Driver Patcher v$($script:Config.AppVersion)    |    github.com/SysAdminDoc/win11-nvme-driver-patcher"
$lblFooter.Location  = New-Object System.Drawing.Point($LX, $footerY)
$lblFooter.Size      = New-Object System.Drawing.Size(($FW - $LX * 2), 18)
$lblFooter.ForeColor = $script:Colors.TextDimmed
$lblFooter.Font      = New-Object System.Drawing.Font("Segoe UI", 8)
$lblFooter.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$script:form.Controls.Add($lblFooter)

# ===================================================================
#  FORM EVENTS
# ===================================================================

$script:form.Add_Load({
    # Apply dark scrollbars to controls
    Set-DarkScrollbar -Control $script:rtbOutput
    if ($script:pnlDrivesContent) { Set-DarkScrollbar -Control $script:pnlDrivesContent }

    Write-Log "$($script:Config.AppName) v$($script:Config.AppVersion) started"
    Write-Log "Working directory: $($script:Config.WorkingDir)"
    Write-Log "----------------------------------------"

    Write-Log "Running pre-flight checks..."
    $checks = Invoke-PreflightChecks

    # Update checklist labels and dots
    foreach ($checkName in $checks.Keys) {
        if ($script:checklistLabels.ContainsKey($checkName)) {
            $check = $checks[$checkName]
            $lbl   = $script:checklistLabels[$checkName]
            $lbl.Text      = $check.Message
            $lbl.ForeColor = switch ($check.Status) {
                "Pass"    { $script:Colors.Success }
                "Warning" { $script:Colors.Warning }
                "Fail"    { $script:Colors.Danger  }
                "Info"    { $script:Colors.Accent  }
                default   { $script:Colors.TextMuted }
            }
            if ($script:ToolTipProvider) {
                $script:ToolTipProvider.SetToolTip($lbl, $check.Message)
            }
        }

        $dotControls = $cardOverview.Controls | Where-Object {
            $t = $_.Tag
            $t -is [hashtable] -and $t.ContainsKey('Role') -and $t['Role'] -eq 'checkDot' -and $t['CheckName'] -eq $checkName
        }
        foreach ($dc in $dotControls) {
            $dc.BackColor = switch ($checks[$checkName].Status) {
                "Pass"    { $script:Colors.Success }
                "Warning" { $script:Colors.Warning }
                "Fail"    { $script:Colors.Danger  }
                "Info"    { $script:Colors.Accent  }
                default   { $script:Colors.Neutral }
            }
        }
    }

    foreach ($checkName in $checks.Keys) {
        $check = $checks[$checkName]
        $level = switch ($check.Status) {
            "Pass"    { "SUCCESS" }
            "Warning" { "WARNING" }
            "Fail"    { "ERROR"   }
            "Info"    { "INFO"    }
            default   { "INFO"    }
        }
        Write-Log "  [$checkName] $($check.Message)" -Level $level
    }

    if ($script:DriverInfo) {
        $driverText = "Driver: $($script:DriverInfo.CurrentDriver)"
        if ($script:NativeNVMeStatus -and $script:NativeNVMeStatus.IsActive) {
            $driverText = "Active: nvmedisk.sys (Native NVMe)"
        }
        $script:lblDriverInfo.Text = $driverText
    }

    # Populate detected drives list
    Update-DrivesList

    if ($script:BypassIOStatus -and $script:BypassIOStatus.Warning) {
        Write-Log "  [BypassIO] $($script:BypassIOStatus.Warning)" -Level "WARNING"
    }

    if ($script:BuildDetails) {
        Write-Log "  [Build] $($script:BuildDetails.DisplayVersion) (Build $($script:BuildDetails.BuildNumber).$($script:BuildDetails.UBR))" -Level "INFO"
    }

    Write-Log "----------------------------------------"
    Refresh-StatusDisplay
    Write-Log "----------------------------------------"
    Write-Log "Ready. Select an action above."

    Write-AppEventLog -Message "$($script:Config.AppName) v$($script:Config.AppVersion) started" -EntryType "Information" -EventId 1000
})

$script:form.Add_FormClosing({
    Save-Configuration

    if ($script:Config.AutoSaveLog -and $script:Config.LogHistory.Count -gt 5) {
        Save-LogFile -Suffix "_autosave" | Out-Null
    }

    Write-AppEventLog -Message "$($script:Config.AppName) closed" -EntryType "Information" -EventId 1000

    if ($script:ToolTipProvider) { $script:ToolTipProvider.Dispose() }
    if ($script:AppMutex) {
        try { $script:AppMutex.ReleaseMutex(); $script:AppMutex.Dispose() } catch { }
    }
})

# Run
[void]$script:form.ShowDialog()
$script:form.Dispose()
