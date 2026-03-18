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
    - Pre-flight checklist with visual go/no-go indicators
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
    Version: 3.4.0
    Author:  Matthew Parker
    Requires: Windows 11 24H2/25H2, Administrator privileges

    CHANGELOG v3.4.0:
    - Complete WPF rewrite replacing WinForms GUI
    - WindowStyle=None with AllowsTransparency for true dark chrome (no light title bar)
    - Custom title bar with drag, minimize, close
    - Zinc-950 dark palette with blue accent (LibreSpot-style)
    - Removed all GDI Region management (WPF uses vector rendering)
    - Removed DarkColorTable C# compilation (not needed with WPF)
    - Removed all System.Drawing dependencies from GUI
    - Fixed dark theme broken on PS7/.NET Core WinForms (WPF renders correctly everywhere)
    - DispatcherTimer for async preflight polling (replaces WinForms Timer)
    - Themed WPF confirmation dialog (replaces MessageBox)
    - Microsoft.Win32.SaveFileDialog (replaces WinForms SaveFileDialog)
    - System.Windows.Clipboard (replaces WinForms Clipboard)
    - All backend logic preserved from v3.3.0

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

# Build argument list for re-launch (used by both elevation and PS7->PS5.1 redirect)
function Get-RelaunchArgs {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Silent) { $argList += " -Silent" }
    if ($Apply) { $argList += " -Apply" }
    if ($Remove) { $argList += " -Remove" }
    if ($Status) { $argList += " -Status" }
    if ($NoRestart) { $argList += " -NoRestart" }
    if ($Force) { $argList += " -Force" }
    if ($ExportDiagnostics) { $argList += " -ExportDiagnostics" }
    if ($GenerateVerifyScript) { $argList += " -GenerateVerifyScript" }
    return $argList
}

if (-not (Test-Administrator)) {
    $argList = Get-RelaunchArgs
    try {
        Start-Process powershell.exe -ArgumentList $argList -Verb RunAs
    }
    catch {
        if (-not $Silent -and -not $ExportDiagnostics -and -not $GenerateVerifyScript) {
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show(
                "This application requires Administrator privileges.`n`nPlease right-click and select 'Run as Administrator'.",
                "Elevation Required",
                [System.Windows.MessageBoxButton]::OK,
                [System.Windows.MessageBoxImage]::Error
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

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type -AssemblyName System.Windows.Forms

if (-not ([System.Management.Automation.PSTypeName]'DpiAwareness').Type) {
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
"@
}

try { [DpiAwareness]::SetProcessDpiAwareness(2) | Out-Null }
catch { try { [DpiAwareness]::SetProcessDPIAware() | Out-Null } catch {} }

# ===========================================================================
# SECTION 3: SINGLE INSTANCE MUTEX
# ===========================================================================

$script:AppMutex = $null
$mutexName = "Global\NVMeDriverPatcher_SingleInstance"

if (-not $ExportDiagnostics -and -not $GenerateVerifyScript -and -not $Status) {
    try {
        $createdNew = $false
        $script:AppMutex = New-Object System.Threading.Mutex($true, $mutexName, [ref]$createdNew)
        if (-not $createdNew) {
            if (-not $Silent) {
                Add-Type -AssemblyName PresentationFramework -ErrorAction SilentlyContinue
                [System.Windows.MessageBox]::Show(
                    "NVMe Driver Patcher is already running.",
                    "Already Running",
                    [System.Windows.MessageBoxButton]::OK,
                    [System.Windows.MessageBoxImage]::Information
                ) | Out-Null
            }
            else {
                Write-Warning "NVMe Driver Patcher is already running."
            }
            exit 0
        }
    }
    catch {
        Write-Warning "Mutex check failed: $($_.Exception.Message)"
    }
}

# ===========================================================================
# SECTION 4: GLOBAL CONFIGURATION
# ===========================================================================

$script:Config = @{
    AppName         = "NVMe Driver Patcher"
    AppVersion      = "3.4.0"
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
    SkipWarnings    = $false
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

function Import-Configuration {
    if (Test-Path $script:Config.ConfigFile) {
        try {
            $savedConfig = Get-Content $script:Config.ConfigFile -Raw | ConvertFrom-Json
            if ($null -ne $savedConfig.AutoSaveLog) { $script:Config.AutoSaveLog = $savedConfig.AutoSaveLog }
            if ($null -ne $savedConfig.EnableToasts) { $script:Config.EnableToasts = $savedConfig.EnableToasts }
            if ($null -ne $savedConfig.WriteEventLog) { $script:Config.WriteEventLog = $savedConfig.WriteEventLog }
            if ($null -ne $savedConfig.RestartDelay) { $script:Config.RestartDelay = $savedConfig.RestartDelay }
            if ($null -ne $savedConfig.IncludeServerKey) { $script:Config.IncludeServerKey = $savedConfig.IncludeServerKey }
            if ($null -ne $savedConfig.SkipWarnings) { $script:Config.SkipWarnings = $savedConfig.SkipWarnings }
        }
        catch {
            Write-Warning "Failed to load configuration: $($_.Exception.Message)"
        }
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
            SkipWarnings     = $script:Config.SkipWarnings
            LastRun          = (Get-Date).ToString("o")
        }
        $configToSave | ConvertTo-Json | Out-File $script:Config.ConfigFile -Encoding UTF8
    }
    catch {
        Write-Warning "Failed to save configuration: $($_.Exception.Message)"
    }
}

Import-Configuration

# ===========================================================================
# SECTION 6: THEME DETECTION (kept for log color selection in silent mode)
# ===========================================================================

function Get-WindowsThemeMode {
    try {
        $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
        $val = Get-ItemProperty -Path $key -Name "AppsUseLightTheme" -ErrorAction SilentlyContinue
        if ($null -ne $val -and $val.AppsUseLightTheme -eq 1) { return "Light" }
    }
    catch { <# Theme detection failed, default to dark #> }
    return "Dark"
}

# Hex color strings for Write-Log level coloring (WPF uses XAML-defined colors)
$script:LogColors = @{
    Success = "#FF22c55e"
    Warning = "#FFf59e0b"
    Danger  = "#FFef4444"
    Info    = "#FF3b82f6"
    Muted   = "#FF52525b"
    Text    = "#FFa1a1aa"
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
        Write-Warning "Event log source creation failed (non-critical): $($_.Exception.Message)"
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
# SECTION 7B: GITHUB UPDATE CHECK
# ===========================================================================

$script:UpdateAvailable = $null

function Test-UpdateAvailable {
    try {
        $apiUrl = "https://api.github.com/repos/SysAdminDoc/win11-nvme-driver-patcher/releases/latest"
        $response = Invoke-RestMethod -Uri $apiUrl -Method Get -TimeoutSec 5 -ErrorAction Stop
        $latestTag = $response.tag_name -replace '^v', ''
        $currentVersion = [version]$script:Config.AppVersion
        $latestVersion = [version]$latestTag
        if ($latestVersion -gt $currentVersion) {
            $bodyText = if ($response.body) { $response.body } else { "" }
            return @{
                Version = $latestTag
                URL     = $response.html_url
                Notes   = if ($bodyText.Length -gt 200) { $bodyText.Substring(0, 200) + "..." } else { $bodyText }
            }
        }
    }
    catch { <# Update check is best-effort, never block on failure #> }
    return $null
}

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

        $cleanupTimer = New-Object System.Windows.Forms.Timer
        $cleanupTimer.Interval = 6000
        $iconRef = $notifyIcon
        $cleanupTimer.Add_Tick({
            $this.Stop()
            $this.Dispose()
            $iconRef.Visible = $false
            $iconRef.Dispose()
        }.GetNewClosure())
        $cleanupTimer.Start()
    }
    catch {
        Write-Warning "Toast notification failed: $($_.Exception.Message)"
    }
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
        catch { <# Both BitLocker detection methods failed, assume not encrypted #> }
    }
    return $false
}

function Test-VeraCryptSystemEncryption {
    # Fast detection: check service + EFI path instead of slow Win32_SystemDriver query
    try {
        $svc = Get-Service -Name "veracrypt" -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq "Running") { return $true }
        # Check for VeraCrypt boot driver via registry (instant, no CIM)
        $vcReg = "HKLM:\SYSTEM\CurrentControlSet\Services\veracrypt"
        if (Test-Path $vcReg) {
            $start = (Get-ItemProperty -Path $vcReg -Name "Start" -ErrorAction SilentlyContinue).Start
            if ($start -eq 0) { return $true }  # Boot-start driver
        }
        # Check EFI
        $efiPath = "$env:SystemDrive\EFI\VeraCrypt"
        if (Test-Path $efiPath -ErrorAction SilentlyContinue) { return $true }
    }
    catch { <# VeraCrypt detection best-effort #> }
    return $false
}

function Get-IncompatibleSoftware {
    # Fast detection via Get-Service (instant) instead of slow Win32_Service/Win32_SystemDriver CIM
    $found = [System.Collections.ArrayList]::new()
    try {
        $allServices = Get-Service -ErrorAction SilentlyContinue
        if ($allServices | Where-Object { $_.Name -match "veracrypt" }) {
            [void]$found.Add(@{ Name = "VeraCrypt"; Severity = "Critical"; Message = "System encryption breaks boot with nvmedisk.sys" })
        }
        if ($allServices | Where-Object { $_.Name -match "acronis|AcronisAgent" }) {
            [void]$found.Add(@{ Name = "Acronis"; Severity = "High"; Message = "Backup may not see drives under Storage disks category" })
        }
        if ($allServices | Where-Object { $_.Name -match "macrium|ReflectService" }) {
            [void]$found.Add(@{ Name = "Macrium Reflect"; Severity = "Medium"; Message = "May need update for Storage disks compatibility" })
        }
        if ($allServices | Where-Object { $_.Name -match "VBox" }) {
            [void]$found.Add(@{ Name = "VirtualBox"; Severity = "Low"; Message = "Storage filter drivers may conflict" })
        }
    }
    catch { <# Software detection best-effort #> }
    return $found
}

function Get-NVMeDriverInfo {
    $driverInfo = @{
        HasThirdParty   = $false
        ThirdPartyName  = ""
        InboxVersion    = ""
        CurrentDriver   = ""
        QueueDepth      = "Unknown"
        FirmwareVersions = @{}
    }

    try {
        $allSignedDrivers = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue
        $nvmeDrivers = $allSignedDrivers |
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

        $stornvme = $allSignedDrivers |
            Where-Object { $_.InfName -eq "stornvme.inf" } | Select-Object -First 1
        if ($stornvme) {
            $driverInfo.InboxVersion = $stornvme.DriverVersion
            if (-not $driverInfo.HasThirdParty) {
                $driverInfo.CurrentDriver = "Windows Inbox (stornvme) v$($stornvme.DriverVersion)"
            }
        }

        try {
            $queueDepthPath = "HKLM:\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device"
            if (Test-Path $queueDepthPath) {
                $qd = Get-ItemProperty -Path $queueDepthPath -Name "IoQueueDepth" -ErrorAction SilentlyContinue
                if ($qd) { $driverInfo.QueueDepth = $qd.IoQueueDepth }
            }
        }
        catch { <# Queue depth registry key may not exist #> }

        try {
            $physDisks = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.BusType -eq "NVMe" }
            foreach ($pd in $physDisks) {
                if ($pd.FirmwareVersion) {
                    $driverInfo.FirmwareVersions["$($pd.DeviceId)"] = $pd.FirmwareVersion
                }
            }
        }
        catch { <# Firmware version collection best-effort #> }
    }
    catch {
        $driverInfo.CurrentDriver = "Unable to detect"
    }

    return $driverInfo
}

function Get-NVMeHealthData {
    $health = @{}
    try {
        $physDisks = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.BusType -eq "NVMe" -or $_.MediaType -eq "SSD" }
        foreach ($pd in $physDisks) {
            $diskNum = $pd.DeviceId
            $info = @{
                Temperature = "N/A"
                Wear = "N/A"
                MediaErrors = 0
                HealthStatus = $pd.HealthStatus
                OperationalStatus = $pd.OperationalStatus
                PowerOnHours = "N/A"
                ReadErrors = 0
                WriteErrors = 0
                AvailableSpare = "N/A"
                SmartTooltip = ""
            }
            try {
                $reliability = $pd | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
                if ($reliability) {
                    if ($null -ne $reliability.Temperature) { $info.Temperature = "$($reliability.Temperature)C" }
                    if ($null -ne $reliability.Wear) { $info.Wear = "$([Math]::Max(0, 100 - $reliability.Wear))%" }
                    if ($null -ne $reliability.MediaErrors) { $info.MediaErrors = $reliability.MediaErrors }
                    if ($null -ne $reliability.PowerOnHours) { $info.PowerOnHours = "$($reliability.PowerOnHours)h" }
                    if ($null -ne $reliability.ReadErrorsTotal) { $info.ReadErrors = $reliability.ReadErrorsTotal }
                    if ($null -ne $reliability.WriteErrorsTotal) { $info.WriteErrors = $reliability.WriteErrorsTotal }
                }
            }
            catch { <# StorageReliabilityCounter not available on all drives #> }
            $tipParts = [System.Collections.ArrayList]::new()
            [void]$tipParts.Add("Health: $($info.HealthStatus)")
            if ($info.Temperature -ne "N/A") { [void]$tipParts.Add("Temp: $($info.Temperature)") }
            if ($info.Wear -ne "N/A") { [void]$tipParts.Add("Life remaining: $($info.Wear)") }
            if ($info.PowerOnHours -ne "N/A") { [void]$tipParts.Add("Power-on: $($info.PowerOnHours)") }
            if ($info.MediaErrors -gt 0) { [void]$tipParts.Add("Media errors: $($info.MediaErrors)") }
            if ($info.ReadErrors -gt 0) { [void]$tipParts.Add("Read errors: $($info.ReadErrors)") }
            $info.SmartTooltip = $tipParts -join " | "
            $health[$diskNum] = $info
        }
    }
    catch { <# PhysicalDisk query failed, health data unavailable #> }
    return $health
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
    $result = @{
        IsActive       = $false
        ActiveDriver   = "Unknown"
        DeviceCategory = "Unknown"
        StorageDisks   = @()
        Details        = ""
    }

    try {
        $nvmeDiskDriver = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq "nvmedisk" -or $_.PathName -match "nvmedisk" }

        if ($nvmeDiskDriver -and $nvmeDiskDriver.State -eq "Running") {
            $result.IsActive = $true
            $result.ActiveDriver = "nvmedisk.sys (Native NVMe)"
            $result.Details = "Native NVMe driver is running"
        }

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

        try {
            $cv = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue
            if ($cv.DisplayVersion) { $details.DisplayVersion = $cv.DisplayVersion }
            if ($cv.UBR) { $details.UBR = [int]$cv.UBR }
        }
        catch { <# Display version registry key may not exist on older builds #> }

        $details.Is24H2OrLater = ($details.BuildNumber -ge 26100)
        $details.IsRecommended = ($details.BuildNumber -ge $script:Config.RecommendedBuild)
    }
    catch {
        Write-Warning "Failed to retrieve build details: $($_.Exception.Message)"
    }

    return $details
}

# Global state
$script:HasNVMeDrives = $false
$script:BitLockerEnabled = $false
$script:VeraCryptDetected = $false
$script:IncompatibleSoftware = @()
$script:DriverInfo = $null
$script:NativeNVMeStatus = $null
$script:BypassIOStatus = $null
$script:BuildDetails = $null
$script:CachedDrives = $null
$script:CachedHealth = $null
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
        VeraCrypt        = @{ Status = "Checking"; Message = "Checking..."; Critical = $true }
        ThirdPartyDriver = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
        Compatibility    = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
        SystemProtection = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
        DriverStatus     = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
        BypassIO         = @{ Status = "Checking"; Message = "Checking..."; Critical = $false }
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    # Windows Version
    Write-Log "  [1/8] Checking Windows version..." -Level "DEBUG"
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
    Write-Log "  [2/8] Scanning drives..." -Level "DEBUG"
    $script:CachedDrives = Get-SystemDrives
    $drives = $script:CachedDrives
    $nvmeCount = ($drives | Where-Object { $_.IsNVMe }).Count
    $script:HasNVMeDrives = ($nvmeCount -gt 0)
    if ($nvmeCount -gt 0) {
        $script:PreflightChecks.NVMeDrives = @{ Status = "Pass"; Message = "$nvmeCount NVMe drive(s)"; Critical = $false }
    }
    else {
        $script:PreflightChecks.NVMeDrives = @{ Status = "Warning"; Message = "No NVMe drives"; Critical = $false }
    }

    # BitLocker
    Write-Log "  [3/8] Checking BitLocker..." -Level "DEBUG"
    $script:BitLockerEnabled = Test-BitLockerEnabled
    if ($script:BitLockerEnabled) {
        $script:PreflightChecks.BitLocker = @{ Status = "Warning"; Message = "Encryption active"; Critical = $false }
    }
    else {
        $script:PreflightChecks.BitLocker = @{ Status = "Pass"; Message = "Not detected"; Critical = $false }
    }

    # VeraCrypt
    Write-Log "  [4/8] Checking VeraCrypt..." -Level "DEBUG"
    $script:VeraCryptDetected = Test-VeraCryptSystemEncryption
    if ($script:VeraCryptDetected) {
        $script:PreflightChecks.VeraCrypt = @{ Status = "Fail"; Message = "BLOCKS PATCH - breaks boot"; Critical = $true }
    }
    else {
        $script:PreflightChecks.VeraCrypt = @{ Status = "Pass"; Message = "Not detected"; Critical = $true }
    }

    # Incompatible Software
    Write-Log "  [5/8] Checking software compatibility..." -Level "DEBUG"
    $script:IncompatibleSoftware = Get-IncompatibleSoftware
    $criticalSw = @($script:IncompatibleSoftware | Where-Object { $_.Severity -eq "Critical" })
    $warnSw = @($script:IncompatibleSoftware | Where-Object { $_.Severity -ne "Critical" })
    if ($criticalSw.Count -gt 0) {
        $script:PreflightChecks.Compatibility = @{ Status = "Fail"; Message = ($criticalSw | ForEach-Object { $_.Name }) -join ", "; Critical = $false }
    }
    elseif ($warnSw.Count -gt 0) {
        $script:PreflightChecks.Compatibility = @{ Status = "Warning"; Message = ($warnSw | ForEach-Object { $_.Name }) -join ", "; Critical = $false }
    }
    else {
        $script:PreflightChecks.Compatibility = @{ Status = "Pass"; Message = "No conflicts"; Critical = $false }
    }

    # Third-party Driver
    Write-Log "  [6/8] Checking NVMe drivers..." -Level "DEBUG"
    $script:DriverInfo = Get-NVMeDriverInfo
    if ($script:DriverInfo.HasThirdParty) {
        $script:PreflightChecks.ThirdPartyDriver = @{ Status = "Warning"; Message = $script:DriverInfo.ThirdPartyName; Critical = $false }
    }
    else {
        $script:PreflightChecks.ThirdPartyDriver = @{ Status = "Pass"; Message = "Using inbox driver"; Critical = $false }
    }

    # System Protection
    Write-Log "  [7/8] Checking System Protection..." -Level "DEBUG"
    try {
        $null = Get-ComputerRestorePoint -ErrorAction SilentlyContinue
        $script:PreflightChecks.SystemProtection = @{ Status = "Pass"; Message = "Available"; Critical = $false }
    }
    catch {
        $script:PreflightChecks.SystemProtection = @{ Status = "Warning"; Message = "May be disabled"; Critical = $false }
    }

    # Native NVMe Driver Activation Status
    Write-Log "  [8/8] Checking native NVMe driver and BypassIO..." -Level "DEBUG"
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

    Write-Log "  Pre-flight complete ($($sw.ElapsedMilliseconds)ms)" -Level "DEBUG"
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

    # WPF GUI log output
    if ($script:ui -and $script:ui['LogOutput']) {
        try {
            $script:ui['LogOutput'].Text += "$logEntry`n"
            $script:ui['LogScroller'].ScrollToBottom()
        }
        catch { <# UI not ready yet #> }
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
    $saveDialog = New-Object Microsoft.Win32.SaveFileDialog
    $saveDialog.Filter = "Text Files (*.txt)|*.txt|Log Files (*.log)|*.log"
    $saveDialog.FileName = "NVMe_Patcher_Log_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
    $saveDialog.Title = "Export Log File"
    $saveDialog.InitialDirectory = $script:Config.WorkingDir

    if ($saveDialog.ShowDialog() -eq $true) {
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
        [System.Windows.Clipboard]::SetText($logText)
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
    catch { $report += "Unable to retrieve hardware information`n" }

    $report += "`nSTORAGE DRIVES`n--------------`n"
    $drives = if ($script:CachedDrives) { $script:CachedDrives } else { Get-SystemDrives }
    foreach ($drive in $drives) {
        $nvmeTag = if ($drive.IsNVMe) { " [NVMe]" } else { "" }
        $bootTag = if ($drive.IsBoot) { " [BOOT]" } else { "" }
        $report += "Disk $($drive.Number): $($drive.Name) ($($drive.Size))$nvmeTag$bootTag`n"
        $report += "  Bus Type: $($drive.BusType)`n"
        $report += "  PNP ID: $($drive.PNPDeviceID)`n"
    }

    $report += "`nNVMe DRIVER INFORMATION`n-----------------------`n"
    $driverInfo = if ($script:DriverInfo) { $script:DriverInfo } else { Get-NVMeDriverInfo }
    $report += "Current Driver: $($driverInfo.CurrentDriver)`n"
    $report += "Inbox Version: $($driverInfo.InboxVersion)`n"
    $report += "Third-Party: $(if ($driverInfo.HasThirdParty) { $driverInfo.ThirdPartyName } else { 'No' })`n"
    $report += "Queue Depth: $($driverInfo.QueueDepth)`n"

    $report += "`nNATIVE NVMe DRIVER STATUS`n-------------------------`n"
    $nativeStatus = if ($script:NativeNVMeStatus) { $script:NativeNVMeStatus } else { Test-NativeNVMeActive }
    $report += "Native NVMe Active: $(if ($nativeStatus.IsActive) { 'Yes' } else { 'No' })`n"
    $report += "Active Driver: $($nativeStatus.ActiveDriver)`n"
    $report += "Device Category: $($nativeStatus.DeviceCategory)`n"
    if ($nativeStatus.StorageDisks.Count -gt 0) {
        $report += "Storage Disks:`n"
        foreach ($sd in $nativeStatus.StorageDisks) { $report += "  - $sd`n" }
    }
    $report += "Details: $($nativeStatus.Details)`n"

    $report += "`nBYPASSIO / DIRECTSTORAGE STATUS`n-------------------------------`n"
    $bypassStatus = if ($script:BypassIOStatus) { $script:BypassIOStatus } else { Get-BypassIOStatus }
    $report += "BypassIO Supported: $(if ($bypassStatus.Supported) { 'Yes' } else { 'No' })`n"
    $report += "Storage Type: $($bypassStatus.StorageType)`n"
    $report += "Driver Compatibility: $($bypassStatus.DriverCompat)`n"
    if ($bypassStatus.BlockedBy) { $report += "Blocked By: $($bypassStatus.BlockedBy)`n" }
    if ($bypassStatus.Warning) { $report += "WARNING: $($bypassStatus.Warning)`n" }
    $report += "Raw Output:`n$($bypassStatus.RawOutput)`n"

    $report += "`nWINDOWS BUILD DETAILS`n--------------------`n"
    $buildDets = if ($script:BuildDetails) { $script:BuildDetails } else { Get-WindowsBuildDetails }
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
$includeServerKey = SERVERKEY_PLACEHOLDER
if ($includeServerKey) {
    $featureIDs += @{ ID = "1176759950"; Name = "Microsoft Official (Server 2025 key)" }
}
$safeBootMinimal = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"
$safeBootNetwork = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"

$passCount = 0
$totalChecks = $featureIDs.Count + 2

Write-Host "REGISTRY KEYS" -ForegroundColor Yellow
Write-Host "-------------" -ForegroundColor Yellow
Write-Host ""

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

$nvmeDiskDriver = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq "nvmedisk" -or $_.PathName -match "nvmedisk" }

if ($nvmeDiskDriver -and $nvmeDiskDriver.State -eq "Running") {
    Write-Host "  [PASS] nvmedisk.sys is RUNNING (Native NVMe active)" -ForegroundColor Green
}
else {
    Write-Host "  [INFO] nvmedisk.sys is NOT running (legacy stornvme.sys stack)" -ForegroundColor Yellow
}

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

    $serverKeyValue = if ($script:Config.IncludeServerKey) { '$true' } else { '$false' }
    $scriptContent = $scriptContent -replace 'SERVERKEY_PLACEHOLDER', $serverKeyValue

    try {
        $scriptContent | Out-File -FilePath $OutputPath -Encoding UTF8
        return $OutputPath
    }
    catch {
        return $null
    }
}

# ===========================================================================
# SECTION 13B: DISKSPD BENCHMARK
# ===========================================================================

function Get-DiskSpdPath {
    $diskSpdDir = Join-Path $script:Config.WorkingDir "DiskSpd"
    $diskSpdExe = Join-Path $diskSpdDir "diskspd.exe"
    if (Test-Path $diskSpdExe) { return $diskSpdExe }
    return $null
}

function Install-DiskSpd {
    $diskSpdDir = Join-Path $script:Config.WorkingDir "DiskSpd"
    $diskSpdExe = Join-Path $diskSpdDir "diskspd.exe"
    if (Test-Path $diskSpdExe) { return $diskSpdExe }

    Write-Log "Downloading Microsoft DiskSpd benchmark tool..." -Level "INFO"
    try {
        if (-not (Test-Path $diskSpdDir)) { New-Item -Path $diskSpdDir -ItemType Directory -Force | Out-Null }
        $zipUrl = "https://github.com/microsoft/diskspd/releases/latest/download/DiskSpd.ZIP"
        $zipPath = Join-Path $diskSpdDir "DiskSpd.zip"
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
        Expand-Archive -Path $zipPath -DestinationPath $diskSpdDir -Force -ErrorAction Stop
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        $exeFound = Get-ChildItem -Path $diskSpdDir -Recurse -Filter "diskspd.exe" |
            Where-Object { $_.FullName -match "amd64" } | Select-Object -First 1
        if ($exeFound) {
            Copy-Item -Path $exeFound.FullName -Destination $diskSpdExe -Force
            Write-Log "DiskSpd downloaded successfully" -Level "SUCCESS"
            return $diskSpdExe
        }
        $exeAny = Get-ChildItem -Path $diskSpdDir -Recurse -Filter "diskspd.exe" | Select-Object -First 1
        if ($exeAny) {
            Copy-Item -Path $exeAny.FullName -Destination $diskSpdExe -Force
            return $diskSpdExe
        }
        Write-Log "DiskSpd exe not found in archive" -Level "ERROR"
    }
    catch {
        Write-Log "Failed to download DiskSpd: $($_.Exception.Message)" -Level "ERROR"
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    }
    return $null
}

function Invoke-StorageBenchmark {
    param([string]$Label = "benchmark")

    $exe = Install-DiskSpd
    if (-not $exe) {
        Write-Log "Cannot run benchmark - DiskSpd unavailable" -Level "ERROR"
        return $null
    }

    $testFile = Join-Path $script:Config.WorkingDir "diskspd_test.dat"
    $results = @{ Label = $Label; Timestamp = (Get-Date).ToString("o"); Read = @{}; Write = @{} }

    try {
        Write-Log "Running 4K random read benchmark (30s)..." -Level "INFO"
        Update-Progress -Value 20 -Status "Benchmarking reads..."
        $readOutput = & $exe -c128M -d30 -w0 -t4 -o16 -b4K -r -Sh -L $testFile 2>&1 | Out-String
        $results.Read = Convert-DiskSpdOutput -RawOutput $readOutput -IOType "Read"

        Write-Log "Running 4K random write benchmark (30s)..." -Level "INFO"
        Update-Progress -Value 60 -Status "Benchmarking writes..."
        $writeOutput = & $exe -c128M -d30 -w100 -t4 -o16 -b4K -r -Sh -L $testFile 2>&1 | Out-String
        $results.Write = Convert-DiskSpdOutput -RawOutput $writeOutput -IOType "Write"

        Update-Progress -Value 0 -Status ""
    }
    catch {
        Write-Log "Benchmark error: $($_.Exception.Message)" -Level "ERROR"
        Update-Progress -Value 0 -Status ""
    }
    finally {
        Remove-Item $testFile -Force -ErrorAction SilentlyContinue
    }

    return $results
}

function Convert-DiskSpdOutput {
    param([string]$RawOutput, [string]$IOType = "Read")
    $parsed = @{ IOPS = 0; ThroughputMBs = 0; AvgLatencyMs = 0 }
    try {
        $lines = $RawOutput -split "`n"
        foreach ($line in $lines) {
            if ($line -match '^\s*total:') {
                $parts = $line -split '\|' | ForEach-Object { $_.Trim() }
                if ($parts.Count -ge 4) {
                    $culture = [System.Globalization.CultureInfo]::InvariantCulture
                    $throughputStr = ($parts[2] -replace '[^\d.,]','') -replace ',','.'
                    $iopsStr = ($parts[3] -replace '[^\d.,]','') -replace ',','.'
                    [double]$throughputVal = 0; [double]::TryParse($throughputStr, [System.Globalization.NumberStyles]::Float, $culture, [ref]$throughputVal) | Out-Null
                    [double]$iopsVal = 0; [double]::TryParse($iopsStr, [System.Globalization.NumberStyles]::Float, $culture, [ref]$iopsVal) | Out-Null
                    $parsed.ThroughputMBs = [math]::Round($throughputVal, 2)
                    $parsed.IOPS = [math]::Round($iopsVal, 0)
                    if ($parts.Count -ge 5) {
                        $latStr = ($parts[4] -replace '[^\d.,]','') -replace ',','.'
                        [double]$latVal = 0; [double]::TryParse($latStr, [System.Globalization.NumberStyles]::Float, $culture, [ref]$latVal) | Out-Null
                        $parsed.AvgLatencyMs = [math]::Round($latVal, 3)
                    }
                    break
                }
            }
        }
    }
    catch { <# Parsing best-effort #> }
    return $parsed
}

function Save-BenchmarkResults {
    param($Results)
    if (-not $Results) { return }
    $benchFile = Join-Path $script:Config.WorkingDir "benchmark_results.json"
    try {
        $existing = @()
        if (Test-Path $benchFile) {
            $raw = Get-Content $benchFile -Raw -ErrorAction SilentlyContinue
            if ($raw) { $existing = @(ConvertFrom-Json $raw -ErrorAction SilentlyContinue) }
        }
        $existing += $Results
        if ($existing.Count -gt 10) { $existing = $existing[-10..-1] }
        $existing | ConvertTo-Json -Depth 5 | Out-File $benchFile -Encoding UTF8
        Write-Log "Benchmark results saved" -Level "SUCCESS"
    }
    catch {
        Write-Log "Failed to save benchmark: $($_.Exception.Message)" -Level "WARNING"
    }
}

function Get-BenchmarkHistory {
    $benchFile = Join-Path $script:Config.WorkingDir "benchmark_results.json"
    if (-not (Test-Path $benchFile)) { return @() }
    try {
        $raw = Get-Content $benchFile -Raw
        return @(ConvertFrom-Json $raw -ErrorAction SilentlyContinue)
    }
    catch { return @() }
}

function Show-BenchmarkComparison {
    param($Current)
    if (-not $Current) { return }

    $history = Get-BenchmarkHistory
    $prev = $history | Where-Object { $_.Label -ne $Current.Label } | Select-Object -Last 1

    Write-Log "" -Level "INFO"
    Write-Log "============ BENCHMARK RESULTS ============" -Level "INFO"
    Write-Log "  $($Current.Label) @ $(Get-Date -Format 'HH:mm:ss')" -Level "INFO"
    Write-Log "  4K Random Read:  $($Current.Read.IOPS) IOPS  |  $($Current.Read.ThroughputMBs) MB/s  |  $($Current.Read.AvgLatencyMs) ms avg" -Level "SUCCESS"
    Write-Log "  4K Random Write: $($Current.Write.IOPS) IOPS  |  $($Current.Write.ThroughputMBs) MB/s  |  $($Current.Write.AvgLatencyMs) ms avg" -Level "SUCCESS"

    if ($prev -and $prev.Read -and $prev.Write) {
        Write-Log "" -Level "INFO"
        Write-Log "  --- vs. Previous ($($prev.Label)) ---" -Level "INFO"
        $prevReadIOPS = if ($prev.Read.IOPS) { [double]$prev.Read.IOPS } else { 0 }
        $prevWriteIOPS = if ($prev.Write.IOPS) { [double]$prev.Write.IOPS } else { 0 }
        $curReadIOPS = if ($Current.Read.IOPS) { [double]$Current.Read.IOPS } else { 0 }
        $curWriteIOPS = if ($Current.Write.IOPS) { [double]$Current.Write.IOPS } else { 0 }
        $readDelta = if ($prevReadIOPS -gt 0) { [math]::Round(($curReadIOPS - $prevReadIOPS) / $prevReadIOPS * 100, 1) } else { 0 }
        $writeDelta = if ($prevWriteIOPS -gt 0) { [math]::Round(($curWriteIOPS - $prevWriteIOPS) / $prevWriteIOPS * 100, 1) } else { 0 }
        $readSign = if ($readDelta -ge 0) { "+" } else { "" }
        $writeSign = if ($writeDelta -ge 0) { "+" } else { "" }
        $readLevel = if ($readDelta -gt 0) { "SUCCESS" } elseif ($readDelta -lt 0) { "WARNING" } else { "INFO" }
        $writeLevel = if ($writeDelta -gt 0) { "SUCCESS" } elseif ($writeDelta -lt 0) { "WARNING" } else { "INFO" }
        Write-Log "  Read IOPS:  $prevReadIOPS --> $curReadIOPS ($readSign$readDelta%)" -Level $readLevel
        Write-Log "  Write IOPS: $prevWriteIOPS --> $curWriteIOPS ($writeSign$writeDelta%)" -Level $writeLevel
    }
    Write-Log "===========================================" -Level "INFO"
}

function Start-GUIBenchmark {
    Set-ButtonsEnabled -Enabled $false

    $status = Test-PatchStatus
    $label = if ($status.Applied) { "Post-Patch" } else { "Pre-Patch" }

    Write-Log "Starting storage benchmark ($label)..." -Level "INFO"
    Write-Log "This will take approximately 60 seconds. Do not use disk-heavy apps." -Level "WARNING"

    $results = Invoke-StorageBenchmark -Label $label
    if ($results) {
        Save-BenchmarkResults -Results $results
        Show-BenchmarkComparison -Current $results
    }

    Set-ButtonsEnabled -Enabled $true
}

# ===========================================================================
# SECTION 13C: POST-REBOOT DETECTION
# ===========================================================================

function Test-PatchAppliedSinceLastRun {
    try {
        $stateFile = Join-Path $script:Config.WorkingDir "last_patch_state.json"
        $currentStatus = Test-PatchStatus
        $nativeStatus = Test-NativeNVMeActive

        $currentState = @{
            Applied = $currentStatus.Applied
            Count = $currentStatus.Count
            NativeActive = $nativeStatus.IsActive
            ActiveDriver = $nativeStatus.ActiveDriver
        }

        if (Test-Path $stateFile) {
            $raw = Get-Content $stateFile -Raw -ErrorAction SilentlyContinue
            $lastState = ConvertFrom-Json $raw -ErrorAction SilentlyContinue
            $lastNativeActive = if ($null -ne $lastState.NativeActive) { $lastState.NativeActive } else { $false }
            $lastApplied = if ($null -ne $lastState.Applied) { $lastState.Applied } else { $false }
            if ($lastState -and $lastApplied -and -not $lastNativeActive -and $currentState.NativeActive) {
                return @{
                    Changed = $true
                    Message = "Native NVMe driver (nvmedisk.sys) is now ACTIVE after reboot"
                    Driver = $currentState.ActiveDriver
                }
            }
            if ($lastState -and -not $lastApplied -and $currentState.Applied) {
                return @{
                    Changed = $true
                    Message = "Patch was applied since last run - reboot to activate"
                    Driver = $currentState.ActiveDriver
                }
            }
        }

        $currentState | ConvertTo-Json | Out-File $stateFile -Encoding UTF8

        return @{ Changed = $false }
    }
    catch { return @{ Changed = $false } }
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
# SECTION 15: WPF UI HELPER FUNCTIONS
# ===========================================================================

function Set-ButtonsEnabled {
    param([bool]$Enabled)
    if ($script:ui) {
        if ($script:ui['BtnApply']) { $script:ui['BtnApply'].IsEnabled = $Enabled }
        if ($script:ui['BtnRemove']) { $script:ui['BtnRemove'].IsEnabled = $Enabled }
        if ($script:ui['BtnBackup']) { $script:ui['BtnBackup'].IsEnabled = $Enabled }
        if ($script:ui['BtnBenchmark']) { $script:ui['BtnBenchmark'].IsEnabled = $Enabled }
        if ($script:ui['BtnDiagnostics']) { $script:ui['BtnDiagnostics'].IsEnabled = $Enabled }
    }
}

function Update-Progress {
    param([int]$Value, [string]$Status = "")
    if (-not $script:ui) { return }
    $val = [Math]::Min($Value, 100)
    if ($script:ui['MainProgress']) {
        $script:ui['MainProgress'].Value = $val
        $script:ui['MainProgress'].Visibility = if ($val -gt 0 -and $val -lt 100) { 'Visible' } else { 'Collapsed' }
    }
    if ($script:ui['ProgressLabel']) {
        if ($Status) {
            $script:ui['ProgressLabel'].Text = $Status
            $script:ui['ProgressLabel'].Visibility = 'Visible'
        }
        else {
            $script:ui['ProgressLabel'].Visibility = 'Collapsed'
        }
    }
}

function Update-DrivesList {
    if (-not $script:ui -or -not $script:ui['DriveList']) { return }
    $script:ui['DriveList'].Children.Clear()
    $drives = if ($script:CachedDrives) { $script:CachedDrives } else { Get-SystemDrives }
    if ($drives.Count -eq 0) {
        $noLabel = New-Object System.Windows.Controls.TextBlock
        $noLabel.Text = "No drives detected"
        $noLabel.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString("#FF71717a")
        $noLabel.FontSize = 11
        $noLabel.Margin = [System.Windows.Thickness]::new(0, 4, 0, 0)
        $script:ui['DriveList'].Children.Add($noLabel) | Out-Null
        return
    }
    $bc = [System.Windows.Media.BrushConverter]::new()
    foreach ($drv in $drives) {
        $row = New-Object System.Windows.Controls.StackPanel
        $row.Orientation = 'Horizontal'
        $row.Margin = [System.Windows.Thickness]::new(0, 3, 0, 3)

        # Status dot
        $dot = New-Object System.Windows.Shapes.Ellipse
        $dot.Width = 8; $dot.Height = 8
        $dot.Fill = if ($drv.IsNVMe) { $bc.ConvertFromString("#FF22c55e") } else { $bc.ConvertFromString("#FF52525b") }
        $dot.VerticalAlignment = 'Center'
        $dot.Margin = [System.Windows.Thickness]::new(0, 0, 10, 0)
        $row.Children.Add($dot) | Out-Null

        # Drive name
        $nameTb = New-Object System.Windows.Controls.TextBlock
        $nameTb.Text = $drv.Name
        $nameTb.Foreground = $bc.ConvertFromString("#FFfafafa")
        $nameTb.FontSize = 11
        $nameTb.Width = 240
        $nameTb.TextTrimming = 'CharacterEllipsis'
        $nameTb.VerticalAlignment = 'Center'
        $row.Children.Add($nameTb) | Out-Null

        # Size
        $sizeTb = New-Object System.Windows.Controls.TextBlock
        $sizeTb.Text = $drv.Size
        $sizeTb.Foreground = $bc.ConvertFromString("#FFa1a1aa")
        $sizeTb.FontSize = 11
        $sizeTb.Width = 60
        $sizeTb.TextAlignment = 'Right'
        $sizeTb.VerticalAlignment = 'Center'
        $sizeTb.Margin = [System.Windows.Thickness]::new(0, 0, 10, 0)
        $row.Children.Add($sizeTb) | Out-Null

        # Bus type pill
        $busPill = New-Object System.Windows.Controls.Border
        $busPill.CornerRadius = [System.Windows.CornerRadius]::new(4)
        $busPill.Padding = [System.Windows.Thickness]::new(8, 2, 8, 2)
        $busPill.Margin = [System.Windows.Thickness]::new(0, 0, 6, 0)
        $busPill.VerticalAlignment = 'Center'
        if ($drv.IsNVMe) {
            $busPill.Background = $bc.ConvertFromString("#FF0c2d5e")
        }
        else {
            $busPill.Background = $bc.ConvertFromString("#FF18181b")
        }
        $busText = New-Object System.Windows.Controls.TextBlock
        $busText.Text = $drv.BusType
        $busText.FontSize = 10
        $busText.FontWeight = 'SemiBold'
        if ($drv.IsNVMe) {
            $busText.Foreground = $bc.ConvertFromString("#FF3b82f6")
        }
        else {
            $busText.Foreground = $bc.ConvertFromString("#FF71717a")
        }
        $busPill.Child = $busText
        $row.Children.Add($busPill) | Out-Null

        # Boot badge
        if ($drv.IsBoot) {
            $bootPill = New-Object System.Windows.Controls.Border
            $bootPill.CornerRadius = [System.Windows.CornerRadius]::new(4)
            $bootPill.Padding = [System.Windows.Thickness]::new(6, 2, 6, 2)
            $bootPill.Margin = [System.Windows.Thickness]::new(0, 0, 6, 0)
            $bootPill.Background = $bc.ConvertFromString("#FF2a2612")
            $bootPill.VerticalAlignment = 'Center'
            $bootText = New-Object System.Windows.Controls.TextBlock
            $bootText.Text = "BOOT"
            $bootText.FontSize = 9
            $bootText.FontWeight = 'SemiBold'
            $bootText.Foreground = $bc.ConvertFromString("#FFf59e0b")
            $bootPill.Child = $bootText
            $row.Children.Add($bootPill) | Out-Null
        }

        # Health badges for NVMe drives
        if ($drv.IsNVMe -and $script:CachedHealth) {
            $diskKey = "$($drv.Number)"
            $hData = $script:CachedHealth[$diskKey]
            if ($hData) {
                if ($hData.Temperature -ne "N/A") {
                    $tempPill = New-Object System.Windows.Controls.Border
                    $tempPill.CornerRadius = [System.Windows.CornerRadius]::new(4)
                    $tempPill.Padding = [System.Windows.Thickness]::new(6, 2, 6, 2)
                    $tempPill.Margin = [System.Windows.Thickness]::new(0, 0, 4, 0)
                    $tempPill.Background = $bc.ConvertFromString("#FF18181b")
                    $tempPill.VerticalAlignment = 'Center'
                    $tempTb = New-Object System.Windows.Controls.TextBlock
                    $tempTb.Text = $hData.Temperature
                    $tempTb.FontSize = 9
                    $tempTb.FontWeight = 'SemiBold'
                    $tempVal = 0; [int]::TryParse(($hData.Temperature -replace '[^0-9]',''), [ref]$tempVal) | Out-Null
                    $tempColor = if ($tempVal -ge 70) { "#FFef4444" } elseif ($tempVal -ge 50) { "#FFf59e0b" } else { "#FF22c55e" }
                    $tempTb.Foreground = $bc.ConvertFromString($tempColor)
                    $tempPill.Child = $tempTb
                    $row.Children.Add($tempPill) | Out-Null
                }
                if ($hData.Wear -ne "N/A") {
                    $wearPill = New-Object System.Windows.Controls.Border
                    $wearPill.CornerRadius = [System.Windows.CornerRadius]::new(4)
                    $wearPill.Padding = [System.Windows.Thickness]::new(6, 2, 6, 2)
                    $wearPill.Background = $bc.ConvertFromString("#FF18181b")
                    $wearPill.VerticalAlignment = 'Center'
                    $wearTb = New-Object System.Windows.Controls.TextBlock
                    $wearTb.Text = $hData.Wear
                    $wearTb.FontSize = 9
                    $wearTb.FontWeight = 'SemiBold'
                    $wearVal = 0; [int]::TryParse(($hData.Wear -replace '[^0-9]',''), [ref]$wearVal) | Out-Null
                    $wearColor = if ($wearVal -le 20) { "#FFef4444" } elseif ($wearVal -le 50) { "#FFf59e0b" } else { "#FF22c55e" }
                    $wearTb.Foreground = $bc.ConvertFromString($wearColor)
                    $wearPill.Child = $wearTb
                    # Tooltip with SMART data
                    $smartTip = if ($hData.SmartTooltip) { $hData.SmartTooltip } else { "Drive health remaining" }
                    $wearPill.ToolTip = $smartTip
                    $row.Children.Add($wearPill) | Out-Null
                }
            }
        }

        $script:ui['DriveList'].Children.Add($row) | Out-Null
    }
}

function Update-StatusDisplay {
    if (-not $script:ui) { return }
    $status = Test-PatchStatus
    $bc = [System.Windows.Media.BrushConverter]::new()

    # Update feature flag dots in registry components section
    $flagDotMap = @{
        "735209102"  = "Dot735"
        "1853569164" = "Dot1853"
        "156965516"  = "Dot156"
    }
    foreach ($id in $script:Config.FeatureIDs) {
        if ($flagDotMap.ContainsKey($id) -and $script:ui[$flagDotMap[$id]]) {
            $isPresent = ($status.Keys -contains $id)
            $script:ui[$flagDotMap[$id]].Fill = if ($isPresent) { $bc.ConvertFromString("#FF22c55e") } else { $bc.ConvertFromString("#FFef4444") }
        }
    }

    # SafeBoot dots
    if ($script:ui['DotSafeMin']) {
        $isPresent = ($status.Keys -contains "SafeBootMinimal")
        $script:ui['DotSafeMin'].Fill = if ($isPresent) { $bc.ConvertFromString("#FF22c55e") } else { $bc.ConvertFromString("#FFef4444") }
    }
    if ($script:ui['DotSafeNet']) {
        $isPresent = ($status.Keys -contains "SafeBootNetwork")
        $script:ui['DotSafeNet'].Fill = if ($isPresent) { $bc.ConvertFromString("#FF22c55e") } else { $bc.ConvertFromString("#FFef4444") }
    }

    # Server key dot
    if ($script:ui['Dot1176']) {
        $serverPresent = $false
        try {
            if (Test-Path $script:Config.RegistryPath) {
                $sVal = Get-ItemProperty -Path $script:Config.RegistryPath -Name "1176759950" -ErrorAction SilentlyContinue
                if ($sVal -and $sVal."1176759950" -eq 1) { $serverPresent = $true }
            }
        }
        catch { <# Server key check non-critical #> }
        $script:ui['Dot1176'].Fill = if ($serverPresent) { $bc.ConvertFromString("#FF22c55e") }
                                     elseif ($script:Config.IncludeServerKey) { $bc.ConvertFromString("#FFf59e0b") }
                                     else { $bc.ConvertFromString("#FF71717a") }
    }

    # Status dot and label
    if ($status.Applied) {
        if ($script:ui['StatusDot']) { $script:ui['StatusDot'].Fill = $bc.ConvertFromString("#FF22c55e") }
        if ($script:ui['StatusLabel']) { $script:ui['StatusLabel'].Text = "Patch Applied" }
    }
    elseif ($status.Partial) {
        if ($script:ui['StatusDot']) { $script:ui['StatusDot'].Fill = $bc.ConvertFromString("#FFf59e0b") }
        if ($script:ui['StatusLabel']) { $script:ui['StatusLabel'].Text = "Partial ($($status.Count)/$($status.Total))" }
    }
    else {
        if ($script:ui['StatusDot']) { $script:ui['StatusDot'].Fill = $bc.ConvertFromString("#FF71717a") }
        if ($script:ui['StatusLabel']) { $script:ui['StatusLabel'].Text = "Not Applied" }
    }

    # Driver info label
    if ($script:ui['DriverLabel'] -and $script:DriverInfo) {
        $driverText = "Driver: $($script:DriverInfo.CurrentDriver)"
        if ($script:NativeNVMeStatus -and $script:NativeNVMeStatus.IsActive) {
            $driverText = "Active: nvmedisk.sys (Native NVMe)"
        }
        $script:ui['DriverLabel'].Text = $driverText
    }

    # Button text/color based on state
    if ($status.Applied) {
        if ($script:ui['BtnApply']) { $script:ui['BtnApply'].Content = "REINSTALL" }
    }
    elseif (-not $status.Partial) {
        if ($script:ui['BtnApply']) { $script:ui['BtnApply'].Content = "APPLY PATCH" }
    }
}

# ===========================================================================
# SECTION 16: BACKUP & PATCH OPERATIONS
# ===========================================================================

function Get-PatchSnapshot {
    $snapshot = @{
        Timestamp = Get-Date -Format "HH:mm:ss"
        Status = Test-PatchStatus
        DriverActive = if ($script:NativeNVMeStatus) { $script:NativeNVMeStatus.ActiveDriver } else { "Unknown" }
        BypassIO = if ($script:BypassIOStatus) { $script:BypassIOStatus.Supported } else { $false }
        Components = @{}
    }
    foreach ($id in $script:Config.FeatureIDs) {
        $val = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
        $snapshot.Components[$id] = if ($val -and $val.$id -eq 1) { "Set (1)" } else { "Not Set" }
    }
    $snapshot.Components["SafeBootMinimal"] = if (Test-Path $script:Config.SafeBootMinimal) { "Present" } else { "Absent" }
    $snapshot.Components["SafeBootNetwork"] = if (Test-Path $script:Config.SafeBootNetwork) { "Present" } else { "Absent" }
    return $snapshot
}

function Show-BeforeAfterComparison {
    param($Before, $After, [string]$Operation = "Patch")
    if (-not $Before -or -not $After) { return }

    Write-Log ""
    Write-Log "========== BEFORE / AFTER ==========" -Level "INFO"
    Write-Log "  Operation: $Operation" -Level "INFO"

    $beforeStatus = if ($Before.Status.Applied) { "Applied ($($Before.Status.Count)/$($Before.Status.Total))" }
                    elseif ($Before.Status.Partial) { "Partial ($($Before.Status.Count)/$($Before.Status.Total))" }
                    else { "Not Applied" }
    $afterStatus  = if ($After.Status.Applied) { "Applied ($($After.Status.Count)/$($After.Status.Total))" }
                    elseif ($After.Status.Partial) { "Partial ($($After.Status.Count)/$($After.Status.Total))" }
                    else { "Not Applied" }

    if ($beforeStatus -ne $afterStatus) {
        Write-Log "  Status:  $beforeStatus  -->  $afterStatus" -Level "SUCCESS"
    } else {
        Write-Log "  Status:  $beforeStatus  (unchanged)" -Level "INFO"
    }

    foreach ($key in $After.Components.Keys) {
        $bVal = $Before.Components[$key]
        $aVal = $After.Components[$key]
        $friendlyName = if ($script:Config.FeatureNames.ContainsKey($key)) { $script:Config.FeatureNames[$key] } else { $key }
        if ($bVal -ne $aVal) {
            Write-Log "  $($friendlyName):  $bVal  -->  $aVal" -Level "SUCCESS"
        }
    }
    Write-Log "====================================" -Level "INFO"
    Write-Log ""
}

$script:BeforeSnapshot = $null

function Export-RegistryBackup {
    param([string]$Description = "NVMe_Backup")
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupFile = Join-Path $script:Config.WorkingDir "$($Description)_$timestamp.reg"
    Write-Log "Exporting registry backup to file..."
    try {
        $lines = [System.Collections.ArrayList]::new()
        [void]$lines.Add("Windows Registry Editor Version 5.00")
        [void]$lines.Add("")
        [void]$lines.Add("; NVMe Driver Patcher Registry Backup")
        [void]$lines.Add("; Created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
        [void]$lines.Add("; Description: $Description")
        [void]$lines.Add("")
        [void]$lines.Add("[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides]")
        if (Test-Path $script:Config.RegistryPath) {
            foreach ($id in $script:Config.FeatureIDs) {
                $val = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
                if ($val) {
                    $propValue = $val | Select-Object -ExpandProperty $id -ErrorAction SilentlyContinue
                    if ($null -ne $propValue) {
                        [void]$lines.Add("`"$id`"=dword:$('{0:x8}' -f $propValue)")
                    }
                }
            }
        }
        $lines -join "`r`n" | Out-File -FilePath $backupFile -Encoding Unicode -NoNewline
        Write-Log "Registry backup saved: $backupFile" -Level "SUCCESS"
        return $backupFile
    }
    catch { Write-Log "Failed to export registry backup: $($_.Exception.Message)" -Level "ERROR"; return $null }
}

function New-SafeRestorePoint {
    param([string]$Description = "NVMe Patcher Backup")

    Write-Log "Creating system backup..."
    Update-Progress -Value 10 -Status "Creating registry backup..."
    $regBackup = Export-RegistryBackup -Description "Pre_Patch"
    Update-Progress -Value 50 -Status "Creating restore point..."

    try {
        Checkpoint-Computer -Description $Description -RestorePointType "MODIFY_SETTINGS" -ErrorAction Stop
        Write-Log "System restore point created: '$Description'" -Level "SUCCESS"
        Update-Progress -Value 0 -Status ""
        return $true
    }
    catch {
        $errorMsg = $_.Exception.Message
        if ($errorMsg -match "1111|24.hour|frequency") {
            Write-Log "Note: Windows limits restore points (one per 24 hours)" -Level "WARNING"
            if ($regBackup) { Write-Log "Registry backup available as alternative" -Level "INFO" }
            Update-Progress -Value 0 -Status ""
            return $true
        }

        Write-Log "Restore point failed: $errorMsg" -Level "ERROR"
        if ($regBackup) {
            Write-Log "Registry backup is available" -Level "INFO"
            Update-Progress -Value 0 -Status ""
            return $true
        }

        Update-Progress -Value 0 -Status ""
        return $script:Config.ForceMode
    }
}

function Install-NVMePatch {
    Write-Log "========================================" -Level "INFO"
    Write-Log "STARTING PATCH INSTALLATION" -Level "INFO"
    Write-Log "========================================" -Level "INFO"
    Write-AppEventLog -Message "NVMe Driver Patch installation started" -EntryType "Information" -EventId 1000
    $script:BeforeSnapshot = Get-PatchSnapshot

    if (-not $script:Config.SilentMode) {
        Set-ButtonsEnabled -Enabled $false
    }

    $successCount = 0
    $appliedKeys = [System.Collections.ArrayList]::new()

    $effectiveTotal = $script:Config.TotalComponents
    $featureIDsToApply = [System.Collections.ArrayList]@($script:Config.FeatureIDs)
    if ($script:Config.IncludeServerKey) {
        [void]$featureIDsToApply.Add($script:Config.ServerFeatureID)
        $effectiveTotal = $script:Config.TotalComponents + 1
        Write-Log "Including optional Microsoft Server 2025 key (1176759950)" -Level "INFO"
    }

    try {
        # Step 0: Suspend BitLocker if active
        if ($script:BitLockerEnabled) {
            Write-Log "Suspending BitLocker for one reboot cycle..." -Level "INFO"
            try {
                Suspend-BitLocker -MountPoint "$env:SystemDrive" -RebootCount 1 -ErrorAction Stop
                Write-Log "BitLocker suspended - will auto-resume after reboot" -Level "SUCCESS"
            }
            catch {
                Write-Log "Could not suspend BitLocker: $($_.Exception.Message)" -Level "WARNING"
                Write-Log "Have your BitLocker recovery key ready before rebooting" -Level "WARNING"
            }
        }

        # Step 1: Backup
        Write-Log "Step 1/3: Creating system backup..."
        $restoreOK = New-SafeRestorePoint -Description "Pre-NVMe-Driver-Patch"
        if (-not $restoreOK) {
            Write-Log "Installation cancelled" -Level "WARNING"
            return $false
        }

        # Step 2: Apply registry components
        Write-Log "Step 2/3: Applying $effectiveTotal registry components..."
        Update-Progress -Value 60 -Status "Applying registry changes..."

        if (-not (Test-Path $script:Config.RegistryPath)) {
            Write-Log "Creating registry path: Overrides" -Level "INFO"
            New-Item -Path $script:Config.RegistryPath -Force | Out-Null
        }

        foreach ($id in $featureIDsToApply) {
            $friendlyName = if ($script:Config.FeatureNames.ContainsKey($id)) { $script:Config.FeatureNames[$id] } else { "Feature Flag" }
            try {
                New-ItemProperty -Path $script:Config.RegistryPath -Name $id -Value 1 -PropertyType DWORD -Force | Out-Null
                $verify = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
                if ($verify.$id -eq 1) {
                    Write-Log "  [OK] $id - $friendlyName" -Level "SUCCESS"
                    $successCount++
                    [void]$appliedKeys.Add(@{ Type = "Feature"; ID = $id })
                }
                else {
                    Write-Log "  [FAIL] $id - $friendlyName" -Level "ERROR"
                }
            }
            catch {
                Write-Log "  [FAIL] $id - $($_.Exception.Message)" -Level "ERROR"
            }
        }

        # SafeBoot Minimal
        try {
            if (-not (Test-Path $script:Config.SafeBootMinimal)) {
                New-Item -Path $script:Config.SafeBootMinimal -Force | Out-Null
            }
            Set-ItemProperty -Path $script:Config.SafeBootMinimal -Name "(Default)" -Value $script:Config.SafeBootValue -Force
            $val = Get-ItemProperty -Path $script:Config.SafeBootMinimal -Name "(Default)" -ErrorAction SilentlyContinue
            if ($val."(Default)" -eq $script:Config.SafeBootValue) {
                Write-Log "  [OK] SafeBoot Minimal Support" -Level "SUCCESS"
                $successCount++
                [void]$appliedKeys.Add(@{ Type = "SafeBoot"; ID = "Minimal" })
            }
            else {
                Write-Log "  [FAIL] SafeBoot Minimal Support" -Level "ERROR"
            }
        }
        catch {
            Write-Log "  [FAIL] SafeBoot Minimal: $($_.Exception.Message)" -Level "ERROR"
        }

        # SafeBoot Network
        try {
            if (-not (Test-Path $script:Config.SafeBootNetwork)) {
                New-Item -Path $script:Config.SafeBootNetwork -Force | Out-Null
            }
            Set-ItemProperty -Path $script:Config.SafeBootNetwork -Name "(Default)" -Value $script:Config.SafeBootValue -Force
            $val = Get-ItemProperty -Path $script:Config.SafeBootNetwork -Name "(Default)" -ErrorAction SilentlyContinue
            if ($val."(Default)" -eq $script:Config.SafeBootValue) {
                Write-Log "  [OK] SafeBoot Network Support" -Level "SUCCESS"
                $successCount++
                [void]$appliedKeys.Add(@{ Type = "SafeBoot"; ID = "Network" })
            }
            else {
                Write-Log "  [FAIL] SafeBoot Network Support" -Level "ERROR"
            }
        }
        catch {
            Write-Log "  [FAIL] SafeBoot Network: $($_.Exception.Message)" -Level "ERROR"
        }

        # Step 3: Validate
        Update-Progress -Value 95 -Status "Validating..."
        Write-Log "Step 3/3: Validating installation..."
        Write-Log "========================================" -Level "INFO"

        if ($successCount -eq $effectiveTotal) {
            Write-Log "Patch Status: SUCCESS - Applied $successCount/$effectiveTotal components" -Level "SUCCESS"
            Write-Log "Please RESTART your computer to apply changes" -Level "WARNING"
            Write-Log "After reboot: Drives should appear under 'Storage disks' using nvmedisk.sys" -Level "INFO"

            if ($script:BypassIOStatus -and -not $script:BypassIOStatus.Supported) {
                Write-Log "NOTE: BypassIO/DirectStorage not supported with Native NVMe - gaming impact possible" -Level "WARNING"
            }

            Write-AppEventLog -Message "NVMe Driver Patch applied successfully ($successCount/$effectiveTotal components)" -EntryType "Information" -EventId 1001
            Show-ToastNotification -Title "NVMe Patch Applied" -Message "All $effectiveTotal components applied successfully. Restart required." -Type "Success"

            $verifyScript = New-VerificationScript
            if ($verifyScript) { Write-Log "Verification script created: $verifyScript" -Level "INFO" }

            Update-Progress -Value 100 -Status "Complete!"
            Start-Sleep -Milliseconds 500
            Update-Progress -Value 0 -Status ""

            if (-not $script:Config.NoRestart -and -not $script:Config.SilentMode) {
                $restartMsg = "Patch applied successfully ($successCount/$effectiveTotal components).`n`n"
                $restartMsg += "Restart your computer now to enable the new NVMe driver?`n`n"
                $restartMsg += "(System will restart in $($script:Config.RestartDelay) seconds if you click Yes)`n`n"
                $restartMsg += "After reboot:`n"
                $restartMsg += "- Drives will move from 'Disk drives' to 'Storage disks'`n"
                $restartMsg += "- Driver changes from stornvme.sys to nvmedisk.sys`n"
                $restartMsg += "- A verification script has been created to confirm"

                $result = Show-ConfirmDialog -Title "Installation Complete" -Message $restartMsg
                if ($result) {
                    Write-Log "Initiating system restart in $($script:Config.RestartDelay) seconds..."
                    Start-Process "shutdown.exe" -ArgumentList "/r /t $($script:Config.RestartDelay) /c `"NVMe Driver Patch - Restarting in $($script:Config.RestartDelay) seconds. Save your work!`""
                }
            }
            return $true
        }
        else {
            Write-Log "Patch Status: PARTIAL - Applied $successCount/$effectiveTotal components" -Level "WARNING"

            Write-Log "Rolling back partial installation to prevent inconsistent state..." -Level "WARNING"
            Update-Progress -Value 96 -Status "Rolling back..."

            foreach ($applied in $appliedKeys) {
                try {
                    if ($applied.Type -eq "Feature") {
                        Remove-ItemProperty -Path $script:Config.RegistryPath -Name $applied.ID -Force -ErrorAction Stop
                        $friendlyName = if ($script:Config.FeatureNames.ContainsKey($applied.ID)) { $script:Config.FeatureNames[$applied.ID] } else { "Feature Flag" }
                        Write-Log "  [ROLLBACK] $($applied.ID) - $friendlyName" -Level "INFO"
                    }
                    elseif ($applied.Type -eq "SafeBoot") {
                        $path = if ($applied.ID -eq "Minimal") { $script:Config.SafeBootMinimal } else { $script:Config.SafeBootNetwork }
                        Remove-Item -Path $path -Force -ErrorAction Stop
                        Write-Log "  [ROLLBACK] SafeBoot $($applied.ID)" -Level "INFO"
                    }
                }
                catch {
                    Write-Log "  [ROLLBACK FAIL] $($applied.Type) $($applied.ID): $($_.Exception.Message)" -Level "ERROR"
                }
            }

            Write-Log "Rollback complete - system returned to pre-patch state" -Level "WARNING"
            Write-AppEventLog -Message "NVMe Driver Patch rolled back after partial failure ($successCount/$effectiveTotal components)" -EntryType "Warning" -EventId 2001
            Show-ToastNotification -Title "NVMe Patch Failed" -Message "Only $successCount of $effectiveTotal components applied. Changes rolled back." -Type "Warning"
            Update-Progress -Value 0 -Status ""
            return $false
        }
    }
    catch {
        Write-Log "INSTALLATION FAILED: $($_.Exception.Message)" -Level "ERROR"
        Write-AppEventLog -Message "NVMe Driver Patch installation failed: $($_.Exception.Message)" -EntryType "Error" -EventId 3001
        Update-Progress -Value 0 -Status ""
        return $false
    }
    finally {
        if (-not $script:Config.SilentMode) {
            Set-ButtonsEnabled -Enabled $true
            Update-StatusDisplay
        }
        if ($script:BeforeSnapshot) {
            $afterSnapshot = Get-PatchSnapshot
            Show-BeforeAfterComparison -Before $script:BeforeSnapshot -After $afterSnapshot -Operation "Install Patch"
            $script:BeforeSnapshot = $null
        }
        try { Test-PatchAppliedSinceLastRun | Out-Null } catch { <# State save best-effort #> }
        Write-Log "========================================" -Level "INFO"
    }
}

function Uninstall-NVMePatch {
    Write-Log "========================================" -Level "INFO"
    Write-Log "STARTING PATCH REMOVAL" -Level "INFO"
    Write-Log "========================================" -Level "INFO"
    Write-AppEventLog -Message "NVMe Driver Patch removal started" -EntryType "Information" -EventId 1000
    $script:BeforeSnapshot = Get-PatchSnapshot

    if (-not $script:Config.SilentMode) {
        Set-ButtonsEnabled -Enabled $false
    }

    Update-Progress -Value 10 -Status "Creating backup..."
    Export-RegistryBackup -Description "Pre_Removal"
    $removedCount = 0

    $allFeatureIDs = [System.Collections.ArrayList]@($script:Config.FeatureIDs)
    [void]$allFeatureIDs.Add($script:Config.ServerFeatureID)

    try {
        Write-Log "Removing registry components..."
        Update-Progress -Value 30 -Status "Removing feature flags..."

        if (Test-Path $script:Config.RegistryPath) {
            foreach ($id in $allFeatureIDs) {
                $exists = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
                if ($exists) {
                    try {
                        Remove-ItemProperty -Path $script:Config.RegistryPath -Name $id -Force -ErrorAction Stop
                        $friendlyName = if ($script:Config.FeatureNames.ContainsKey($id)) { $script:Config.FeatureNames[$id] } else { "Feature Flag" }
                        Write-Log "  [REMOVED] $id - $friendlyName" -Level "SUCCESS"
                        $removedCount++
                    }
                    catch {
                        Write-Log "  [FAIL] Failed to remove $($id): $($_.Exception.Message)" -Level "ERROR"
                    }
                }
                else {
                    Write-Log "  [ABSENT] Feature Flag: $id (Already gone)" -Level "INFO"
                }
            }
        }

        Update-Progress -Value 60 -Status "Removing SafeBoot keys..."

        if (Test-Path $script:Config.SafeBootMinimal) {
            try {
                Remove-Item -Path $script:Config.SafeBootMinimal -Force -ErrorAction Stop
                Write-Log "  [REMOVED] SafeBoot Minimal" -Level "SUCCESS"
                $removedCount++
            }
            catch {
                Write-Log "  [FAIL] SafeBoot Minimal: $($_.Exception.Message)" -Level "ERROR"
            }
        }

        if (Test-Path $script:Config.SafeBootNetwork) {
            try {
                Remove-Item -Path $script:Config.SafeBootNetwork -Force -ErrorAction Stop
                Write-Log "  [REMOVED] SafeBoot Network" -Level "SUCCESS"
                $removedCount++
            }
            catch {
                Write-Log "  [FAIL] SafeBoot Network: $($_.Exception.Message)" -Level "ERROR"
            }
        }

        Update-Progress -Value 90 -Status "Validating..."
        Write-Log "========================================" -Level "INFO"
        Write-Log "Patch Status: REMOVED - Removed $removedCount components" -Level "SUCCESS"
        Write-Log "After reboot: Drives will return to 'Disk drives' using stornvme.sys" -Level "INFO"
        Write-Log "Please RESTART your computer" -Level "WARNING"
        Write-AppEventLog -Message "NVMe Driver Patch removed ($removedCount components)" -EntryType "Information" -EventId 1001
        Show-ToastNotification -Title "NVMe Patch Removed" -Message "Patch components removed. Restart required." -Type "Info"

        Update-Progress -Value 100 -Status "Complete!"
        Start-Sleep -Milliseconds 500
        Update-Progress -Value 0 -Status ""
        return $true
    }
    catch {
        Write-Log "REMOVAL FAILED: $($_.Exception.Message)" -Level "ERROR"
        Write-AppEventLog -Message "NVMe Driver Patch removal failed: $($_.Exception.Message)" -EntryType "Error" -EventId 3001
        Update-Progress -Value 0 -Status ""
        return $false
    }
    finally {
        if (-not $script:Config.SilentMode) {
            Set-ButtonsEnabled -Enabled $true
            Update-StatusDisplay
        }
        if ($script:BeforeSnapshot) {
            $afterSnapshot = Get-PatchSnapshot
            Show-BeforeAfterComparison -Before $script:BeforeSnapshot -After $afterSnapshot -Operation "Remove Patch"
            $script:BeforeSnapshot = $null
        }
        Write-Log "========================================" -Level "INFO"
    }
}

function Show-ConfirmDialog {
    param(
        [string]$Title,
        [string]$Message,
        [string]$WarningText = "",
        [bool]$CheckNVMe = $false
    )

    # VeraCrypt hard block
    if ($Title -eq "Apply Patch" -and $script:VeraCryptDetected) {
        Write-Log "BLOCKED: VeraCrypt system encryption detected - nvmedisk.sys breaks VeraCrypt boot" -Level "ERROR"
        Write-Log "See: https://github.com/veracrypt/VeraCrypt/issues/1640" -Level "ERROR"
        if (-not $script:Config.SilentMode -and $script:window) {
            $blockMsg = "CANNOT APPLY PATCH`n`nVeraCrypt system encryption detected. Enabling the native NVMe driver (nvmedisk.sys) breaks VeraCrypt boot entirely.`n`nThis is a known critical incompatibility (VeraCrypt Issue #1640).`n`nYou must either:`n- Decrypt your system drive with VeraCrypt first, OR`n- Wait for a VeraCrypt update that supports nvmedisk.sys`n`nThis block cannot be overridden."
            Show-ThemedDialog -Message $blockMsg -Title "VeraCrypt Incompatibility" -Icon "Error"
        }
        return $false
    }

    if ($script:Config.ForceMode -or $script:Config.SkipWarnings) { return $true }

    # Build comprehensive message
    $warnings = [System.Collections.ArrayList]::new()

    if ($CheckNVMe -and -not $script:HasNVMeDrives) {
        [void]$warnings.Add("[!] NO NVMe DRIVES DETECTED - This patch only affects NVMe drives using the Windows inbox driver.")
    }

    if ($script:BitLockerEnabled) {
        [void]$warnings.Add("[!] BITLOCKER ACTIVE - Will be automatically suspended for one reboot to prevent recovery key prompt.")
    }

    foreach ($sw in $script:IncompatibleSoftware) {
        if ($sw.Severity -ne "Critical") {
            [void]$warnings.Add("[i] $($sw.Name): $($sw.Message)")
        }
    }

    if ($script:DriverInfo -and $script:DriverInfo.HasThirdParty) {
        [void]$warnings.Add("[i] THIRD-PARTY DRIVER: $($script:DriverInfo.ThirdPartyName) - This patch only affects the Windows inbox driver and may have no effect.")
    }

    if ($Title -eq "Apply Patch" -and $script:BuildDetails -and -not $script:BuildDetails.Is24H2OrLater) {
        [void]$warnings.Add("[!] OLDER BUILD: $($script:BuildDetails.DisplayVersion) (Build $($script:BuildDetails.BuildNumber)) - Designed for 24H2+ (Build 26100+). Results may be unpredictable.")
    }

    if ($Title -eq "Apply Patch") {
        [void]$warnings.Add("[i] GAMING NOTE: Native NVMe does not support BypassIO. DirectStorage games may have higher CPU usage.")
    }

    $fullMessage = $Message
    if ($WarningText) { $fullMessage += "`n`nWARNING: $WarningText" }

    if ($warnings.Count -gt 0) {
        $fullMessage += "`n`n--- NOTICES ---`n"
        $fullMessage += ($warnings -join "`n`n")
    }

    $fullMessage += "`n`nProceed?"

    if ($script:Config.SilentMode) {
        return $true
    }

    $result = Show-ThemedDialog -Message $fullMessage -Title $Title -Buttons "YesNo" -Icon "Question"
    if ($result -ne 'Yes') {
        Write-Log "Operation cancelled by user" -Level "WARNING"
        return $false
    }
    return $true
}

function Show-ThemedDialog {
    param([string]$Message, [string]$Title = "NVMe Driver Patcher", [string]$Buttons = "OK", [string]$Icon = "Information")
    $dlgXaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize" SizeToContent="WidthAndHeight" MinWidth="380" MaxWidth="520"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False" Topmost="True">
    <Border CornerRadius="10" Background="#FF09090b" BorderBrush="#FF27272a" BorderThickness="1" Padding="0" Margin="12">
        <Border.Effect><DropShadowEffect BlurRadius="20" ShadowDepth="4" Opacity="0.5" Direction="270" Color="#000000"/></Border.Effect>
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <Border Grid.Row="0" Background="#FF111113" CornerRadius="10,10,0,0" Padding="16,10">
                <TextBlock Name="DlgTitle" FontSize="13" FontWeight="SemiBold" Foreground="#FFfafafa" FontFamily="Segoe UI"/>
            </Border>
            <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="20,20,20,16">
                <Canvas Name="IconCanvas" Width="28" Height="28" Margin="0,0,16,0" VerticalAlignment="Top"/>
                <TextBlock Name="DlgMessage" FontSize="13" Foreground="#FFd4d4d8" FontFamily="Segoe UI" TextWrapping="Wrap" MaxWidth="400" VerticalAlignment="Center"/>
            </StackPanel>
            <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="20,4,20,18">
                <Button Name="BtnNo" Content="No" Width="80" Height="32" FontSize="12" FontWeight="SemiBold" Cursor="Hand" Margin="0,0,8,0" Visibility="Collapsed">
                    <Button.Template><ControlTemplate TargetType="Button">
                        <Border x:Name="bd" Background="#FF18181b" CornerRadius="6" BorderBrush="#FF3f3f46" BorderThickness="1">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="#FF27272a"/><Setter TargetName="bd" Property="BorderBrush" Value="#FF52525b"/></Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate></Button.Template>
                    <Button.Foreground><SolidColorBrush Color="#FFd4d4d8"/></Button.Foreground>
                </Button>
                <Button Name="BtnYes" Content="Yes" Width="80" Height="32" FontSize="12" FontWeight="SemiBold" Cursor="Hand" Margin="0,0,0,0" Visibility="Collapsed">
                    <Button.Template><ControlTemplate TargetType="Button">
                        <Border x:Name="bd" Background="#FF3b82f6" CornerRadius="6">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="#FF2563eb"/></Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate></Button.Template>
                    <Button.Foreground><SolidColorBrush Color="#FFfafafa"/></Button.Foreground>
                </Button>
                <Button Name="BtnOK" Content="OK" Width="80" Height="32" FontSize="12" FontWeight="SemiBold" Cursor="Hand" Visibility="Collapsed">
                    <Button.Template><ControlTemplate TargetType="Button">
                        <Border x:Name="bd" Background="#FF3b82f6" CornerRadius="6">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="#FF2563eb"/></Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate></Button.Template>
                    <Button.Foreground><SolidColorBrush Color="#FFfafafa"/></Button.Foreground>
                </Button>
            </StackPanel>
        </Grid>
    </Border>
</Window>
"@
    $dlgReader = New-Object System.Xml.XmlNodeReader ([xml]$dlgXaml)
    $dlg = [Windows.Markup.XamlReader]::Load($dlgReader)
    $dlg.FindName("DlgTitle").Text = $Title
    $dlg.FindName("DlgMessage").Text = $Message
    $script:dlgResult = "OK"

    # Icon
    $canvas = $dlg.FindName("IconCanvas")
    $iconColor = "#FF3b82f6"
    switch ($Icon) {
        "Error"       { $iconColor = "#FFef4444" }
        "Warning"     { $iconColor = "#FFf59e0b" }
        "Question"    { $iconColor = "#FF3b82f6" }
        "Information" { $iconColor = "#FF3b82f6" }
    }
    $ellipse = New-Object System.Windows.Shapes.Ellipse
    $ellipse.Width = 28; $ellipse.Height = 28
    $ellipse.Fill = [System.Windows.Media.BrushConverter]::new().ConvertFromString($iconColor)
    $ellipse.Opacity = 0.15
    $canvas.Children.Add($ellipse) | Out-Null
    $path = New-Object System.Windows.Shapes.Path
    $path.Stroke = [System.Windows.Media.BrushConverter]::new().ConvertFromString($iconColor)
    $path.StrokeThickness = 2
    switch ($Icon) {
        "Error"       { $path.Data = [System.Windows.Media.Geometry]::Parse("M 10,10 L 18,18 M 18,10 L 10,18") }
        "Warning"     { $path.Data = [System.Windows.Media.Geometry]::Parse("M 14,8 L 14,15 M 14,18 L 14,18.5"); $path.StrokeThickness = 2.5; $path.StrokeStartLineCap = "Round"; $path.StrokeEndLineCap = "Round" }
        "Question"    { $path.Data = [System.Windows.Media.Geometry]::Parse("M 11,10 C 11,7 17,7 17,10 C 17,12.5 14,12.5 14,15 M 14,18 L 14,18.5"); $path.StrokeStartLineCap = "Round"; $path.StrokeEndLineCap = "Round" }
        "Information" { $path.Data = [System.Windows.Media.Geometry]::Parse("M 14,9 L 14,9.5 M 14,12 L 14,19"); $path.StrokeThickness = 2.5; $path.StrokeStartLineCap = "Round"; $path.StrokeEndLineCap = "Round" }
    }
    [System.Windows.Controls.Canvas]::SetLeft($path, 0)
    [System.Windows.Controls.Canvas]::SetTop($path, 0)
    $canvas.Children.Add($path) | Out-Null

    # Buttons
    $btnOK = $dlg.FindName("BtnOK"); $btnYes = $dlg.FindName("BtnYes"); $btnNo = $dlg.FindName("BtnNo")
    if ($Buttons -eq "YesNo") { $btnYes.Visibility = "Visible"; $btnNo.Visibility = "Visible" }
    else { $btnOK.Visibility = "Visible" }
    $btnOK.Add_Click({ $script:dlgResult = "OK"; $dlg.Close() })
    $btnYes.Add_Click({ $script:dlgResult = "Yes"; $dlg.Close() })
    $btnNo.Add_Click({ $script:dlgResult = "No"; $dlg.Close() })
    try { $dlg.Owner = $script:window } catch {}
    $dlg.Add_MouseLeftButtonDown({ $dlg.DragMove() })
    $dlg.ShowDialog() | Out-Null
    return $script:dlgResult
}

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

    if (-not $Apply -and -not $Remove -and -not $Status) {
        Write-Error "Silent mode requires -Apply, -Remove, or -Status parameter."
        exit 3
    }

    if (($Apply -and $Remove) -or ($Apply -and $Status) -or ($Remove -and $Status)) {
        Write-Error "Cannot combine -Apply, -Remove, and -Status parameters."
        exit 3
    }

    Invoke-PreflightChecks | Out-Null

    if (-not (Test-WindowsCompatibility) -and -not $Force) {
        Write-Error "Windows compatibility check failed."
        exit 1
    }

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

    $logFile = Save-LogFile -Suffix "_silent"
    if ($logFile) { Write-Host "Log saved to: $logFile" }

    if ($script:AppMutex) { try { $script:AppMutex.ReleaseMutex(); $script:AppMutex.Dispose() } catch { <# Mutex cleanup best-effort #> } }

    exit $exitCode
}

# ===========================================================================
# SECTION 18: GUI MODE - WPF CONSTRUCTION
# ===========================================================================

$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="NVMe Driver Patcher" Height="820" Width="1100"
        WindowStyle="None" ResizeMode="NoResize" AllowsTransparency="True"
        Background="#00000000" WindowStartupLocation="CenterScreen">
    <Window.Resources>
        <!-- CheckBox -->
        <Style x:Key="DarkCheckBox" TargetType="CheckBox">
            <Setter Property="Foreground" Value="#FFd4d4d8"/><Setter Property="FontSize" Value="12"/><Setter Property="Margin" Value="0,4"/><Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><StackPanel Orientation="Horizontal">
                <Border x:Name="box" Width="18" Height="18" CornerRadius="4" Background="#FF18181b" BorderBrush="#FF3f3f46" BorderThickness="1.5" Margin="0,0,10,0">
                    <Path x:Name="check" Data="M 3 6 L 6 9 L 11 3" Stroke="#FF3b82f6" StrokeThickness="2" Visibility="Collapsed" Margin="1,1,0,0"/></Border>
                <ContentPresenter VerticalAlignment="Center"/>
            </StackPanel><ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="check" Property="Visibility" Value="Visible"/><Setter TargetName="box" Property="Background" Value="#FF0c1a3d"/><Setter TargetName="box" Property="BorderBrush" Value="#FF3b82f6"/></Trigger>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="box" Property="BorderBrush" Value="#FF52525b"/></Trigger>
            </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
        </Style>
        <!-- Action Button -->
        <Style x:Key="ActionButton" TargetType="Button">
            <Setter Property="Height" Value="42"/><Setter Property="FontWeight" Value="SemiBold"/><Setter Property="FontSize" Value="13"/><Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Foreground" Value="#FFfafafa"/><Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                <Border x:Name="border" Background="{TemplateBinding Background}" CornerRadius="6" Padding="20,0">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="border" Property="Opacity" Value="0.88"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="border" Property="Opacity" Value="0.3"/></Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
        </Style>
        <!-- Secondary Button -->
        <Style x:Key="SecondaryButton" TargetType="Button">
            <Setter Property="Height" Value="36"/><Setter Property="FontSize" Value="11"/><Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Foreground" Value="#FFfafafa"/><Setter Property="Background" Value="#FF18181b"/><Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                <Border x:Name="border" Background="{TemplateBinding Background}" CornerRadius="6" BorderBrush="#FF27272a" BorderThickness="1" Padding="16,0">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="border" Property="Background" Value="#FF27272a"/><Setter TargetName="border" Property="BorderBrush" Value="#FF3f3f46"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="border" Property="Opacity" Value="0.3"/></Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
        </Style>
        <!-- Rounded ProgressBar -->
        <ControlTemplate x:Key="RoundProgress" TargetType="ProgressBar">
            <Grid><Border x:Name="PART_Track" CornerRadius="3" Background="#FF27272a" Height="6"/>
                <Border x:Name="PART_Indicator" CornerRadius="3" HorizontalAlignment="Left" Height="6" Background="{TemplateBinding Foreground}"/></Grid>
        </ControlTemplate>
        <!-- Tooltip -->
        <Style TargetType="ToolTip">
            <Setter Property="Background" Value="#FF18181b"/><Setter Property="Foreground" Value="#FFd4d4d8"/><Setter Property="BorderBrush" Value="#FF3f3f46"/><Setter Property="BorderThickness" Value="1"/>
            <Setter Property="FontSize" Value="11"/><Setter Property="Padding" Value="10,6"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToolTip">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}">
                    <Border.Effect><DropShadowEffect BlurRadius="12" ShadowDepth="2" Opacity="0.35" Direction="270"/></Border.Effect>
                    <ContentPresenter/></Border>
            </ControlTemplate></Setter.Value></Setter>
        </Style>
        <!-- ScrollViewer dark -->
        <Style TargetType="ScrollViewer">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollViewer">
                <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                    <ScrollContentPresenter Grid.Column="0"/>
                    <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Orientation="Vertical"
                               Value="{TemplateBinding VerticalOffset}" Maximum="{TemplateBinding ScrollableHeight}"
                               ViewportSize="{TemplateBinding ViewportHeight}"
                               Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}" Width="8" Margin="2,0,2,0"/>
                </Grid>
            </ControlTemplate></Setter.Value></Setter>
        </Style>
        <Style TargetType="ScrollBar">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
                <Grid Background="Transparent"><Track x:Name="PART_Track" IsDirectionReversed="True">
                    <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb">
                        <Border CornerRadius="4" Background="#FF3f3f46" Margin="0,2"/>
                    </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                </Track></Grid>
            </ControlTemplate></Setter.Value></Setter>
        </Style>
    </Window.Resources>

    <Grid Margin="16">
        <Border CornerRadius="12" Background="#FF09090b" BorderBrush="#FF1a1a1e" BorderThickness="1" ClipToBounds="False">
            <Border.Effect><DropShadowEffect BlurRadius="28" ShadowDepth="0" Opacity="0.5" Color="#000000"/></Border.Effect>
            <Grid ClipToBounds="True">
                <!-- Top accent gradient -->
                <Border Height="2" VerticalAlignment="Top" CornerRadius="12,12,0,0" Panel.ZIndex="2"><Border.Background>
                    <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                        <GradientStop Color="#FF0c2d5e" Offset="0"/><GradientStop Color="#FF3b82f6" Offset="0.35"/>
                        <GradientStop Color="#FF60a5fa" Offset="0.55"/><GradientStop Color="#FF0c2d5e" Offset="1"/>
                    </LinearGradientBrush></Border.Background></Border>

                <Grid><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>

                    <!-- TITLE BAR -->
                    <Border Grid.Row="0" Background="#FF09090b" Padding="24,16,24,0">
                        <Grid>
                            <StackPanel Orientation="Horizontal" HorizontalAlignment="Left" VerticalAlignment="Center">
                                <Border Width="36" Height="36" CornerRadius="8" Background="#FF1e3a5f" Margin="0,0,14,0" VerticalAlignment="Center">
                                    <TextBlock Text="NVMe" Foreground="#FF3b82f6" FontSize="9" FontWeight="Black" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                </Border>
                                <StackPanel VerticalAlignment="Center">
                                    <TextBlock Text="NVMe Driver Patcher" Foreground="#FFfafafa" FontSize="17" FontWeight="Bold"/>
                                    <TextBlock Text="Enable Server 2025 Native NVMe on Windows 11" Foreground="#FF52525b" FontSize="10" Margin="0,-1,0,0"/>
                                </StackPanel>
                            </StackPanel>
                            <StackPanel HorizontalAlignment="Right" VerticalAlignment="Center" Orientation="Horizontal">
                                <Border CornerRadius="10" Background="#FF18181b" Padding="10,4" Margin="0,0,12,0" VerticalAlignment="Center">
                                    <TextBlock Name="VersionLabel" Foreground="#FF3b82f6" FontSize="10" FontWeight="SemiBold"/>
                                </Border>
                                <Button Name="MinimizeBtn" Content="&#x2013;" Width="32" Height="28" Background="Transparent" Foreground="#FF52525b" BorderThickness="0" FontSize="12" FontWeight="Bold" Cursor="Hand">
                                    <Button.Template><ControlTemplate TargetType="Button"><Border x:Name="b" Background="Transparent" CornerRadius="6"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                                        <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="#FF27272a"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Button.Template></Button>
                                <Button Name="CloseBtn" Content="&#x2715;" Width="32" Height="28" Background="Transparent" Foreground="#FF52525b" BorderThickness="0" FontSize="11" Cursor="Hand">
                                    <Button.Template><ControlTemplate TargetType="Button"><Border x:Name="b" Background="Transparent" CornerRadius="6"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                                        <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="#FFdc2626"/><Setter Property="Foreground" Value="#FFfafafa"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Button.Template></Button>
                            </StackPanel>
                        </Grid>
                    </Border>

                    <!-- CONTENT -->
                    <Border Grid.Row="1" Margin="24,14,24,24">
                        <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="16"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>

                            <!-- LEFT COLUMN -->
                            <ScrollViewer Grid.Column="0" VerticalScrollBarVisibility="Auto">
                                <StackPanel>
                                    <!-- PATCH STATUS -->
                                    <Border Background="#FF111113" CornerRadius="10" Padding="20,16" BorderBrush="#FF1a1a1e" BorderThickness="1" Margin="0,0,0,12">
                                        <Grid>
                                            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                                                <Ellipse Name="StatusDot" Width="12" Height="12" Fill="#FF71717a" Margin="0,0,12,0"/>
                                                <TextBlock Name="StatusLabel" Text="Checking..." Foreground="#FFfafafa" FontSize="16" FontWeight="SemiBold" VerticalAlignment="Center"/>
                                            </StackPanel>
                                            <TextBlock Name="DriverLabel" HorizontalAlignment="Right" Foreground="#FF52525b" FontSize="10" VerticalAlignment="Center"/>
                                        </Grid>
                                    </Border>

                                    <!-- ACTIONS -->
                                    <Border Background="#FF111113" CornerRadius="10" Padding="20,16" BorderBrush="#FF1a1a1e" BorderThickness="1" Margin="0,0,0,12">
                                        <StackPanel>
                                            <TextBlock Text="ACTIONS" Foreground="#FF52525b" FontSize="10" FontWeight="Bold" Margin="0,0,0,12"/>
                                            <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="10"/><ColumnDefinition/></Grid.ColumnDefinitions>
                                                <Button Name="BtnApply" Content="APPLY PATCH" Grid.Column="0" Background="#FF166534" Style="{StaticResource ActionButton}"/>
                                                <Button Name="BtnRemove" Content="REMOVE PATCH" Grid.Column="2" Background="#FF18181b" Style="{StaticResource ActionButton}">
                                                    <Button.Template><ControlTemplate TargetType="Button">
                                                        <Border x:Name="border" Background="#FF18181b" CornerRadius="6" BorderBrush="#FF27272a" BorderThickness="1" Padding="20,0">
                                                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                                                        <ControlTemplate.Triggers>
                                                            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="border" Property="Background" Value="#FF27272a"/></Trigger>
                                                            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="border" Property="Opacity" Value="0.3"/></Trigger>
                                                        </ControlTemplate.Triggers>
                                                    </ControlTemplate></Button.Template>
                                                </Button>
                                            </Grid>
                                            <Grid Margin="0,10,0,0"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="8"/><ColumnDefinition/><ColumnDefinition Width="8"/><ColumnDefinition/></Grid.ColumnDefinitions>
                                                <Button Name="BtnBackup" Content="BACKUP" Grid.Column="0" Style="{StaticResource SecondaryButton}"/>
                                                <Button Name="BtnBenchmark" Content="BENCHMARK" Grid.Column="2" Style="{StaticResource SecondaryButton}">
                                                    <Button.Foreground><SolidColorBrush Color="#FF3b82f6"/></Button.Foreground></Button>
                                                <Button Name="BtnDiagnostics" Content="DIAGNOSTICS" Grid.Column="4" Style="{StaticResource SecondaryButton}"/>
                                            </Grid>
                                        </StackPanel>
                                    </Border>

                                    <!-- PREFLIGHT CHECKS -->
                                    <Border Background="#FF111113" CornerRadius="10" Padding="20,16" BorderBrush="#FF1a1a1e" BorderThickness="1" Margin="0,0,0,12">
                                        <StackPanel>
                                            <TextBlock Text="SYSTEM OVERVIEW" Foreground="#FF52525b" FontSize="10" FontWeight="Bold" Margin="0,0,0,12"/>
                                            <Grid Name="CheckGrid">
                                                <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
                                                <Grid.RowDefinitions>
                                                    <RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/><RowDefinition/>
                                                </Grid.RowDefinitions>
                                                <StackPanel Grid.Row="0" Grid.Column="0" Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotBuild" Width="6" Height="6" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBlock Text="Build" Foreground="#FF71717a" FontSize="11" Width="70"/><TextBlock Name="ValBuild" Text="Checking..." Foreground="#FF52525b" FontSize="11"/></StackPanel>
                                                <StackPanel Grid.Row="1" Grid.Column="0" Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotNVMe" Width="6" Height="6" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBlock Text="NVMe" Foreground="#FF71717a" FontSize="11" Width="70"/><TextBlock Name="ValNVMe" Text="Checking..." Foreground="#FF52525b" FontSize="11"/></StackPanel>
                                                <StackPanel Grid.Row="2" Grid.Column="0" Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotBitLocker" Width="6" Height="6" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBlock Text="BitLocker" Foreground="#FF71717a" FontSize="11" Width="70"/><TextBlock Name="ValBitLocker" Text="Checking..." Foreground="#FF52525b" FontSize="11"/></StackPanel>
                                                <StackPanel Grid.Row="3" Grid.Column="0" Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotVeraCrypt" Width="6" Height="6" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBlock Text="VeraCrypt" Foreground="#FF71717a" FontSize="11" Width="70"/><TextBlock Name="ValVeraCrypt" Text="Checking..." Foreground="#FF52525b" FontSize="11"/></StackPanel>
                                                <StackPanel Grid.Row="4" Grid.Column="0" Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotDriver" Width="6" Height="6" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBlock Text="Driver" Foreground="#FF71717a" FontSize="11" Width="70"/><TextBlock Name="ValDriver" Text="Checking..." Foreground="#FF52525b" FontSize="11"/></StackPanel>
                                                <StackPanel Grid.Row="0" Grid.Column="1" Orientation="Horizontal" Margin="0,3"><Ellipse Name="Dot3rdParty" Width="6" Height="6" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBlock Text="3rd Party" Foreground="#FF71717a" FontSize="11" Width="70"/><TextBlock Name="Val3rdParty" Text="Checking..." Foreground="#FF52525b" FontSize="11"/></StackPanel>
                                                <StackPanel Grid.Row="1" Grid.Column="1" Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotCompat" Width="6" Height="6" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBlock Text="Compat." Foreground="#FF71717a" FontSize="11" Width="70"/><TextBlock Name="ValCompat" Text="Checking..." Foreground="#FF52525b" FontSize="11"/></StackPanel>
                                                <StackPanel Grid.Row="2" Grid.Column="1" Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotSysProt" Width="6" Height="6" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBlock Text="Sys Prot." Foreground="#FF71717a" FontSize="11" Width="70"/><TextBlock Name="ValSysProt" Text="Checking..." Foreground="#FF52525b" FontSize="11"/></StackPanel>
                                                <StackPanel Grid.Row="3" Grid.Column="1" Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotBypassIO" Width="6" Height="6" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBlock Text="BypassIO" Foreground="#FF71717a" FontSize="11" Width="70"/><TextBlock Name="ValBypassIO" Text="Checking..." Foreground="#FF52525b" FontSize="11"/></StackPanel>
                                            </Grid>
                                            <Border Height="1" Background="#FF1a1a1e" Margin="0,12,0,12"/>
                                            <CheckBox Name="ChkServerKey" Content="Include Microsoft Server 2025 key (1176759950)" Style="{StaticResource DarkCheckBox}"/>
                                            <CheckBox Name="ChkSkipWarnings" Content="Skip confirmation warnings (experienced users)" Style="{StaticResource DarkCheckBox}"/>
                                            <TextBlock Text="Note: Native NVMe does not yet support BypassIO. DirectStorage games may see higher CPU usage." Foreground="#FFa16207" FontSize="10" FontStyle="Italic" TextWrapping="Wrap" Margin="0,10,0,0"/>
                                        </StackPanel>
                                    </Border>

                                    <!-- REGISTRY COMPONENTS -->
                                    <Border Background="#FF111113" CornerRadius="10" Padding="20,16" BorderBrush="#FF1a1a1e" BorderThickness="1" Margin="0,0,0,12">
                                        <StackPanel>
                                            <TextBlock Text="REGISTRY COMPONENTS" Foreground="#FF52525b" FontSize="10" FontWeight="Bold" Margin="0,0,0,10"/>
                                            <TextBlock Text="FEATURE FLAGS" Foreground="#FF3f3f46" FontSize="9" FontWeight="Bold" Margin="0,0,0,6"/>
                                            <StackPanel Name="FlagList">
                                                <StackPanel Orientation="Horizontal" Margin="0,3"><Ellipse Name="Dot735" Width="8" Height="8" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,10,0"/><TextBlock Text="735209102" Foreground="#FFfafafa" FontSize="11" FontFamily="Cascadia Code,Consolas" Width="100"/><TextBlock Text="NativeNVMeStackForGeClient" Foreground="#FF52525b" FontSize="10"/></StackPanel>
                                                <StackPanel Orientation="Horizontal" Margin="0,3"><Ellipse Name="Dot1853" Width="8" Height="8" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,10,0"/><TextBlock Text="1853569164" Foreground="#FFfafafa" FontSize="11" FontFamily="Cascadia Code,Consolas" Width="100"/><TextBlock Text="UxAccOptimization" Foreground="#FF52525b" FontSize="10"/></StackPanel>
                                                <StackPanel Orientation="Horizontal" Margin="0,3"><Ellipse Name="Dot156" Width="8" Height="8" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,10,0"/><TextBlock Text="156965516" Foreground="#FFfafafa" FontSize="11" FontFamily="Cascadia Code,Consolas" Width="100"/><TextBlock Text="Standalone_Future" Foreground="#FF52525b" FontSize="10"/></StackPanel>
                                                <StackPanel Orientation="Horizontal" Margin="0,3"><Ellipse Name="Dot1176" Width="8" Height="8" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,10,0"/><TextBlock Text="1176759950" Foreground="#FFfafafa" FontSize="11" FontFamily="Cascadia Code,Consolas" Width="100"/><TextBlock Text="Server 2025 (optional)" Foreground="#FF52525b" FontSize="10"/></StackPanel>
                                            </StackPanel>
                                            <Border Height="1" Background="#FF1a1a1e" Margin="0,10,0,10"/>
                                            <TextBlock Text="SAFE MODE SUPPORT (CRITICAL)" Foreground="#FF3f3f46" FontSize="9" FontWeight="Bold" Margin="0,0,0,6"/>
                                            <StackPanel Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotSafeMin" Width="8" Height="8" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,10,0"/><TextBlock Text="SafeBoot" Foreground="#FFfafafa" FontSize="11" FontFamily="Cascadia Code,Consolas" Width="100"/><TextBlock Text="Minimal -- BSOD prevention" Foreground="#FF52525b" FontSize="10"/></StackPanel>
                                            <StackPanel Orientation="Horizontal" Margin="0,3"><Ellipse Name="DotSafeNet" Width="8" Height="8" Fill="#FF71717a" VerticalAlignment="Center" Margin="0,0,10,0"/><TextBlock Text="SafeBoot/Net" Foreground="#FFfafafa" FontSize="11" FontFamily="Cascadia Code,Consolas" Width="100"/><TextBlock Text="Network -- Safe Mode w/ Networking" Foreground="#FF52525b" FontSize="10"/></StackPanel>
                                        </StackPanel>
                                    </Border>

                                    <!-- DETECTED DRIVES -->
                                    <Border Background="#FF111113" CornerRadius="10" Padding="20,16" BorderBrush="#FF1a1a1e" BorderThickness="1">
                                        <StackPanel>
                                            <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
                                                <TextBlock Text="DETECTED DRIVES" Foreground="#FF52525b" FontSize="10" FontWeight="Bold"/>
                                                <TextBlock Text="  green = NVMe    gray = other" Foreground="#FF3f3f46" FontSize="9" Margin="10,1,0,0"/>
                                            </StackPanel>
                                            <StackPanel Name="DriveList"/>
                                        </StackPanel>
                                    </Border>
                                </StackPanel>
                            </ScrollViewer>

                            <!-- RIGHT COLUMN: Activity Log -->
                            <Border Grid.Column="2" Background="#FF111113" CornerRadius="10" Padding="20,16" BorderBrush="#FF1a1a1e" BorderThickness="1">
                                <Grid><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                                    <TextBlock Grid.Row="0" Text="ACTIVITY LOG" Foreground="#FF52525b" FontSize="10" FontWeight="Bold" Margin="0,0,0,10"/>
                                    <Border Grid.Row="1" Background="#FF0d0d10" CornerRadius="8" Padding="12,10">
                                        <ScrollViewer Name="LogScroller" VerticalScrollBarVisibility="Auto">
                                            <TextBlock Name="LogOutput" Foreground="#FFa1a1aa" FontSize="11" FontFamily="Cascadia Code,Consolas,Courier New" TextWrapping="Wrap"/>
                                        </ScrollViewer>
                                    </Border>
                                    <StackPanel Grid.Row="2" Margin="0,12,0,0">
                                        <ProgressBar Name="MainProgress" Height="6" Value="0" Foreground="#FF3b82f6" Template="{StaticResource RoundProgress}" Visibility="Collapsed"/>
                                        <TextBlock Name="ProgressLabel" Foreground="#FF52525b" FontSize="10" Margin="0,6,0,0" Visibility="Collapsed"/>
                                    </StackPanel>
                                </Grid>
                            </Border>
                        </Grid>
                    </Border>

                    <!-- FOOTER -->
                    <Border Grid.Row="1" VerticalAlignment="Bottom" Margin="24,0,24,8">
                        <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
                            <TextBlock Foreground="#FF3f3f46" FontSize="10"><Hyperlink Name="LinkGitHub" Foreground="#FF52525b" TextDecorations="None" FontSize="10" Cursor="Hand">GitHub</Hyperlink></TextBlock>
                            <TextBlock Text="  |  " Foreground="#FF27272a" FontSize="10"/>
                            <TextBlock Foreground="#FF3f3f46" FontSize="10"><Hyperlink Name="LinkDocs" Foreground="#FF52525b" TextDecorations="None" FontSize="10" Cursor="Hand">Docs</Hyperlink></TextBlock>
                        </StackPanel>
                    </Border>
                </Grid>
            </Grid>
        </Border>
    </Grid>
</Window>
"@

try { $reader = New-Object System.Xml.XmlNodeReader ([xml]$xaml); $script:window = [Windows.Markup.XamlReader]::Load($reader) }
catch { Write-Error "XAML Failed: $($_.Exception.Message)"; Exit 1 }

# Wire up named elements
$script:ui = @{}
@('VersionLabel','MinimizeBtn','CloseBtn','StatusDot','StatusLabel','DriverLabel',
  'BtnApply','BtnRemove','BtnBackup','BtnBenchmark','BtnDiagnostics',
  'DotBuild','DotNVMe','DotBitLocker','DotVeraCrypt','DotDriver','Dot3rdParty','DotCompat','DotSysProt','DotBypassIO',
  'ValBuild','ValNVMe','ValBitLocker','ValVeraCrypt','ValDriver','Val3rdParty','ValCompat','ValSysProt','ValBypassIO',
  'ChkServerKey','ChkSkipWarnings',
  'Dot735','Dot1853','Dot156','Dot1176','DotSafeMin','DotSafeNet',
  'DriveList','LogScroller','LogOutput','MainProgress','ProgressLabel',
  'LinkGitHub','LinkDocs','FlagList'
) | ForEach-Object { $el = $script:window.FindName($_); if ($el) { $script:ui[$_] = $el } }

$script:ui['VersionLabel'].Text = "v$($script:Config.AppVersion)"

# Title bar drag + buttons
$script:window.Add_MouseLeftButtonDown({ $script:window.DragMove() })
$script:ui['CloseBtn'].Add_Click({ $script:window.Close() })
$script:ui['MinimizeBtn'].Add_Click({ $script:window.WindowState = 'Minimized' })

# Links
$script:ui['LinkGitHub'].NavigateUri = [Uri]$script:Config.GitHubURL
$script:ui['LinkDocs'].NavigateUri = [Uri]$script:Config.DocumentationURL
$script:ui['LinkGitHub'].Add_RequestNavigate({ param($s,$e); Start-Process $e.Uri.AbsoluteUri; $e.Handled = $true })
$script:ui['LinkDocs'].Add_RequestNavigate({ param($s,$e); Start-Process $e.Uri.AbsoluteUri; $e.Handled = $true })

# Checkbox event handlers
$script:ui['ChkServerKey'].IsChecked = $script:Config.IncludeServerKey
$script:ui['ChkSkipWarnings'].IsChecked = $script:Config.SkipWarnings

$script:ui['ChkServerKey'].Add_Checked({
    $script:Config.IncludeServerKey = $true
    Write-Log "Optional Server 2025 key (1176759950): enabled" -Level "INFO"
    Update-StatusDisplay
})
$script:ui['ChkServerKey'].Add_Unchecked({
    $script:Config.IncludeServerKey = $false
    Write-Log "Optional Server 2025 key (1176759950): disabled" -Level "INFO"
    Update-StatusDisplay
})
$script:ui['ChkSkipWarnings'].Add_Checked({
    $script:Config.SkipWarnings = $true
    Write-Log "Confirmation warnings: skipped" -Level "INFO"
})
$script:ui['ChkSkipWarnings'].Add_Unchecked({
    $script:Config.SkipWarnings = $false
    Write-Log "Confirmation warnings: enabled" -Level "INFO"
})

# Button click handlers
$script:ui['BtnApply'].Add_Click({
    if (Show-ConfirmDialog -Title "Apply Patch" -Message "Apply the NVMe driver enhancement patch?" -WarningText "This will modify system registry settings." -CheckNVMe $true) {
        Install-NVMePatch
    }
})

$script:ui['BtnRemove'].Add_Click({
    if (Show-ConfirmDialog -Title "Remove Patch" -Message "Remove the NVMe driver patch?" -WarningText "This will revert to default Windows behavior." -CheckNVMe $true) {
        Uninstall-NVMePatch
    }
})

$script:ui['BtnBackup'].Add_Click({
    Set-ButtonsEnabled -Enabled $false
    New-SafeRestorePoint -Description "Manual NVMe Backup $(Get-Date -Format 'yyyy-MM-dd')"
    Set-ButtonsEnabled -Enabled $true
})

$script:ui['BtnBenchmark'].Add_Click({ Start-GUIBenchmark })

$script:ui['BtnDiagnostics'].Add_Click({
    $diagFile = Export-SystemDiagnostics
    if ($diagFile) {
        Write-Log "Diagnostics exported: $diagFile" -Level "SUCCESS"
        Show-ThemedDialog -Message "Diagnostics exported to:`n$diagFile" -Title "Export Complete" -Icon "Information"
    }
})

# ===========================================================================
# SECTION 19: ASYNC PREFLIGHT & WINDOW EVENTS
# ===========================================================================

$script:window.Add_ContentRendered({
    Write-Log "$($script:Config.AppName) v$($script:Config.AppVersion) started"
    Write-Log "Working directory: $($script:Config.WorkingDir)"
    Write-Log "----------------------------------------"
    Write-Log "Running pre-flight checks..."
    Set-ButtonsEnabled -Enabled $false

    # Build runspace with all needed function definitions
    $funcNames = @('Get-WindowsBuildDetails', 'Get-NVMeHealthData', 'Get-SystemDrives',
                   'Test-BitLockerEnabled', 'Test-VeraCryptSystemEncryption', 'Get-IncompatibleSoftware',
                   'Get-NVMeDriverInfo', 'Test-NativeNVMeActive',
                   'Get-BypassIOStatus', 'Test-PatchStatus', 'Invoke-PreflightChecks')
    $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    # Write-Log in the background runspace: collects messages for replay on UI thread
    $bgLogFunc = [System.Management.Automation.Runspaces.SessionStateFunctionEntry]::new(
        'Write-Log', 'param([string]$Message, [string]$Level = "INFO"); if (-not $script:BgLogMessages) { $script:BgLogMessages = [System.Collections.ArrayList]::new() }; [void]$script:BgLogMessages.Add(@{ Message = $Message; Level = $Level })')
    $iss.Commands.Add($bgLogFunc)
    foreach ($fn in $funcNames) {
        $cmd = Get-Command $fn -ErrorAction SilentlyContinue
        if ($cmd) {
            $entry = [System.Management.Automation.Runspaces.SessionStateFunctionEntry]::new($fn, $cmd.Definition)
            $iss.Commands.Add($entry)
        }
    }

    $bgRunspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($iss)
    $bgRunspace.Open()
    $bgPS = [PowerShell]::Create()
    $bgPS.Runspace = $bgRunspace

    $configCopy = $script:Config.Clone()
    $configCopy.SilentMode = $true

    [void]$bgPS.AddScript({
        param($cfg)
        $script:Config = $cfg
        $checks = Invoke-PreflightChecks
        return @{
            Checks              = $checks
            BuildDetails        = $script:BuildDetails
            CachedDrives        = $script:CachedDrives
            HasNVMeDrives       = $script:HasNVMeDrives
            BitLockerEnabled    = $script:BitLockerEnabled
            VeraCryptDetected   = $script:VeraCryptDetected
            IncompatibleSoftware = $script:IncompatibleSoftware
            DriverInfo          = $script:DriverInfo
            NativeNVMeStatus    = $script:NativeNVMeStatus
            BypassIOStatus      = $script:BypassIOStatus
            BgLogMessages       = $script:BgLogMessages
        }
    }).AddParameter('cfg', $configCopy)

    $script:bgHandle = $bgPS.BeginInvoke()
    $script:bgPS = $bgPS
    $script:bgRunspace = $bgRunspace

    # Poll for completion with a DispatcherTimer
    $script:preflightTimer = New-Object System.Windows.Threading.DispatcherTimer
    $script:preflightTimer.Interval = [TimeSpan]::FromMilliseconds(100)
    $script:preflightTimer.Add_Tick({
        if (-not $script:bgHandle.IsCompleted) { return }

        $script:preflightTimer.Stop()

        try {
            $resultList = $script:bgPS.EndInvoke($script:bgHandle)
            $r = $resultList[0]

            # Marshal results back to script scope
            $script:BuildDetails        = $r.BuildDetails
            $script:CachedDrives        = $r.CachedDrives
            $script:HasNVMeDrives       = $r.HasNVMeDrives
            $script:BitLockerEnabled    = $r.BitLockerEnabled
            $script:VeraCryptDetected   = $r.VeraCryptDetected
            $script:IncompatibleSoftware = $r.IncompatibleSoftware
            $script:DriverInfo          = $r.DriverInfo
            $script:NativeNVMeStatus    = $r.NativeNVMeStatus
            $script:BypassIOStatus      = $r.BypassIOStatus
            $checks                     = $r.Checks

            # Replay background log messages on the UI thread
            if ($r.BgLogMessages) {
                foreach ($msg in $r.BgLogMessages) {
                    Write-Log $msg.Message -Level $msg.Level
                }
            }

            $bc = [System.Windows.Media.BrushConverter]::new()

            # Map checklist check names to UI element names
            $checkToDot = @{
                'WindowsVersion'   = 'DotBuild'
                'NVMeDrives'       = 'DotNVMe'
                'BitLocker'        = 'DotBitLocker'
                'VeraCrypt'        = 'DotVeraCrypt'
                'DriverStatus'     = 'DotDriver'
                'ThirdPartyDriver' = 'Dot3rdParty'
                'Compatibility'    = 'DotCompat'
                'SystemProtection' = 'DotSysProt'
                'BypassIO'         = 'DotBypassIO'
            }
            $checkToVal = @{
                'WindowsVersion'   = 'ValBuild'
                'NVMeDrives'       = 'ValNVMe'
                'BitLocker'        = 'ValBitLocker'
                'VeraCrypt'        = 'ValVeraCrypt'
                'DriverStatus'     = 'ValDriver'
                'ThirdPartyDriver' = 'Val3rdParty'
                'Compatibility'    = 'ValCompat'
                'SystemProtection' = 'ValSysProt'
                'BypassIO'         = 'ValBypassIO'
            }

            foreach ($checkName in $checks.Keys) {
                $check = $checks[$checkName]
                $statusColor = switch ($check.Status) {
                    "Pass"    { "#FF22c55e" }
                    "Warning" { "#FFf59e0b" }
                    "Fail"    { "#FFef4444" }
                    "Info"    { "#FF3b82f6" }
                    default   { "#FF71717a" }
                }

                # Update dot
                if ($checkToDot.ContainsKey($checkName) -and $script:ui[$checkToDot[$checkName]]) {
                    $script:ui[$checkToDot[$checkName]].Fill = $bc.ConvertFromString($statusColor)
                }

                # Update value label
                if ($checkToVal.ContainsKey($checkName) -and $script:ui[$checkToVal[$checkName]]) {
                    $script:ui[$checkToVal[$checkName]].Text = $check.Message
                    $script:ui[$checkToVal[$checkName]].Foreground = $bc.ConvertFromString($statusColor)
                }
            }

            # Log all check results
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

            # Load health data lazily (after preflight, on UI thread -- not blocking startup)
            $script:CachedHealth = Get-NVMeHealthData
            Update-DrivesList

            # Log firmware versions
            if ($script:DriverInfo -and $script:DriverInfo.FirmwareVersions.Count -gt 0) {
                foreach ($diskId in $script:DriverInfo.FirmwareVersions.Keys) {
                    Write-Log "  [Firmware] Disk ${diskId}: $($script:DriverInfo.FirmwareVersions[$diskId])" -Level "INFO"
                }
            }

            # Log incompatible software
            if ($script:IncompatibleSoftware -and $script:IncompatibleSoftware.Count -gt 0) {
                foreach ($sw in $script:IncompatibleSoftware) {
                    $swLevel = if ($sw.Severity -eq "Critical") { "ERROR" } else { "WARNING" }
                    Write-Log "  [Compat] $($sw.Name) ($($sw.Severity)): $($sw.Message)" -Level $swLevel
                }
            }

            if ($script:BypassIOStatus -and $script:BypassIOStatus.Warning) {
                Write-Log "  [BypassIO] $($script:BypassIOStatus.Warning)" -Level "WARNING"
            }

            if ($script:BuildDetails) {
                Write-Log "  [Build] $($script:BuildDetails.DisplayVersion) (Build $($script:BuildDetails.BuildNumber).$($script:BuildDetails.UBR))" -Level "INFO"
            }

            Write-Log "----------------------------------------"
            Update-StatusDisplay
            Write-Log "----------------------------------------"

            # Non-blocking update check
            try {
                $script:UpdateAvailable = Test-UpdateAvailable
                if ($script:UpdateAvailable) {
                    Write-Log "UPDATE AVAILABLE: v$($script:UpdateAvailable.Version) -- $($script:Config.GitHubURL)/releases" -Level "WARNING"
                }
            }
            catch { <# Update check is best-effort #> }

            # Post-reboot verification detection
            try {
                $rebootCheck = Test-PatchAppliedSinceLastRun
                if ($rebootCheck.Changed) {
                    Write-Log "" -Level "INFO"
                    Write-Log "========== POST-REBOOT VERIFICATION ==========" -Level "SUCCESS"
                    Write-Log "  $($rebootCheck.Message)" -Level "SUCCESS"
                    if ($rebootCheck.Driver) { Write-Log "  Active driver: $($rebootCheck.Driver)" -Level "SUCCESS" }
                    Write-Log "===============================================" -Level "SUCCESS"
                    Write-Log ""
                    Show-ToastNotification -Title "NVMe Driver Active" -Message $rebootCheck.Message -Type "Success"
                }
            }
            catch { <# Post-reboot check is best-effort #> }

            Write-Log "Ready. Select an action above."
        }
        catch {
            Write-Log "Pre-flight check error: $($_.Exception.Message)" -Level "ERROR"
        }
        finally {
            $script:bgPS.Dispose()
            $script:bgRunspace.Dispose()
            Set-ButtonsEnabled -Enabled $true
        }
    }.GetNewClosure())
    $script:preflightTimer.Start()

    Write-AppEventLog -Message "$($script:Config.AppName) v$($script:Config.AppVersion) started" -EntryType "Information" -EventId 1000
})

$script:window.Add_Closing({
    Save-Configuration

    if ($script:Config.AutoSaveLog -and $script:Config.LogHistory.Count -gt 5) {
        Save-LogFile -Suffix "_autosave" | Out-Null
    }

    Write-AppEventLog -Message "$($script:Config.AppName) closed" -EntryType "Information" -EventId 1000

    if ($script:preflightTimer) { try { $script:preflightTimer.Stop() } catch { <# Timer cleanup #> } }
    if ($script:bgPS) { try { $script:bgPS.Dispose() } catch { <# Runspace cleanup #> } }
    if ($script:bgRunspace) { try { $script:bgRunspace.Dispose() } catch { <# Runspace cleanup #> } }
    if ($script:AppMutex) {
        try { $script:AppMutex.ReleaseMutex(); $script:AppMutex.Dispose() } catch { <# Mutex cleanup best-effort #> }
    }
})

# Run
[void]$script:window.ShowDialog()
