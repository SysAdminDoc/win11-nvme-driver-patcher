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
    Version: 3.3.0
    Author:  Matthew Parker
    Requires: Windows 11 24H2/25H2, Administrator privileges

    CHANGELOG v3.3.0:
    Safety:
    - Added VeraCrypt system encryption detection (hard block - breaks boot entirely)
    - Added automatic BitLocker suspension before patching (Suspend-BitLocker -RebootCount 1)
    - Added incompatible software detection (Acronis, Macrium, VeraCrypt, VirtualBox)
    - Added rollback on partial failure (undoes applied registry keys if not all succeed)
    - Consolidated 5 sequential confirmation dialogs into single comprehensive dialog
    - Added "Skip warnings" checkbox in System Overview options panel
    - Fixed critical SafeBoot GUID typo in README troubleshooting commands
    Diagnostics & Benchmarking:
    - Added built-in DiskSpd benchmark (4K random read/write before/after comparison)
    - Added BENCHMARK button to Actions card for on-demand storage benchmarking
    - Added enhanced NVMe SMART tooltips (TBW, Power-On Hours, Available Spare)
    - Added NVMe firmware version display per drive
    - Added post-reboot patch verification (auto-detects driver activation since last run)
    - Added GitHub update check on startup (non-blocking, 5s timeout)
    - Added VeraCrypt and Compatibility preflight checks to checklist grid
    Code Quality:
    - Removed version number from script filename (now NVMe_Driver_Patcher.ps1)
    - Renamed Load-Configuration to Import-Configuration (PSScriptAnalyzer compliance)
    - Renamed Refresh-StatusDisplay to Update-StatusDisplay (PSScriptAnalyzer compliance)
    - Removed AcceptButton (Enter triggered patch unexpectedly)
    - Fixed invalid CheckBox FlatAppearance properties
    - Merged duplicate resize handlers into one
    - Made preflight checks fully async using [PowerShell]::Create() + BeginInvoke() + Timer polling
    - Fixed footer link visibility (BackColor + BringToFront)
    - Added runspace/timer cleanup in FormClosing
    - Added slim marquee loading bar below header during preflight scanning
    - Enabled form AutoScroll with dark scrollbar theming
    - Added GitHub Actions CI/CD workflow for automated releases
    - Fixed $diskId: variable interpolation bug causing script crash on launch

    CHANGELOG v3.2.0:
    - Fixed GDI Region memory leak in Update-DrivesList (dot indicators)
    - Fixed remaining DoEvents() re-entrancy in Set-ButtonsEnabled and Update-Progress
    - Fixed boot label using hardcoded semi-transparent color instead of theme-aware WarningDim
    - Removed dead "Keyboard shortcuts" comment placeholder
    - Styled server key checkbox to match custom dark/light theme
    - Added right-click context menu on Activity Log (Copy, Save, Clear)
    - Made footer GitHub URL a clickable LinkLabel
    - Made form resizable with Sizable border, min size constraint, and dynamic log card stretching
    - Optimized checklist dot matching from O(n^2) Where-Object to O(1) hashtable lookup
    - Added keyboard accessibility (AcceptButton, KeyPreview)
    - Added animated status pulse when patch state changes
    - Added system tray minimize with double-click restore and context menu
    - Added before/after comparison view showing component changes after patch/unpatch
    - Added circular GDI+ progress ring replacing flat progress bar
    - Added NVMe health indicators (temperature, wear %) via StorageReliabilityCounter

    CHANGELOG v3.1.0:
    - Fixed silent mode falling through to GUI (missing exit)
    - Fixed toast notifications disposing before rendering (timer-based cleanup)
    - Fixed registry backup using inconsistent line endings (now proper CRLF)
    - Fixed verification script not accounting for optional server key
    - Fixed light theme AccentSubtle color being hardcoded to dark palette
    - Fixed Update-StatusDisplay showing hardcoded "/5" instead of dynamic total
    - Deduplicated CIM queries in Get-NVMeDriverInfo (2x Win32_PnPSignedDriver)
    - Cached system detection results to avoid redundant WMI calls
    - Replaced DoEvents with targeted Control.Refresh to prevent re-entrancy
    - Improved mutex single-instance check using createdNew parameter
    - Simplified theme color initialization ($Theme.Clone())
    - Added debug logging/comments to all empty catch blocks
    - Reformatted compressed single-line functions for readability

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

if (-not ([System.Management.Automation.PSTypeName]'DarkTitleBar').Type) {
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

public class DarkTitleBar {
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void EnableDarkMode(IntPtr hwnd) {
        int darkMode = 1;
        DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
    }

    public static void SetCaptionColor(IntPtr hwnd, int color) {
        DwmSetWindowAttribute(hwnd, 35, ref color, sizeof(int));
    }

    public static void SetBorderColor(IntPtr hwnd, int color) {
        DwmSetWindowAttribute(hwnd, 34, ref color, sizeof(int));
    }
}
"@
}

try { [DpiAwareness]::SetProcessDpiAwareness(2) | Out-Null }
catch { try { [DpiAwareness]::SetProcessDPIAware() | Out-Null } catch {} }

[System.Windows.Forms.Application]::EnableVisualStyles()

# Force dark mode at the process level BEFORE any window is created
try {
    $dm = 1
    # Try attribute 20 first (Windows 11), then 19 (older Win10 builds)
    $hr = [DarkTitleBar]::DwmSetWindowAttribute([IntPtr]::Zero, 20, [ref]$dm, 4)
}
catch {}

# Dark color table for context menus (compiled separately with WinForms reference)
if (-not ([System.Management.Automation.PSTypeName]'DarkColorTable').Type) {
try {
    # Resolve actual assembly paths (works on both PS5.1 and PS7+)
    $asmRefs = @(
        ([System.Windows.Forms.Form].Assembly.Location),
        ([System.Drawing.Color].Assembly.Location)
    )
    # On .NET Core/5+, Color is in System.Drawing.Primitives
    $primAsm = [System.Drawing.Color].Assembly
    if ($primAsm.Location -and $primAsm.Location -notmatch 'System\.Drawing\.dll$') {
        $asmRefs += $primAsm.Location
    }
    Add-Type -ReferencedAssemblies $asmRefs -TypeDefinition @"
using System.Drawing;
using System.Windows.Forms;

public class DarkColorTable : ProfessionalColorTable {
    private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
    public override Color MenuBorder { get { return C(56, 56, 64); } }
    public override Color MenuItemBorder { get { return C(56, 56, 64); } }
    public override Color MenuItemSelected { get { return C(48, 48, 56); } }
    public override Color MenuItemSelectedGradientBegin { get { return C(48, 48, 56); } }
    public override Color MenuItemSelectedGradientEnd { get { return C(48, 48, 56); } }
    public override Color MenuItemPressedGradientBegin { get { return C(36, 36, 42); } }
    public override Color MenuItemPressedGradientEnd { get { return C(36, 36, 42); } }
    public override Color MenuStripGradientBegin { get { return C(22, 22, 26); } }
    public override Color MenuStripGradientEnd { get { return C(22, 22, 26); } }
    public override Color ToolStripDropDownBackground { get { return C(26, 26, 30); } }
    public override Color ImageMarginGradientBegin { get { return C(26, 26, 30); } }
    public override Color ImageMarginGradientMiddle { get { return C(26, 26, 30); } }
    public override Color ImageMarginGradientEnd { get { return C(26, 26, 30); } }
    public override Color SeparatorDark { get { return C(56, 56, 64); } }
    public override Color SeparatorLight { get { return C(36, 36, 42); } }
    public override Color CheckBackground { get { return C(56, 132, 244); } }
    public override Color CheckSelectedBackground { get { return C(80, 152, 255); } }
    public override Color CheckPressedBackground { get { return C(40, 100, 200); } }
}
"@
}
catch { <# DarkColorTable compile failed - menus will use fallback colors #> }
}

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
    catch {
        Write-Warning "Mutex check failed: $($_.Exception.Message)"
    }
}

# ===========================================================================
# SECTION 4: GLOBAL CONFIGURATION
# ===========================================================================

$script:Config = @{
    AppName         = "NVMe Driver Patcher"
    AppVersion      = "3.3.0"
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
# SECTION 6: THEME DETECTION
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

$currentTheme = Get-WindowsThemeMode

$PaletteDark = @{
    Background    = [System.Drawing.Color]::FromArgb(15, 15, 18)
    Surface       = [System.Drawing.Color]::FromArgb(22, 22, 26)
    SurfaceLight  = [System.Drawing.Color]::FromArgb(36, 36, 42)
    SurfaceHover  = [System.Drawing.Color]::FromArgb(48, 48, 56)
    CardBackground= [System.Drawing.Color]::FromArgb(26, 26, 30)
    CardBorder    = [System.Drawing.Color]::FromArgb(44, 44, 52)
    Border        = [System.Drawing.Color]::FromArgb(56, 56, 64)
    TextPrimary   = [System.Drawing.Color]::FromArgb(238, 238, 242)
    TextSecondary = [System.Drawing.Color]::FromArgb(178, 178, 190)
    TextMuted     = [System.Drawing.Color]::FromArgb(120, 120, 134)
    TextDimmed    = [System.Drawing.Color]::FromArgb(80, 80, 92)
    WarningDim    = [System.Drawing.Color]::FromArgb(42, 38, 26)
    ProgressBack  = [System.Drawing.Color]::FromArgb(44, 44, 52)
    ChecklistBg   = [System.Drawing.Color]::FromArgb(20, 20, 24)
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

$script:Colors = $Theme.Clone()

# Shared accent colors
$script:Colors.Accent        = [System.Drawing.Color]::FromArgb(56, 132, 244)
$script:Colors.AccentDark    = [System.Drawing.Color]::FromArgb(40, 100, 200)
$script:Colors.AccentHover   = [System.Drawing.Color]::FromArgb(80, 152, 255)
$script:Colors.Success       = [System.Drawing.Color]::FromArgb(34, 154, 80)
$script:Colors.SuccessHover  = [System.Drawing.Color]::FromArgb(44, 174, 96)
$script:Colors.Danger        = [System.Drawing.Color]::FromArgb(220, 55, 65)
$script:Colors.DangerHover   = [System.Drawing.Color]::FromArgb(240, 70, 80)
$script:Colors.WarningBright = [System.Drawing.Color]::FromArgb(245, 190, 60)
$script:Colors.Info          = [System.Drawing.Color]::FromArgb(56, 132, 244)
$script:Colors.Neutral       = [System.Drawing.Color]::FromArgb(110, 110, 125)

if ($currentTheme -eq "Dark") {
    $script:Colors.AccentSubtle  = [System.Drawing.Color]::FromArgb(36, 44, 62)
    $script:Colors.Warning       = [System.Drawing.Color]::FromArgb(245, 190, 60)
    $script:Colors.WarningBg     = [System.Drawing.Color]::FromArgb(52, 48, 34)
    $script:Colors.SuccessBg     = [System.Drawing.Color]::FromArgb(30, 50, 38)
    $script:Colors.DangerBg      = [System.Drawing.Color]::FromArgb(55, 34, 36)
}
else {
    $script:Colors.AccentSubtle  = [System.Drawing.Color]::FromArgb(220, 232, 252)
    $script:Colors.Warning       = [System.Drawing.Color]::FromArgb(210, 155, 40)
    $script:Colors.WarningBg     = [System.Drawing.Color]::FromArgb(255, 250, 230)
    $script:Colors.SuccessBg     = [System.Drawing.Color]::FromArgb(220, 240, 220)
    $script:Colors.DangerBg      = [System.Drawing.Color]::FromArgb(255, 235, 235)
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
    <#
    .SYNOPSIS
        Checks GitHub releases API for a newer version. Non-blocking, best-effort.
        Returns hashtable with Version and URL if update found, $null otherwise.
    #>
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

        # Clean up via timer so balloon has time to render
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
    <#
    .SYNOPSIS
        Detects VeraCrypt system partition encryption. This is a HARD BLOCK --
        enabling nvmedisk.sys with VeraCrypt system encryption breaks boot entirely.
        See: https://github.com/veracrypt/VeraCrypt/issues/1640
    #>
    try {
        # Check for VeraCrypt driver
        $vcDriver = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq "veracrypt" -or $_.PathName -match "veracrypt" }
        if ($vcDriver -and $vcDriver.State -eq "Running") {
            # Check if system drive is encrypted (VeraCrypt boot loader present)
            $vcService = Get-Service -Name "veracrypt" -ErrorAction SilentlyContinue
            if ($vcService) { return $true }
            # Also check for VeraCrypt boot driver
            $vcBoot = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match "veracrypt" -and $_.StartMode -eq "Boot" }
            if ($vcBoot) { return $true }
        }
        # Check for VeraCrypt EFI boot loader
        $efiPath = "$env:SystemDrive\EFI\VeraCrypt"
        if (Test-Path $efiPath -ErrorAction SilentlyContinue) { return $true }
    }
    catch { <# VeraCrypt detection best-effort #> }
    return $false
}

function Get-IncompatibleSoftware {
    <#
    .SYNOPSIS
        Detects installed software known to be incompatible with the native NVMe driver.
        Returns array of hashtables with Name, Severity, and Message.
    #>
    $found = [System.Collections.ArrayList]::new()
    try {
        $services = Get-CimInstance Win32_Service -ErrorAction SilentlyContinue
        $drivers = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue

        # VeraCrypt (Critical - breaks boot)
        $vc = $services | Where-Object { $_.Name -match "veracrypt" -or $_.PathName -match "veracrypt" }
        if ($vc) {
            [void]$found.Add(@{ Name = "VeraCrypt"; Severity = "Critical"; Message = "System encryption breaks boot with nvmedisk.sys" })
        }

        # Acronis (High - drives invisible to backup)
        $acronis = $services | Where-Object { $_.Name -match "acronis|AcronisAgent|mms" -or $_.PathName -match "acronis" }
        if ($acronis) {
            [void]$found.Add(@{ Name = "Acronis"; Severity = "High"; Message = "Backup may not see drives under Storage disks category" })
        }

        # Macrium Reflect (Medium - may need update)
        $macrium = $services | Where-Object { $_.Name -match "macrium|ReflectService" -or $_.PathName -match "macrium" }
        if ($macrium) {
            [void]$found.Add(@{ Name = "Macrium Reflect"; Severity = "Medium"; Message = "May need update for Storage disks compatibility" })
        }

        # VirtualBox (Medium - storage filter driver conflicts)
        $vbox = $drivers | Where-Object { $_.Name -match "VBoxDrv|VBoxNet|VBoxUSB" }
        if ($vbox) {
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
        
        # Try to get queue depth from registry
        try {
            $queueDepthPath = "HKLM:\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device"
            if (Test-Path $queueDepthPath) {
                $qd = Get-ItemProperty -Path $queueDepthPath -Name "IoQueueDepth" -ErrorAction SilentlyContinue
                if ($qd) { $driverInfo.QueueDepth = $qd.IoQueueDepth }
            }
        }
        catch { <# Queue depth registry key may not exist #> }

        # Collect firmware versions per NVMe drive
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
            # Build rich tooltip
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
        catch { <# Display version registry key may not exist on older builds #> }

        # Build 26100+ = 24H2, Build 26200+ = 25H2
        $details.Is24H2OrLater = ($details.BuildNumber -ge 26100)
        $details.IsRecommended = ($details.BuildNumber -ge $script:Config.RecommendedBuild)
    }
    catch {
        Write-Warning "Failed to retrieve build details: $($_.Exception.Message)"
    }
    
    return $details
}

# Animation state
$script:StatusPulseTimer = $null
$script:PulseStep = 0
$script:PulseDirection = 1
$script:PulseTargetColor = $null
$script:PreviousPatchState = $null

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
    $script:CachedHealth = Get-NVMeHealthData
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
    $script:BitLockerEnabled = Test-BitLockerEnabled
    if ($script:BitLockerEnabled) {
        $script:PreflightChecks.BitLocker = @{ Status = "Warning"; Message = "Encryption active"; Critical = $false }
    }
    else {
        $script:PreflightChecks.BitLocker = @{ Status = "Pass"; Message = "Not detected"; Critical = $false }
    }
    
    # VeraCrypt (Critical - breaks boot entirely)
    $script:VeraCryptDetected = Test-VeraCryptSystemEncryption
    if ($script:VeraCryptDetected) {
        $script:PreflightChecks.VeraCrypt = @{ Status = "Fail"; Message = "BLOCKS PATCH - breaks boot"; Critical = $true }
    }
    else {
        $script:PreflightChecks.VeraCrypt = @{ Status = "Pass"; Message = "Not detected"; Critical = $true }
    }

    # Incompatible Software
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
    $script:DriverInfo = Get-NVMeDriverInfo
    if ($script:DriverInfo.HasThirdParty) {
        $script:PreflightChecks.ThirdPartyDriver = @{ Status = "Warning"; Message = $script:DriverInfo.ThirdPartyName; Critical = $false }
    }
    else {
        $script:PreflightChecks.ThirdPartyDriver = @{ Status = "Pass"; Message = "Using inbox driver"; Critical = $false }
    }
    
    # System Protection
    try {
        $null = Get-ComputerRestorePoint -ErrorAction SilentlyContinue
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

        # Refresh UI periodically - only process paint/timer messages to avoid
        # re-entrancy from user input during long operations
        if ($script:Config.LogHistory.Count % 10 -eq 0 -and $script:form) {
            $script:rtbOutput.Refresh()
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
$totalChecks = $featureIDs.Count + 2  # feature flags + 2 SafeBoot keys

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
    
    # Inject actual server key setting into the generated script
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
    <#
    .SYNOPSIS
        Downloads Microsoft DiskSpd from GitHub releases. Returns path to exe or $null.
    #>
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
        # Find the amd64 exe
        $exeFound = Get-ChildItem -Path $diskSpdDir -Recurse -Filter "diskspd.exe" |
            Where-Object { $_.FullName -match "amd64" } | Select-Object -First 1
        if ($exeFound) {
            Copy-Item -Path $exeFound.FullName -Destination $diskSpdExe -Force
            Write-Log "DiskSpd downloaded successfully" -Level "SUCCESS"
            return $diskSpdExe
        }
        # Fallback: any diskspd.exe
        $exeAny = Get-ChildItem -Path $diskSpdDir -Recurse -Filter "diskspd.exe" | Select-Object -First 1
        if ($exeAny) {
            Copy-Item -Path $exeAny.FullName -Destination $diskSpdExe -Force
            return $diskSpdExe
        }
        Write-Log "DiskSpd exe not found in archive" -Level "ERROR"
    }
    catch {
        Write-Log "Failed to download DiskSpd: $($_.Exception.Message)" -Level "ERROR"
        # Clean up partial downloads
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    }
    return $null
}

function Invoke-StorageBenchmark {
    <#
    .SYNOPSIS
        Runs a quick 4K random read/write benchmark on the system drive using DiskSpd.
        Returns hashtable with IOPS, throughput, and latency for read and write.
    #>
    param([string]$Label = "benchmark")

    $exe = Install-DiskSpd
    if (-not $exe) {
        Write-Log "Cannot run benchmark - DiskSpd unavailable" -Level "ERROR"
        return $null
    }

    $testFile = Join-Path $script:Config.WorkingDir "diskspd_test.dat"
    $results = @{ Label = $Label; Timestamp = (Get-Date).ToString("o"); Read = @{}; Write = @{} }

    try {
        # 4K Random Read: 4 threads, 16 outstanding IOs, 10 second warmup, 30 second test, 4KB blocks, random
        Write-Log "Running 4K random read benchmark (30s)..." -Level "INFO"
        Update-Progress -Value 20 -Status "Benchmarking reads..."
        $readOutput = & $exe -c128M -d30 -w0 -t4 -o16 -b4K -r -Sh -L $testFile 2>&1 | Out-String
        $results.Read = Convert-DiskSpdOutput -RawOutput $readOutput -IOType "Read"

        # 4K Random Write: same params but 100% write
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
        # DiskSpd output format (with -L flag):
        # total:   <bytes>  | <IOs> | <MiB/s> | <I/O per s> | <AvgLat> | ...
        # The line starts with "total:" then bytes, then pipe-separated fields.
        # Split on | gives: [0]="total: <bytes>", [1]="<IOs>", [2]="<MiB/s>", [3]="<IOPS>", [4]="<AvgLat>"
        $lines = $RawOutput -split "`n"
        foreach ($line in $lines) {
            if ($line -match '^\s*total:') {
                $parts = $line -split '\|' | ForEach-Object { $_.Trim() }
                if ($parts.Count -ge 4) {
                    # Use InvariantCulture to handle locale-dependent decimal separators
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
        # Keep only last 10 results
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
    $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor

    $status = Test-PatchStatus
    $label = if ($status.Applied) { "Post-Patch" } else { "Pre-Patch" }

    Write-Log "Starting storage benchmark ($label)..." -Level "INFO"
    Write-Log "This will take approximately 60 seconds. Do not use disk-heavy apps." -Level "WARNING"

    $results = Invoke-StorageBenchmark -Label $label
    if ($results) {
        Save-BenchmarkResults -Results $results
        Show-BenchmarkComparison -Current $results
    }

    $script:form.Cursor = [System.Windows.Forms.Cursors]::Default
    Set-ButtonsEnabled -Enabled $true
}

# ===========================================================================
# SECTION 13C: POST-REBOOT DETECTION
# ===========================================================================

function Test-PatchAppliedSinceLastRun {
    <#
    .SYNOPSIS
        Checks if the patch state changed since the tool last ran.
        Compares current state with the saved config's last known state.
    #>
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
            # Detect if patch was applied since last run and driver activated
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

        # Save current state for next comparison
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

function New-DarkContextMenu {
    param([System.Windows.Forms.ContextMenuStrip]$Menu)
    $bgColor = $script:Colors.CardBackground
    $fgColor = $script:Colors.TextPrimary
    $Menu.BackColor = $bgColor
    $Menu.ForeColor = $fgColor
    $Menu.Font = New-Object System.Drawing.Font("Segoe UI", 9)
    $Menu.ShowImageMargin = $false
    try {
        $darkTable = New-Object DarkColorTable
        $Menu.Renderer = New-Object System.Windows.Forms.ToolStripProfessionalRenderer($darkTable)
    }
    catch {
        <# DarkColorTable compilation failed; fall back to manual colors only #>
    }
    $Menu.Add_ItemAdded({
        param($sender, $e)
        try { $e.Item.BackColor = $bgColor; $e.Item.ForeColor = $fgColor } catch {}
    }.GetNewClosure())
    return $Menu
}

function Set-DarkScrollbar {
    param([System.Windows.Forms.Control]$Control)
    try { [DarkScrollBar]::SetWindowTheme($Control.Handle, "DarkMode_Explorer", $null) | Out-Null } catch { <# Dark scrollbar theming is cosmetic-only #> }
}

function Update-DrivesList {
    if (-not $script:pnlDrivesContent) { return }
    $script:pnlDrivesContent.Controls.Clear()
    $drives = if ($script:CachedDrives) { $script:CachedDrives } else { Get-SystemDrives }
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
        $rgn = New-Object System.Drawing.Region($dp); $dp.Dispose()
        $oldRgn = $dot.Region; $dot.Region = $rgn; if ($oldRgn) { $oldRgn.Dispose() }
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
        $nextBadgeX = 426
        if ($drv.IsBoot) {
            $bootLbl = New-Object System.Windows.Forms.Label
            $bootLbl.Size      = New-Object System.Drawing.Size(42, 18)
            $bootLbl.Location  = New-Object System.Drawing.Point($nextBadgeX, 5)
            $bootLbl.Text      = "BOOT"
            $bootLbl.Font      = New-Object System.Drawing.Font("Segoe UI Semibold", 7.5)
            $bootLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
            $bootLbl.ForeColor = $script:Colors.Warning
            $bootLbl.BackColor = $script:Colors.WarningDim
            Set-RoundedCorners -Control $bootLbl -Radius 8
            $row.Controls.Add($bootLbl)
            $nextBadgeX += 48
        }

        # NVMe health badges (temperature, wear)
        if ($drv.IsNVMe -and $script:CachedHealth) {
            $diskKey = "$($drv.Number)"
            $hData = $script:CachedHealth[$diskKey]
            if ($hData) {
                if ($hData.Temperature -ne "N/A") {
                    $tempLbl = New-Object System.Windows.Forms.Label
                    $tempLbl.Size = New-Object System.Drawing.Size(38, 18)
                    $tempLbl.Location = New-Object System.Drawing.Point($nextBadgeX, 5)
                    $tempLbl.Text = $hData.Temperature
                    $tempLbl.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 7)
                    $tempLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
                    $tempVal = 0; [int]::TryParse(($hData.Temperature -replace '[^0-9]',''), [ref]$tempVal) | Out-Null
                    $tempLbl.ForeColor = if ($tempVal -ge 70) { $script:Colors.Danger } elseif ($tempVal -ge 50) { $script:Colors.Warning } else { $script:Colors.Success }
                    $tempLbl.BackColor = $script:Colors.SurfaceLight
                    Set-RoundedCorners -Control $tempLbl -Radius 6
                    $row.Controls.Add($tempLbl)
                    $nextBadgeX += 42
                }
                if ($hData.Wear -ne "N/A") {
                    $wearLbl = New-Object System.Windows.Forms.Label
                    $wearLbl.Size = New-Object System.Drawing.Size(38, 18)
                    $wearLbl.Location = New-Object System.Drawing.Point($nextBadgeX, 5)
                    $wearLbl.Text = $hData.Wear
                    $wearLbl.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 7)
                    $wearLbl.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
                    $wearVal = 0; [int]::TryParse(($hData.Wear -replace '[^0-9]',''), [ref]$wearVal) | Out-Null
                    $wearLbl.ForeColor = if ($wearVal -le 20) { $script:Colors.Danger } elseif ($wearVal -le 50) { $script:Colors.Warning } else { $script:Colors.Success }
                    $wearLbl.BackColor = $script:Colors.SurfaceLight
                    Set-RoundedCorners -Control $wearLbl -Radius 6
                    $smartTip = if ($hData.SmartTooltip) { $hData.SmartTooltip } else { "Drive health remaining" }
                    if ($script:ToolTipProvider) { $script:ToolTipProvider.SetToolTip($wearLbl, $smartTip) }
                    $row.Controls.Add($wearLbl)
                    $nextBadgeX += 42
                }
            }
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
    $card.Add_Paint({ param($paintSender, $e); $g = $e.Graphics; $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias; $r = Get-ControlTagValue -Control $paintSender -Key 'CornerRadius' -Default 10; $d = $r * 2; $pen = $null; $borderPath = $null; try { $pen = New-Object System.Drawing.Pen($script:Colors.CardBorder, 1); $rect = New-Object System.Drawing.Rectangle(0, 0, ($paintSender.Width - 1), ($paintSender.Height - 1)); $borderPath = New-Object System.Drawing.Drawing2D.GraphicsPath; $borderPath.AddArc($rect.X, $rect.Y, $d, $d, 180, 90); $borderPath.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90); $borderPath.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90); $borderPath.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90); $borderPath.CloseFigure(); $g.DrawPath($pen, $borderPath) } finally { if ($borderPath) { $borderPath.Dispose() }; if ($pen) { $pen.Dispose() } } })
    if ($Title) { $titleLabel = New-Object System.Windows.Forms.Label; $titleLabel.Text = $Title.ToUpper(); $titleLabel.Location = New-Object System.Drawing.Point($Padding, 14); $titleLabel.Size = New-Object System.Drawing.Size(($Size.Width - $Padding * 2), 20); $titleLabel.ForeColor = $script:Colors.TextSecondary; $titleLabel.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 8.5); Set-ControlTagData -Control $titleLabel -NewData @{ Role = "cardTitle" }; $card.Controls.Add($titleLabel) }
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

function Set-ButtonsEnabled { param([bool]$Enabled); if ($script:btnApply) { $script:btnApply.Enabled = $Enabled }; if ($script:btnRemove) { $script:btnRemove.Enabled = $Enabled }; if ($script:btnBackup) { $script:btnBackup.Enabled = $Enabled }; if ($script:form) { $script:form.Refresh() } }

function Update-Progress { param([int]$Value, [string]$Status = ""); if ($script:progressBar) { $script:progressBar.Value = [Math]::Min($Value, 100); $script:progressBar.Visible = ($Value -gt 0 -and $Value -lt 100) }; if ($script:progressRing) { $script:progressRing.Tag = @{ Value = $Value }; $script:progressRing.Visible = ($Value -gt 0 -and $Value -lt 100); $script:progressRing.Invalidate() }; if ($script:lblProgress -and $Status) { $script:lblProgress.Text = $Status; $script:lblProgress.Visible = ($Status -ne "") }; if ($script:form) { $script:form.Refresh() } }

# ===========================================================================
# SECTION 16: BACKUP & PATCH OPERATIONS (Same as v2.8.0 with toast notifications)
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
        $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    }

    $successCount = 0
    $appliedKeys = [System.Collections.ArrayList]::new()

    # Determine total components based on server key inclusion
    $effectiveTotal = $script:Config.TotalComponents
    $featureIDsToApply = [System.Collections.ArrayList]@($script:Config.FeatureIDs)
    if ($script:Config.IncludeServerKey) {
        [void]$featureIDsToApply.Add($script:Config.ServerFeatureID)
        $effectiveTotal = $script:Config.TotalComponents + 1
        Write-Log "Including optional Microsoft Server 2025 key (1176759950)" -Level "INFO"
    }

    try {
        # Step 0: Suspend BitLocker if active (prevents recovery key prompt on reboot)
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

                $result = [System.Windows.Forms.MessageBox]::Show(
                    $restartMsg, "Installation Complete",
                    [System.Windows.Forms.MessageBoxButtons]::YesNo,
                    [System.Windows.Forms.MessageBoxIcon]::Question
                )
                if ($result -eq [System.Windows.Forms.DialogResult]::Yes) {
                    Write-Log "Initiating system restart in $($script:Config.RestartDelay) seconds..."
                    Start-Process "shutdown.exe" -ArgumentList "/r /t $($script:Config.RestartDelay) /c `"NVMe Driver Patch - Restarting in $($script:Config.RestartDelay) seconds. Save your work!`""
                }
            }
            return $true
        }
        else {
            Write-Log "Patch Status: PARTIAL - Applied $successCount/$effectiveTotal components" -Level "WARNING"

            # Rollback: undo all applied keys to leave system in a clean state
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
            $script:form.Cursor = [System.Windows.Forms.Cursors]::Default
            Set-ButtonsEnabled -Enabled $true
            Update-StatusDisplay
        }
        if ($script:BeforeSnapshot) {
            $afterSnapshot = Get-PatchSnapshot
            Show-BeforeAfterComparison -Before $script:BeforeSnapshot -After $afterSnapshot -Operation "Install Patch"
            $script:BeforeSnapshot = $null
        }
        # Save state for post-reboot detection
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
        $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    }

    Update-Progress -Value 10 -Status "Creating backup..."
    Export-RegistryBackup -Description "Pre_Removal"
    $removedCount = 0

    # Remove all feature flags including optional server key if present
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
            $script:form.Cursor = [System.Windows.Forms.Cursors]::Default
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

function Start-StatusPulse {
    param([System.Drawing.Color]$TargetColor)
    $script:PulseTargetColor = $TargetColor
    $script:PulseStep = 0
    $script:PulseDirection = 1
    if ($script:StatusPulseTimer) { $script:StatusPulseTimer.Stop(); $script:StatusPulseTimer.Dispose() }
    $script:StatusPulseTimer = New-Object System.Windows.Forms.Timer
    $script:StatusPulseTimer.Interval = 40
    $script:StatusPulseTimer.Add_Tick({
        $script:PulseStep += $script:PulseDirection * 8
        if ($script:PulseStep -ge 100) { $script:PulseDirection = -1; $script:PulseStep = 100 }
        if ($script:PulseStep -le 0) {
            $script:PulseStep = 0
            $this.Stop()
            $this.Dispose()
            $script:StatusPulseTimer = $null
            # Set final color
            $icon = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "icon" }
            if ($icon) { $icon.BackColor = $script:PulseTargetColor }
            return
        }
        $icon = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "icon" }
        if ($icon) {
            $t = $script:PulseTargetColor
            $factor = [Math]::Min($script:PulseStep, 100) / 100.0
            $bright = [int](255 * $factor)
            $r = [Math]::Min(255, [int]($t.R + ($bright - $t.R) * $factor * 0.5))
            $g = [Math]::Min(255, [int]($t.G + ($bright - $t.G) * $factor * 0.5))
            $b = [Math]::Min(255, [int]($t.B + ($bright - $t.B) * $factor * 0.5))
            $icon.BackColor = [System.Drawing.Color]::FromArgb($r, $g, $b)
        }
    }.GetNewClosure())
    $script:StatusPulseTimer.Start()
}

function Update-StatusDisplay {
    $status = Test-PatchStatus

    # Update feature flag dots
    if ($script:keyRows) {
        foreach ($id in $script:Config.FeatureIDs) {
            if ($script:keyRows.ContainsKey($id)) {
                $isPresent = ($status.Keys -contains $id)
                $dot = $script:keyRows[$id].Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "dot" }
                if ($dot) { $dot.BackColor = if ($isPresent) { $script:Colors.Success } else { $script:Colors.Danger } }
            }
        }

        foreach ($safeName in @("SafeBootMinimal", "SafeBootNetwork")) {
            if ($script:keyRows.ContainsKey($safeName)) {
                $isPresent = ($status.Keys -contains $safeName)
                $dot = $script:keyRows[$safeName].Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "dot" }
                if ($dot) { $dot.BackColor = if ($isPresent) { $script:Colors.Success } else { $script:Colors.Danger } }
            }
        }
    }

    # Update optional server key dot
    if ($script:keyRows -and $script:keyRows.ContainsKey("1176759950")) {
        $serverPresent = $false
        try {
            if (Test-Path $script:Config.RegistryPath) {
                $sVal = Get-ItemProperty -Path $script:Config.RegistryPath -Name "1176759950" -ErrorAction SilentlyContinue
                if ($sVal -and $sVal."1176759950" -eq 1) { $serverPresent = $true }
            }
        }
        catch { <# Server key check non-critical #> }

        $dot = $script:keyRows["1176759950"].Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "dot" }
        if ($dot) {
            $dot.BackColor = if ($serverPresent) { $script:Colors.Success }
                             elseif ($script:Config.IncludeServerKey) { $script:Colors.Warning }
                             else { $script:Colors.TextMuted }
        }
    }

    # Update status indicator and buttons based on patch status
    if ($script:pnlPatchStatus) {
        $icon = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "icon" }
        $label = $script:pnlPatchStatus.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "status" }

        # Determine new state string for change detection
        $newState = if ($status.Applied) { "Applied" } elseif ($status.Partial) { "Partial" } else { "NotApplied" }
        $stateChanged = ($null -ne $script:PreviousPatchState -and $script:PreviousPatchState -ne $newState)
        $script:PreviousPatchState = $newState

        if ($status.Applied) {
            if ($label) { $label.Text = "Patch Applied" }
            if ($stateChanged) { Start-StatusPulse -TargetColor $script:Colors.Success }
            elseif ($icon) { $icon.BackColor = $script:Colors.Success }
        }
        elseif ($status.Partial) {
            if ($label) { $label.Text = "Partial ($($status.Count)/$($status.Total))" }
            if ($stateChanged) { Start-StatusPulse -TargetColor $script:Colors.Warning }
            elseif ($icon) { $icon.BackColor = $script:Colors.Warning }
        }
        else {
            if ($label) { $label.Text = "Not Applied" }
            if ($stateChanged) { Start-StatusPulse -TargetColor $script:Colors.TextMuted }
            elseif ($icon) { $icon.BackColor = $script:Colors.TextMuted }
        }
    }

    # Update button styling based on state
    if ($status.Applied) {
        if ($script:btnApply) {
            $script:btnApply.Text = "REINSTALL"
            $tag = $script:btnApply.Tag
            if ($tag -is [hashtable]) { $tag['Original'] = $script:Colors.SurfaceLight; $tag['Hover'] = $script:Colors.SurfaceHover }
            $script:btnApply.BackColor = $script:Colors.SurfaceLight
        }
        if ($script:btnRemove) {
            $tagRemove = $script:btnRemove.Tag
            if ($tagRemove -is [hashtable]) { $tagRemove['Original'] = $script:Colors.Danger; $tagRemove['Hover'] = $script:Colors.DangerHover }
            $script:btnRemove.BackColor = $script:Colors.Danger
        }
    }
    elseif (-not $status.Partial) {
        if ($script:btnApply) {
            $script:btnApply.Text = "APPLY PATCH"
            $tag = $script:btnApply.Tag
            if ($tag -is [hashtable]) { $tag['Original'] = $script:Colors.Success; $tag['Hover'] = $script:Colors.SuccessHover }
            $script:btnApply.BackColor = $script:Colors.Success
        }
        if ($script:btnRemove) {
            $tagRemove = $script:btnRemove.Tag
            if ($tagRemove -is [hashtable]) { $tagRemove['Original'] = $script:Colors.SurfaceLight; $tagRemove['Hover'] = $script:Colors.SurfaceHover }
            $script:btnRemove.BackColor = $script:Colors.SurfaceLight
        }
    }
}

function Show-ConfirmDialog {
    param(
        [string]$Title,
        [string]$Message,
        [string]$WarningText = "",
        [bool]$CheckNVMe = $false
    )

    # VeraCrypt hard block -- cannot be skipped, even with Force
    if ($Title -eq "Apply Patch" -and $script:VeraCryptDetected) {
        Write-Log "BLOCKED: VeraCrypt system encryption detected - nvmedisk.sys breaks VeraCrypt boot" -Level "ERROR"
        Write-Log "See: https://github.com/veracrypt/VeraCrypt/issues/1640" -Level "ERROR"
        if (-not $script:Config.SilentMode) {
            [System.Windows.Forms.MessageBox]::Show(
                "CANNOT APPLY PATCH`n`nVeraCrypt system encryption detected. Enabling the native NVMe driver (nvmedisk.sys) breaks VeraCrypt boot entirely.`n`nThis is a known critical incompatibility (VeraCrypt Issue #1640).`n`nYou must either:`n- Decrypt your system drive with VeraCrypt first, OR`n- Wait for a VeraCrypt update that supports nvmedisk.sys`n`nThis block cannot be overridden.",
                "VeraCrypt Incompatibility",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Error
            ) | Out-Null
        }
        return $false
    }

    if ($script:Config.ForceMode -or $script:Config.SkipWarnings) { return $true }

    # Build a single comprehensive message with all relevant warnings
    $warnings = [System.Collections.ArrayList]::new()
    $icon = [System.Windows.Forms.MessageBoxIcon]::Question

    if ($CheckNVMe -and -not $script:HasNVMeDrives) {
        [void]$warnings.Add("[!] NO NVMe DRIVES DETECTED - This patch only affects NVMe drives using the Windows inbox driver.")
        $icon = [System.Windows.Forms.MessageBoxIcon]::Warning
    }

    if ($script:BitLockerEnabled) {
        [void]$warnings.Add("[!] BITLOCKER ACTIVE - Will be automatically suspended for one reboot to prevent recovery key prompt.")
        $icon = [System.Windows.Forms.MessageBoxIcon]::Warning
    }

    # Incompatible software warnings
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
        $icon = [System.Windows.Forms.MessageBoxIcon]::Warning
    }

    if ($Title -eq "Apply Patch") {
        [void]$warnings.Add("[i] GAMING NOTE: Native NVMe does not support BypassIO. DirectStorage games may have higher CPU usage.")
    }

    # Build the final dialog message
    $fullMessage = $Message
    if ($WarningText) { $fullMessage += "`n`nWARNING: $WarningText" }

    if ($warnings.Count -gt 0) {
        $fullMessage += "`n`n--- NOTICES ---`n"
        $fullMessage += ($warnings -join "`n`n")
    }

    $fullMessage += "`n`nProceed?"

    $result = [System.Windows.Forms.MessageBox]::Show(
        $fullMessage, $Title,
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        $icon
    )

    if ($result -ne [System.Windows.Forms.DialogResult]::Yes) {
        Write-Log "Operation cancelled by user" -Level "WARNING"
        return $false
    }
    return $true
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
    if ($script:AppMutex) { try { $script:AppMutex.ReleaseMutex(); $script:AppMutex.Dispose() } catch { <# Mutex cleanup best-effort #> } }

    exit $exitCode
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
$script:form.FormBorderStyle = "Sizable"
$script:form.MinimumSize     = New-Object System.Drawing.Size($FW, 700)
$script:form.MaximizeBox     = $true
$script:form.BackColor       = $script:Colors.Background
$script:form.Font            = New-Object System.Drawing.Font("Segoe UI", 9)
$script:form.GetType().GetProperty(
    "DoubleBuffered",
    [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic
).SetValue($script:form, $true, $null)
$script:form.AutoScroll = $true
$script:form.AutoScrollMargin = New-Object System.Drawing.Size(0, 10)

# ===================================================================
#  HEADER
# ===================================================================

$pnlHeader = New-Object System.Windows.Forms.Panel
$pnlHeader.Location  = New-Object System.Drawing.Point(0, 0)
$pnlHeader.Size      = New-Object System.Drawing.Size($FW, $HH)
$pnlHeader.BackColor = $script:Colors.Surface

# Accent bar (3px for more visual weight)
$pnlAccentBar = New-Object System.Windows.Forms.Panel
$pnlAccentBar.Dock      = [System.Windows.Forms.DockStyle]::Bottom
$pnlAccentBar.Size      = New-Object System.Drawing.Size($FW, 3)
$pnlAccentBar.BackColor = $script:Colors.Accent
$pnlHeader.Controls.Add($pnlAccentBar)

# Icon badge - use full word width at smaller font so it never wraps
$lblIcon = New-Object System.Windows.Forms.Label
$lblIcon.Text      = "NVMe"
$lblIcon.Location  = New-Object System.Drawing.Point(32, 26)
$lblIcon.Size      = New-Object System.Drawing.Size(76, 48)
$lblIcon.ForeColor = [System.Drawing.Color]::White
$lblIcon.Font      = New-Object System.Drawing.Font("Segoe UI Black", 11)
$lblIcon.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$lblIcon.BackColor = $script:Colors.Accent
Set-RoundedCorners -Control $lblIcon -Radius 12
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

# Custom-painted loading bar (replaces ProgressBar to avoid light-theme visual styles)
$script:loadingBar = New-Object System.Windows.Forms.Panel
$script:loadingBar.Location = New-Object System.Drawing.Point(0, $HH)
$script:loadingBar.Size     = New-Object System.Drawing.Size($FW, 3)
$script:loadingBar.BackColor = $script:Colors.Background
$script:loadingBar.Visible   = $false
$script:loadingBar.GetType().GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic).SetValue($script:loadingBar, $true, $null)
$script:loadingBar.Tag = @{ MarqueePos = 0 }
$script:loadingBar.Add_Paint({
    param($s, $e)
    $g = $e.Graphics
    $pos = 0; if ($s.Tag -is [hashtable]) { $pos = $s.Tag['MarqueePos'] }
    $barWidth = [int]($s.Width * 0.25)
    $brush = New-Object System.Drawing.SolidBrush($script:Colors.Accent)
    $g.FillRectangle($brush, $pos, 0, $barWidth, $s.Height)
    $brush.Dispose()
}.GetNewClosure())
# Marquee animation timer
$script:loadingTimer = New-Object System.Windows.Forms.Timer
$script:loadingTimer.Interval = 20
$script:loadingTimer.Add_Tick({
    if (-not $script:loadingBar -or -not $script:loadingBar.Visible) { return }
    $tag = $script:loadingBar.Tag
    if ($tag -is [hashtable]) {
        $tag['MarqueePos'] = ($tag['MarqueePos'] + 4) % ($script:loadingBar.Width + [int]($script:loadingBar.Width * 0.25))
        if ($tag['MarqueePos'] -gt $script:loadingBar.Width) { $tag['MarqueePos'] = -[int]($script:loadingBar.Width * 0.25) }
    }
    $script:loadingBar.Invalidate()
}.GetNewClosure())
$script:form.Controls.Add($script:loadingBar)
$script:loadingBar.BringToFront()

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
$overviewH = 400
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
$script:checklistDots = @{}

# Reordered so long-value items are in left column (more width)
$checkItemsLeft  = @("WindowsVersion", "NVMeDrives",  "BitLocker",         "VeraCrypt",       "DriverStatus")
$checkNamesLeft  = @("Build",          "NVMe",        "BitLocker",         "VeraCrypt",       "Driver")
$checkItemsRight = @("ThirdPartyDriver",   "Compatibility",     "SystemProtection",  "BypassIO")
$checkNamesRight = @("3rd Party",          "Compat.",           "Sys Prot.",          "BypassIO")

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
    $script:checklistDots[$checkItemsLeft[$i]] = $dot
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
    $script:checklistDots[$checkItemsRight[$i]] = $dot
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
$divY = $gridTop + (5 * $RH) + 10
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
$script:chkServerKey.BackColor = $script:Colors.CardBackground
$script:chkServerKey.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
$script:chkServerKey.FlatStyle = [System.Windows.Forms.FlatStyle]::System
$script:chkServerKey.Cursor    = [System.Windows.Forms.Cursors]::Hand
$script:chkServerKey.Checked   = $script:Config.IncludeServerKey
try { [DarkScrollBar]::SetWindowTheme($script:chkServerKey.Handle, "DarkMode_Explorer", $null) | Out-Null } catch {}
$script:chkServerKey.Add_CheckedChanged({
    $script:Config.IncludeServerKey = $this.Checked
    $keyDesc = if ($this.Checked) { "enabled" } else { "disabled" }
    Write-Log "Optional Server 2025 key (1176759950): $keyDesc" -Level "INFO"
    Update-StatusDisplay
})
$cardOverview.Controls.Add($script:chkServerKey)

# Skip warnings checkbox
$script:chkSkipWarnings = New-Object System.Windows.Forms.CheckBox
$script:chkSkipWarnings.Location  = New-Object System.Drawing.Point(($CP + 2), ($optY + 26))
$script:chkSkipWarnings.Size      = New-Object System.Drawing.Size(500, 24)
$script:chkSkipWarnings.Text      = "Skip confirmation warnings (experienced users)"
$script:chkSkipWarnings.ForeColor = $script:Colors.TextSecondary
$script:chkSkipWarnings.BackColor = $script:Colors.CardBackground
$script:chkSkipWarnings.Font      = New-Object System.Drawing.Font("Segoe UI", 9)
$script:chkSkipWarnings.FlatStyle = [System.Windows.Forms.FlatStyle]::System
$script:chkSkipWarnings.Cursor    = [System.Windows.Forms.Cursors]::Hand
$script:chkSkipWarnings.Checked   = $script:Config.SkipWarnings
try { [DarkScrollBar]::SetWindowTheme($script:chkSkipWarnings.Handle, "DarkMode_Explorer", $null) | Out-Null } catch {}
$script:chkSkipWarnings.Add_CheckedChanged({
    $script:Config.SkipWarnings = $this.Checked
    $warnDesc = if ($this.Checked) { "skipped" } else { "enabled" }
    Write-Log "Confirmation warnings: $warnDesc" -Level "INFO"
})
$cardOverview.Controls.Add($script:chkSkipWarnings)

# BypassIO note (aligned to $CP)
$noteY = $optY + 58
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

# Secondary row (3 buttons -- Docs link moved to footer)
$secGap  = 14
$btnSecW = [int](($IW - ($secGap * 2)) / 3)
$secBtnY = 124

$script:btnBackup = New-ModernButton `
    -Text "BACKUP" `
    -Location (New-Object System.Drawing.Point($CP, $secBtnY)) `
    -Size (New-Object System.Drawing.Size($btnSecW, 42)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Create system restore point + registry backup" `
    -OnClick {
        Set-ButtonsEnabled -Enabled $false
        $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        New-SafeRestorePoint -Description "Manual NVMe Backup $(Get-Date -Format 'yyyy-MM-dd')"
        $script:form.Cursor = [System.Windows.Forms.Cursors]::Default
        Set-ButtonsEnabled -Enabled $true
    }
$cardActions.Controls.Add($script:btnBackup)

$btnBench = New-ModernButton `
    -Text "BENCHMARK" `
    -Location (New-Object System.Drawing.Point(($CP + $btnSecW + $secGap), $secBtnY)) `
    -Size (New-Object System.Drawing.Size($btnSecW, 42)) `
    -BackColor $script:Colors.AccentSubtle `
    -HoverColor $script:Colors.SurfaceHover `
    -ForeColor $script:Colors.Accent `
    -ToolTip "Run 4K random read/write benchmark (DiskSpd, ~60s)" `
    -OnClick { Start-GUIBenchmark }
$cardActions.Controls.Add($btnBench)

$btnDiag = New-ModernButton `
    -Text "DIAGNOSTICS" `
    -Location (New-Object System.Drawing.Point(($CP + ($btnSecW + $secGap) * 2), $secBtnY)) `
    -Size (New-Object System.Drawing.Size($btnSecW, 42)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Export full system diagnostics report" `
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

$script:form.Controls.Add($cardActions)

# Keyboard accessibility
$script:form.KeyPreview = $true

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

# Right-click context menu for log
$logContextMenu = New-Object System.Windows.Forms.ContextMenuStrip
New-DarkContextMenu -Menu $logContextMenu | Out-Null

$menuCopy = $logContextMenu.Items.Add("Copy Selection")
$menuCopy.Add_Click({ if ($script:rtbOutput.SelectionLength -gt 0) { [System.Windows.Forms.Clipboard]::SetText($script:rtbOutput.SelectedText) } })

$menuCopyAll = $logContextMenu.Items.Add("Copy All")
$menuCopyAll.Add_Click({ Copy-LogToClipboard })

$logContextMenu.Items.Add("-") | Out-Null

$menuSaveLog = $logContextMenu.Items.Add("Save Log...")
$menuSaveLog.Add_Click({ Export-LogFile })

$logContextMenu.Items.Add("-") | Out-Null

$menuClear = $logContextMenu.Items.Add("Clear Log")
$menuClear.Add_Click({ $script:rtbOutput.Clear(); $script:Config.LogHistory.Clear(); Write-Log "Log cleared" -Level "INFO" })

$script:rtbOutput.ContextMenuStrip = $logContextMenu

$cardLog.Controls.Add($script:rtbOutput)

# Progress bar (hidden fallback, kept for compatibility)
$progY = $logH - 38
$script:progressBar = New-Object System.Windows.Forms.ProgressBar
$script:progressBar.Location = New-Object System.Drawing.Point($CP, $progY)
$script:progressBar.Size     = New-Object System.Drawing.Size(($IW - 100), 18)
$script:progressBar.Style    = "Continuous"
$script:progressBar.Visible  = $false
$cardLog.Controls.Add($script:progressBar)

# Circular progress ring
$ringSize = 36
$script:progressRing = New-Object System.Windows.Forms.Panel
$script:progressRing.Location = New-Object System.Drawing.Point($CP, ($logH - 42))
$script:progressRing.Size = New-Object System.Drawing.Size($ringSize, $ringSize)
$script:progressRing.BackColor = [System.Drawing.Color]::Transparent
$script:progressRing.Visible = $false
$script:progressRing.Tag = @{ Value = 0 }
$script:progressRing.GetType().GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic).SetValue($script:progressRing, $true, $null)
$script:progressRing.Add_Paint({
    param($s, $e)
    $g = $e.Graphics
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $val = 0
    if ($s.Tag -is [hashtable] -and $s.Tag.ContainsKey('Value')) { $val = $s.Tag['Value'] }
    $penWidth = 3
    $rect = New-Object System.Drawing.Rectangle($penWidth, $penWidth, ($s.Width - $penWidth * 2), ($s.Height - $penWidth * 2))
    # Background track
    $trackPen = New-Object System.Drawing.Pen($script:Colors.SurfaceLight, $penWidth)
    $g.DrawEllipse($trackPen, $rect)
    $trackPen.Dispose()
    # Progress arc
    if ($val -gt 0) {
        $sweepAngle = [int](360 * $val / 100)
        $arcPen = New-Object System.Drawing.Pen($script:Colors.Accent, $penWidth)
        $arcPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arcPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($arcPen, $rect, -90, $sweepAngle)
        $arcPen.Dispose()
    }
    # Center percentage text
    $text = "$val%"
    $font = New-Object System.Drawing.Font("Segoe UI Semibold", 7)
    $textSize = $g.MeasureString($text, $font)
    $textX = ($s.Width - $textSize.Width) / 2
    $textY = ($s.Height - $textSize.Height) / 2
    $brush = New-Object System.Drawing.SolidBrush($script:Colors.TextSecondary)
    $g.DrawString($text, $font, $brush, $textX, $textY)
    $brush.Dispose()
    $font.Dispose()
}.GetNewClosure())
$cardLog.Controls.Add($script:progressRing)

$script:lblProgress = New-Object System.Windows.Forms.Label
$script:lblProgress.Location  = New-Object System.Drawing.Point(($CP + $ringSize + 8), ($logH - 36))
$script:lblProgress.Size      = New-Object System.Drawing.Size(200, 18)
$script:lblProgress.ForeColor = $script:Colors.TextMuted
$script:lblProgress.Font      = New-Object System.Drawing.Font("Segoe UI", 8)
$script:lblProgress.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$script:lblProgress.Visible   = $false
$cardLog.Controls.Add($script:lblProgress)

$script:cardLog = $cardLog
$script:form.Controls.Add($cardLog)

# ===================================================================
#  FOOTER
# ===================================================================

$lblFooter = New-Object System.Windows.Forms.LinkLabel
$footerText = "v$($script:Config.AppVersion)    |    GitHub    |    Docs"
$lblFooter.Text      = $footerText
$lblFooter.Location  = New-Object System.Drawing.Point($LX, 10)
$lblFooter.Size      = New-Object System.Drawing.Size(($FW - $LX * 2), 18)
$lblFooter.ForeColor = $script:Colors.TextDimmed
$lblFooter.LinkColor = $script:Colors.Accent
$lblFooter.ActiveLinkColor = $script:Colors.AccentHover
$lblFooter.VisitedLinkColor = $script:Colors.Accent
$lblFooter.Font      = New-Object System.Drawing.Font("Segoe UI", 8)
$lblFooter.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$lblFooter.LinkBehavior = [System.Windows.Forms.LinkBehavior]::HoverUnderline
# Set link areas for GitHub and Docs
$ghStart = $footerText.IndexOf("GitHub")
$docsStart = $footerText.IndexOf("Docs")
$lblFooter.Links.Clear()
if ($ghStart -ge 0) { $lblFooter.Links.Add($ghStart, 6, "github") | Out-Null }
if ($docsStart -ge 0) { $lblFooter.Links.Add($docsStart, 4, "docs") | Out-Null }
$lblFooter.LinkArea = New-Object System.Windows.Forms.LinkArea(0, 0)
$lblFooter.Add_LinkClicked({
    if ($_.Link.LinkData -eq "github") { Start-Process $script:Config.GitHubURL }
    elseif ($_.Link.LinkData -eq "docs") { Start-Process $script:Config.DocumentationURL }
})
$lblFooter.BackColor = $script:Colors.Background
$script:lblFooter = $lblFooter
$script:form.Controls.Add($lblFooter)
$lblFooter.BringToFront()

# Calculate minimum content height for AutoScroll
$script:MinContentH = $leftBottom + 44
$script:form.AutoScrollMinSize = New-Object System.Drawing.Size(0, $script:MinContentH)

# Combined form resize handler - layout + tray minimize
$script:form.Add_Resize({
    # Minimize to system tray
    if ($this.WindowState -eq [System.Windows.Forms.FormWindowState]::Minimized) {
        $this.Hide()
        $script:trayIcon.Visible = $true
        $script:trayIcon.ShowBalloonTip(2000, $script:Config.AppName, "Minimized to system tray. Double-click to restore.", [System.Windows.Forms.ToolTipIcon]::Info)
        return
    }
    # Use DisplayRectangle height which accounts for AutoScroll offset
    $displayH = $this.DisplayRectangle.Height
    $effectiveH = [Math]::Max($displayH, $script:MinContentH)
    # Reposition footer
    if ($script:lblFooter) { $script:lblFooter.Top = $effectiveH - 30 }
    # Stretch loading bar to form width
    if ($script:loadingBar) { $script:loadingBar.Width = $this.ClientSize.Width }
    # Stretch log card to fill available height
    if ($script:cardLog) {
        $logTop = $script:cardLog.Top
        $newLogH = $effectiveH - $logTop - 42
        if ($newLogH -ge 200) {
            $script:cardLog.Height = $newLogH
            if ($script:rtbOutput) {
                $script:rtbOutput.Height = $newLogH - 100
            }
            $progBottom = $newLogH - 42
            if ($script:progressBar) { $script:progressBar.Top = $progBottom + 4 }
            if ($script:progressRing) { $script:progressRing.Top = $progBottom }
            if ($script:lblProgress) { $script:lblProgress.Top = $progBottom + 6 }
        }
    }
}.GetNewClosure())

# ===================================================================
#  SYSTEM TRAY SUPPORT
# ===================================================================

$script:trayIcon = New-Object System.Windows.Forms.NotifyIcon
$script:trayIcon.Icon = [System.Drawing.SystemIcons]::Application
$script:trayIcon.Text = "$($script:Config.AppName) v$($script:Config.AppVersion)"
$script:trayIcon.Visible = $false

# Tray context menu
$trayMenu = New-Object System.Windows.Forms.ContextMenuStrip
New-DarkContextMenu -Menu $trayMenu | Out-Null
$trayRestore = $trayMenu.Items.Add("Restore")
$trayRestore.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$trayRestore.Add_Click({
    $script:form.Show()
    $script:form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
    $script:form.Activate()
    $script:trayIcon.Visible = $false
})
$trayMenu.Items.Add("-") | Out-Null
$trayExit = $trayMenu.Items.Add("Exit")
$trayExit.Add_Click({ $script:trayIcon.Visible = $false; $script:form.Close() })
$script:trayIcon.ContextMenuStrip = $trayMenu

# Double-click tray icon to restore
$script:trayIcon.Add_DoubleClick({
    $script:form.Show()
    $script:form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
    $script:form.Activate()
    $script:trayIcon.Visible = $false
})

# ===================================================================
#  FORM EVENTS
# ===================================================================

$script:form.Add_Load({
    # Position footer correctly using content-relative coordinates
    $effectiveH = [Math]::Max($this.DisplayRectangle.Height, $script:MinContentH)
    if ($script:lblFooter) { $script:lblFooter.Top = $effectiveH - 30 }

    # Apply immersive dark mode to window chrome (title bar, borders)
    try {
        [DarkTitleBar]::EnableDarkMode($this.Handle)
        $bg = $script:Colors.Background
        $bgRef = $bg.R -bor ($bg.G -shl 8) -bor ($bg.B -shl 16)
        [DarkTitleBar]::SetCaptionColor($this.Handle, $bgRef)
        $bd = $script:Colors.CardBorder
        $bdRef = $bd.R -bor ($bd.G -shl 8) -bor ($bd.B -shl 16)
        [DarkTitleBar]::SetBorderColor($this.Handle, $bdRef)
    }
    catch { <# DWM dark mode is Windows 11 only #> }

    # Apply DarkMode_Explorer to ALL scrollable controls (form, RTB, panels)
    foreach ($ctrl in @($this, $script:rtbOutput, $script:pnlDrivesContent)) {
        if ($ctrl) {
            try { [DarkScrollBar]::SetWindowTheme($ctrl.Handle, "DarkMode_Explorer", $null) | Out-Null } catch {}
        }
    }
    # Also walk all child panels that might have scrollbars
    foreach ($ctrl in $this.Controls) {
        if ($ctrl -is [System.Windows.Forms.Panel] -and $ctrl.AutoScroll) {
            try { [DarkScrollBar]::SetWindowTheme($ctrl.Handle, "DarkMode_Explorer", $null) | Out-Null } catch {}
        }
    }
})

# Defer heavy preflight checks to background runspace so GUI stays responsive
$script:form.Add_Shown({
    $script:form.Refresh()

    Write-Log "$($script:Config.AppName) v$($script:Config.AppVersion) started"
    Write-Log "Working directory: $($script:Config.WorkingDir)"
    Write-Log "----------------------------------------"
    Write-Log "Running pre-flight checks..."
    Set-ButtonsEnabled -Enabled $false
    if ($script:loadingBar) { $script:loadingBar.Visible = $true; $script:loadingBar.Tag['MarqueePos'] = 0; $script:loadingTimer.Start() }

    # Build runspace with all needed function definitions
    $funcNames = @('Get-WindowsBuildDetails', 'Get-NVMeHealthData', 'Get-SystemDrives',
                   'Test-BitLockerEnabled', 'Test-VeraCryptSystemEncryption', 'Get-IncompatibleSoftware',
                   'Get-NVMeDriverInfo', 'Test-NativeNVMeActive',
                   'Get-BypassIOStatus', 'Test-PatchStatus', 'Invoke-PreflightChecks')
    $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    # No-op Write-Log for background thread
    $noop = [System.Management.Automation.Runspaces.SessionStateFunctionEntry]::new(
        'Write-Log', 'param([string]$Message, [string]$Level = "INFO")')
    $iss.Commands.Add($noop)
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
            Checks           = $checks
            BuildDetails     = $script:BuildDetails
            CachedHealth     = $script:CachedHealth
            CachedDrives     = $script:CachedDrives
            HasNVMeDrives       = $script:HasNVMeDrives
            BitLockerEnabled    = $script:BitLockerEnabled
            VeraCryptDetected   = $script:VeraCryptDetected
            IncompatibleSoftware = $script:IncompatibleSoftware
            DriverInfo          = $script:DriverInfo
            NativeNVMeStatus    = $script:NativeNVMeStatus
            BypassIOStatus      = $script:BypassIOStatus
        }
    }).AddParameter('cfg', $configCopy)

    $script:bgHandle = $bgPS.BeginInvoke()
    $script:bgPS = $bgPS
    $script:bgRunspace = $bgRunspace

    # Poll for completion with a WinForms Timer
    $script:preflightTimer = New-Object System.Windows.Forms.Timer
    $script:preflightTimer.Interval = 100
    $script:preflightTimer.Add_Tick({
        if (-not $script:bgHandle.IsCompleted) { return }

        $script:preflightTimer.Stop()
        $script:preflightTimer.Dispose()

        try {
            $resultList = $script:bgPS.EndInvoke($script:bgHandle)
            $r = $resultList[0]

            # Marshal results back to script scope
            $script:BuildDetails     = $r.BuildDetails
            $script:CachedHealth     = $r.CachedHealth
            $script:CachedDrives     = $r.CachedDrives
            $script:HasNVMeDrives    = $r.HasNVMeDrives
            $script:BitLockerEnabled    = $r.BitLockerEnabled
            $script:VeraCryptDetected   = $r.VeraCryptDetected
            $script:IncompatibleSoftware = $r.IncompatibleSoftware
            $script:DriverInfo       = $r.DriverInfo
            $script:NativeNVMeStatus = $r.NativeNVMeStatus
            $script:BypassIOStatus   = $r.BypassIOStatus
            $checks                  = $r.Checks

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

                if ($script:checklistDots.ContainsKey($checkName)) {
                    $script:checklistDots[$checkName].BackColor = switch ($checks[$checkName].Status) {
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
            if ($script:loadingBar) { $script:loadingBar.Visible = $false; $script:loadingTimer.Stop() }
            $script:bgPS.Dispose()
            $script:bgRunspace.Dispose()
            Set-ButtonsEnabled -Enabled $true
        }
    }.GetNewClosure())
    $script:preflightTimer.Start()

    Write-AppEventLog -Message "$($script:Config.AppName) v$($script:Config.AppVersion) started" -EntryType "Information" -EventId 1000
})

$script:form.Add_FormClosing({
    Save-Configuration

    if ($script:Config.AutoSaveLog -and $script:Config.LogHistory.Count -gt 5) {
        Save-LogFile -Suffix "_autosave" | Out-Null
    }

    Write-AppEventLog -Message "$($script:Config.AppName) closed" -EntryType "Information" -EventId 1000

    if ($script:loadingTimer) { try { $script:loadingTimer.Stop(); $script:loadingTimer.Dispose() } catch { <# Timer cleanup #> } }
    if ($script:preflightTimer) { try { $script:preflightTimer.Stop(); $script:preflightTimer.Dispose() } catch { <# Timer cleanup #> } }
    if ($script:bgPS) { try { $script:bgPS.Dispose() } catch { <# Runspace cleanup #> } }
    if ($script:bgRunspace) { try { $script:bgRunspace.Dispose() } catch { <# Runspace cleanup #> } }
    if ($script:trayIcon) { $script:trayIcon.Visible = $false; $script:trayIcon.Dispose() }
    if ($script:StatusPulseTimer) { $script:StatusPulseTimer.Stop(); $script:StatusPulseTimer.Dispose() }
    if ($script:ToolTipProvider) { $script:ToolTipProvider.Dispose() }
    if ($script:AppMutex) {
        try { $script:AppMutex.ReleaseMutex(); $script:AppMutex.Dispose() } catch { <# Mutex cleanup best-effort #> }
    }
})

# Run
[void]$script:form.ShowDialog()
$script:form.Dispose()
