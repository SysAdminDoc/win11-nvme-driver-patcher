<#
.SYNOPSIS
    NVMe Driver Patcher for Windows 11
    
.DESCRIPTION
    GUI tool to enable the experimental Server 2025 NVMe driver in Windows 11.
    
    SAFETY FEATURES:
    - Administrator privilege verification
    - Windows version compatibility check
    - Automatic System Protection enablement for C: drive
    - Mandatory restore point creation before changes
    - Confirmation dialogs for all critical operations
    - Comprehensive logging with export capability
    
    REGISTRY KEYS MODIFIED (5 COMPONENT ATOMIC PATCH):
    1. Feature Flag: 735209102
    2. Feature Flag: 1853569164
    3. Feature Flag: 156965516
    4. SafeBoot Minimal: {75416E63-5912-4DFA-AE8F-3EFACCAFFB14}
    5. SafeBoot Network: {75416E63-5912-4DFA-AE8F-3EFACCAFFB14}

.NOTES
    Version: 2.6.3
    Author:  Matthew Parker
    Requires: Windows 11, Administrator privileges
    
.LINK
    https://learn.microsoft.com/en-us/windows-server/storage/

#>

#Requires -Version 5.1

# ===========================================================================
# SECTION 1: INITIALIZATION & PRIVILEGE ELEVATION
# ===========================================================================

# Error handling preference
$ErrorActionPreference = "Continue"

# Check and request Administrator privileges
function Test-Administrator {
    $currentPrincipal = New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent()
    )
    return $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    try {
        $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        Start-Process powershell.exe -ArgumentList $arguments -Verb RunAs
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show(
            "This application requires Administrator privileges.`n`nPlease right-click and select 'Run as Administrator'.",
            "Elevation Required",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        )
    }
    exit
}

# ===========================================================================
# SECTION 2: ASSEMBLY LOADING & GLOBAL CONFIGURATION
# ===========================================================================

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# Global Configuration
$script:Config = @{
    AppName        = "NVMe Driver Patcher"
    AppVersion     = "2.6.3"
    RegistryPath   = "HKLM:\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides"
    FeatureIDs     = @("735209102", "1853569164", "156965516")
    SafeBootMinimal = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"
    SafeBootNetwork = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"
    SafeBootValue   = "Storage Disks"
    MinWinBuild    = 22000  # Windows 11 minimum build
    LogHistory     = [System.Collections.ArrayList]::new()
    WorkingDir     = $null  # Set at startup
    TotalComponents = 5     # 3 Features + 2 SafeBoot Keys
}

# Initialize working directory at startup
$script:Config.WorkingDir = Join-Path ([Environment]::GetFolderPath("Desktop")) "NVMe Patcher"
if (-not (Test-Path $script:Config.WorkingDir)) {
    try {
        New-Item -Path $script:Config.WorkingDir -ItemType Directory -Force | Out-Null
    }
    catch {
        # Fall back to temp if desktop folder creation fails
        $script:Config.WorkingDir = Join-Path $env:TEMP "NVMePatcher_Backups"
        if (-not (Test-Path $script:Config.WorkingDir)) {
            New-Item -Path $script:Config.WorkingDir -ItemType Directory -Force | Out-Null
        }
    }
}

# ===========================================================================
# THEME DETECTION
# ===========================================================================

function Get-WindowsThemeMode {
    try {
        $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
        $val = Get-ItemProperty -Path $key -Name "AppsUseLightTheme" -ErrorAction SilentlyContinue
        if ($null -ne $val -and $val.AppsUseLightTheme -eq 1) {
            return "Light"
        }
    }
    catch {}
    return "Dark" # Default to Dark if unsure
}

$currentTheme = Get-WindowsThemeMode

# Define Palettes
$PaletteDark = @{
    Background      = [System.Drawing.Color]::FromArgb(32, 32, 32)
    Surface         = [System.Drawing.Color]::FromArgb(40, 40, 40)
    SurfaceLight    = [System.Drawing.Color]::FromArgb(50, 50, 50)
    SurfaceHover    = [System.Drawing.Color]::FromArgb(60, 60, 60)
    CardBackground  = [System.Drawing.Color]::FromArgb(44, 44, 44)
    CardBorder      = [System.Drawing.Color]::FromArgb(60, 60, 60)
    Border          = [System.Drawing.Color]::FromArgb(70, 70, 70)
    TextPrimary     = [System.Drawing.Color]::FromArgb(255, 255, 255)
    TextSecondary   = [System.Drawing.Color]::FromArgb(200, 200, 200)
    TextMuted       = [System.Drawing.Color]::FromArgb(150, 150, 150)
    TextDimmed      = [System.Drawing.Color]::FromArgb(110, 110, 110)
    WarningDim      = [System.Drawing.Color]::FromArgb(55, 50, 40)
}

$PaletteLight = @{
    Background      = [System.Drawing.Color]::FromArgb(243, 243, 243)
    Surface         = [System.Drawing.Color]::FromArgb(255, 255, 255)
    SurfaceLight    = [System.Drawing.Color]::FromArgb(240, 240, 240)
    SurfaceHover    = [System.Drawing.Color]::FromArgb(230, 230, 230)
    CardBackground  = [System.Drawing.Color]::FromArgb(255, 255, 255)
    CardBorder      = [System.Drawing.Color]::FromArgb(220, 220, 220)
    Border          = [System.Drawing.Color]::FromArgb(200, 200, 200)
    TextPrimary     = [System.Drawing.Color]::FromArgb(20, 20, 20)
    TextSecondary   = [System.Drawing.Color]::FromArgb(60, 60, 60)
    TextMuted       = [System.Drawing.Color]::FromArgb(100, 100, 100)
    TextDimmed      = [System.Drawing.Color]::FromArgb(140, 140, 140)
    WarningDim      = [System.Drawing.Color]::FromArgb(255, 248, 220)
}

# Select Palette
$Theme = if ($currentTheme -eq "Light") { $PaletteLight } else { $PaletteDark }

$script:Colors = @{
    Background      = $Theme.Background
    Surface         = $Theme.Surface
    SurfaceLight    = $Theme.SurfaceLight
    SurfaceHover    = $Theme.SurfaceHover
    CardBackground  = $Theme.CardBackground
    CardBorder      = $Theme.CardBorder
    Border          = $Theme.Border
    TextPrimary     = $Theme.TextPrimary
    TextSecondary   = $Theme.TextSecondary
    TextMuted       = $Theme.TextMuted
    TextDimmed      = $Theme.TextDimmed
    WarningDim      = $Theme.WarningDim
    
    # Constants across themes
    Accent          = [System.Drawing.Color]::FromArgb(0, 120, 212)
    AccentDark      = [System.Drawing.Color]::FromArgb(0, 90, 158)
    AccentHover     = [System.Drawing.Color]::FromArgb(30, 140, 230)
    Success         = [System.Drawing.Color]::FromArgb(16, 124, 16)
    SuccessHover    = [System.Drawing.Color]::FromArgb(26, 144, 26)
    Warning         = [System.Drawing.Color]::FromArgb(180, 130, 0)
    WarningBright   = [System.Drawing.Color]::FromArgb(255, 185, 0)
    Danger          = [System.Drawing.Color]::FromArgb(232, 17, 35)
    DangerHover     = [System.Drawing.Color]::FromArgb(250, 50, 50)
    Info            = [System.Drawing.Color]::FromArgb(0, 99, 177)
}

if ($currentTheme -eq "Dark") {
    $script:Colors.Warning = [System.Drawing.Color]::FromArgb(252, 185, 65)
}

# ===========================================================================
# SECTION 3: CUSTOM UI COMPONENTS
# ===========================================================================

# Helper function to create rounded region
function Get-RoundedRegion {
    param(
        [int]$Width,
        [int]$Height,
        [int]$Radius = 8
    )
    
    if ($Width -le 0 -or $Height -le 0) {
        return $null
    }
    
    $path = $null
    $region = $null
    
    try {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $diameter = $Radius * 2
        
        $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
        $path.AddArc($Width - $diameter, 0, $diameter, $diameter, 270, 90)
        $path.AddArc($Width - $diameter, $Height - $diameter, $diameter, $diameter, 0, 90)
        $path.AddArc(0, $Height - $diameter, $diameter, $diameter, 90, 90)
        $path.CloseFigure()
        
        $region = New-Object System.Drawing.Region($path)
    }
    finally {
        if ($path) {
            $path.Dispose()
        }
    }
    
    return $region
}

# Safely merge data into Control.Tag without overwriting existing data
function Set-ControlTagData {
    param(
        [System.Windows.Forms.Control]$Control,
        [hashtable]$NewData
    )
    
    $existingTag = $Control.Tag
    
    if ($null -eq $existingTag) {
        $Control.Tag = $NewData.Clone()
    }
    elseif ($existingTag -is [hashtable]) {
        foreach ($key in $NewData.Keys) {
            $existingTag[$key] = $NewData[$key]
        }
    }
    else {
        $merged = $NewData.Clone()
        $merged['_OriginalTag'] = $existingTag
        $Control.Tag = $merged
    }
}

# Get value from Control.Tag safely
function Get-ControlTagValue {
    param(
        [System.Windows.Forms.Control]$Control,
        [string]$Key,
        $Default = $null
    )
    
    $tag = $Control.Tag
    if ($null -eq $tag) { return $Default }
    if ($tag -is [hashtable] -and $tag.ContainsKey($Key)) {
        return $tag[$key]
    }
    return $Default
}

# Apply rounded corners with resize handler
function Set-RoundedCorners {
    param(
        [System.Windows.Forms.Control]$Control,
        [int]$Radius = 8
    )
    
    Set-ControlTagData -Control $Control -NewData @{ 
        CornerRadius = $Radius 
        _ResizeHandlerAttached = $true
    }
    
    $region = Get-RoundedRegion -Width $Control.Width -Height $Control.Height -Radius $Radius
    if ($region) {
        $oldRegion = $Control.Region
        $Control.Region = $region
        if ($oldRegion) {
            $oldRegion.Dispose()
        }
    }
    
    $alreadyAttached = Get-ControlTagValue -Control $Control -Key '_ResizeHandlerAttached' -Default $false
    if (-not $alreadyAttached) {
        Set-ControlTagData -Control $Control -NewData @{ _ResizeHandlerAttached = $true }
    }
    
    $Control.Add_Resize({
        $r = Get-ControlTagValue -Control $this -Key 'CornerRadius' -Default 8
        $newRegion = Get-RoundedRegion -Width $this.Width -Height $this.Height -Radius $r
        if ($newRegion) {
            $oldRegion = $this.Region
            $this.Region = $newRegion
            if ($oldRegion) {
                $oldRegion.Dispose()
            }
        }
    })
}

# Create a modern card panel
function New-CardPanel {
    param(
        [System.Drawing.Point]$Location,
        [System.Drawing.Size]$Size,
        [string]$Title = "",
        [int]$Padding = 16,
        [int]$CornerRadius = 8
    )
    
    $card = New-Object System.Windows.Forms.Panel
    $card.Location = $Location
    $card.Size = $Size
    $card.BackColor = $script:Colors.CardBackground
    $card.Padding = New-Object System.Windows.Forms.Padding($Padding)
    
    $card.GetType().GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic).SetValue($card, $true, $null)
    
    Set-ControlTagData -Control $card -NewData @{ 
        CornerRadius = $CornerRadius
        Title = $Title 
    }
    
    $region = Get-RoundedRegion -Width $Size.Width -Height $Size.Height -Radius $CornerRadius
    if ($region) {
        $card.Region = $region
    }
    
    $card.Add_Resize({
        $r = Get-ControlTagValue -Control $this -Key 'CornerRadius' -Default 8
        $newRegion = Get-RoundedRegion -Width $this.Width -Height $this.Height -Radius $r
        if ($newRegion) {
            $oldRegion = $this.Region
            $this.Region = $newRegion
            if ($oldRegion) {
                $oldRegion.Dispose()
            }
        }
    })
    
    $card.Add_Paint({
        param($sender, $e)
        $g = $e.Graphics
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        
        $r = Get-ControlTagValue -Control $sender -Key 'CornerRadius' -Default 8
        $diameter = $r * 2
        
        $pen = $null
        $borderPath = $null
        
        try {
            $pen = New-Object System.Drawing.Pen($script:Colors.CardBorder, 1)
            $rect = New-Object System.Drawing.Rectangle(0, 0, ($sender.Width - 1), ($sender.Height - 1))
            
            $borderPath = New-Object System.Drawing.Drawing2D.GraphicsPath
            $borderPath.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
            $borderPath.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
            $borderPath.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
            $borderPath.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
            $borderPath.CloseFigure()
            
            $g.DrawPath($pen, $borderPath)
        }
        finally {
            if ($borderPath) { $borderPath.Dispose() }
            if ($pen) { $pen.Dispose() }
        }
    })
    
    if ($Title) {
        $titleLabel = New-Object System.Windows.Forms.Label
        $titleLabel.Text = $Title.ToUpper()
        $titleLabel.Location = New-Object System.Drawing.Point($Padding, 12)
        $titleLabel.Size = New-Object System.Drawing.Size(($Size.Width - $Padding * 2), 16)
        $titleLabel.ForeColor = $script:Colors.TextMuted
        $titleLabel.Font = New-Object System.Drawing.Font("Segoe UI", 8, [System.Drawing.FontStyle]::Bold)
        Set-ControlTagData -Control $titleLabel -NewData @{ Role = "cardTitle" }
        $card.Controls.Add($titleLabel)
    }
    
    return $card
}

# Modern flat button
function New-ModernButton {
    param(
        [string]$Text,
        [System.Drawing.Point]$Location,
        [System.Drawing.Size]$Size,
        [System.Drawing.Color]$BackColor,
        [System.Drawing.Color]$HoverColor,
        [System.Drawing.Color]$ForeColor = $script:Colors.TextPrimary,
        [scriptblock]$OnClick,
        [string]$ToolTip = "",
        [bool]$IsPrimary = $false,
        [int]$CornerRadius = 6
    )
    
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Location = $Location
    $button.Size = $Size
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.FlatAppearance.BorderSize = 0
    $button.BackColor = $BackColor
    $button.ForeColor = $ForeColor
    $button.Cursor = [System.Windows.Forms.Cursors]::Hand
    
    Set-ControlTagData -Control $button -NewData @{ 
        Original = $BackColor
        Hover = $HoverColor
        IsPrimary = $IsPrimary
        CornerRadius = $CornerRadius 
    }
    
    if ($IsPrimary) {
        $button.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 10)
    } else {
        $button.Font = New-Object System.Drawing.Font("Segoe UI", 9.5)
    }
    
    $region = Get-RoundedRegion -Width $Size.Width -Height $Size.Height -Radius $CornerRadius
    if ($region) {
        $button.Region = $region
    }
    
    $button.Add_Resize({
        $r = Get-ControlTagValue -Control $this -Key 'CornerRadius' -Default 6
        $newRegion = Get-RoundedRegion -Width $this.Width -Height $this.Height -Radius $r
        if ($newRegion) {
            $oldRegion = $this.Region
            $this.Region = $newRegion
            if ($oldRegion) {
                $oldRegion.Dispose()
            }
        }
    })
    
    $button.Add_MouseEnter({
        if ($this.Enabled) {
            $hoverColor = Get-ControlTagValue -Control $this -Key 'Hover'
            if ($hoverColor) {
                $this.BackColor = $hoverColor
            }
        }
    })
    
    $button.Add_MouseLeave({
        if ($this.Enabled) {
            $originalColor = Get-ControlTagValue -Control $this -Key 'Original'
            if ($originalColor) {
                $this.BackColor = $originalColor
            }
        }
    })
    
    $button.Add_EnabledChanged({
        if ($this.Enabled) {
            $originalColor = Get-ControlTagValue -Control $this -Key 'Original'
            if ($originalColor) {
                $this.BackColor = $originalColor
            }
            $this.ForeColor = $script:Colors.TextPrimary
            $this.Cursor = [System.Windows.Forms.Cursors]::Hand
        } else {
            $this.BackColor = $script:Colors.SurfaceLight
            $this.ForeColor = $script:Colors.TextDimmed
            $this.Cursor = [System.Windows.Forms.Cursors]::Default
        }
    })
    
    if ($OnClick) {
        $button.Add_Click($OnClick)
    }
    
    if ($ToolTip) {
        $script:ToolTipProvider.SetToolTip($button, $ToolTip)
    }
    
    return $button
}

# Enhanced status indicator
function New-StatusIndicator {
    param(
        [System.Drawing.Point]$Location,
        [System.Drawing.Size]$Size,
        [string]$Label = ""
    )
    
    $panel = New-Object System.Windows.Forms.Panel
    $panel.Location = $Location
    $panel.Size = $Size
    $panel.BackColor = [System.Drawing.Color]::Transparent
    
    $labelCtrl = New-Object System.Windows.Forms.Label
    $labelCtrl.Location = New-Object System.Drawing.Point(0, 2)
    $labelCtrl.Size = New-Object System.Drawing.Size($Size.Width, 16)
    $labelCtrl.ForeColor = $script:Colors.TextDimmed
    $labelCtrl.Font = New-Object System.Drawing.Font("Segoe UI", 7.5)
    $labelCtrl.Text = $Label.ToUpper()
    Set-ControlTagData -Control $labelCtrl -NewData @{ Role = "title" }
    
    $iconPanel = New-Object System.Windows.Forms.Panel
    $iconPanel.Size = New-Object System.Drawing.Size(10, 10)
    $iconPanel.Location = New-Object System.Drawing.Point(0, 26)
    $iconPanel.BackColor = $script:Colors.TextMuted
    Set-ControlTagData -Control $iconPanel -NewData @{ Role = "icon" }
    
    $iconPath = $null
    try {
        $iconPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $iconPath.AddEllipse(0, 0, 10, 10)
        $iconPanel.Region = New-Object System.Drawing.Region($iconPath)
    }
    finally {
        if ($iconPath) { $iconPath.Dispose() }
    }
    
    $statusLabel = New-Object System.Windows.Forms.Label
    $statusLabel.Location = New-Object System.Drawing.Point(16, 22)
    $statusLabel.Size = New-Object System.Drawing.Size(($Size.Width - 20), 22)
    $statusLabel.ForeColor = $script:Colors.TextSecondary
    $statusLabel.Font = New-Object System.Drawing.Font("Segoe UI Semibold", 9.5)
    $statusLabel.Text = "Checking..."
    Set-ControlTagData -Control $statusLabel -NewData @{ Role = "status" }
    
    $panel.Controls.Add($labelCtrl)
    $panel.Controls.Add($iconPanel)
    $panel.Controls.Add($statusLabel)
    
    return $panel
}

# Registry key status row
function New-KeyStatusRow {
    param(
        [System.Drawing.Point]$Location,
        [string]$KeyName,
        [string]$Description
    )
    
    $panel = New-Object System.Windows.Forms.Panel
    $panel.Location = $Location
    $panel.Size = New-Object System.Drawing.Size(492, 22)
    $panel.BackColor = [System.Drawing.Color]::Transparent
    
    $dot = New-Object System.Windows.Forms.Panel
    $dot.Size = New-Object System.Drawing.Size(8, 8)
    $dot.Location = New-Object System.Drawing.Point(0, 7)
    $dot.BackColor = $script:Colors.TextMuted
    Set-ControlTagData -Control $dot -NewData @{ Role = "dot" }
    
    $dotPath = $null
    try {
        $dotPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $dotPath.AddEllipse(0, 0, 8, 8)
        $dot.Region = New-Object System.Drawing.Region($dotPath)
    }
    finally {
        if ($dotPath) { $dotPath.Dispose() }
    }
    
    $keyLabel = New-Object System.Windows.Forms.Label
    $keyLabel.Location = New-Object System.Drawing.Point(16, 2)
    $keyLabel.Size = New-Object System.Drawing.Size(95, 18)
    $keyLabel.ForeColor = $script:Colors.TextPrimary
    $keyLabel.Font = New-Object System.Drawing.Font("Cascadia Code, Consolas", 8.5)
    $keyLabel.Text = $KeyName
    Set-ControlTagData -Control $keyLabel -NewData @{ Role = "keyname" }
    
    $descLabel = New-Object System.Windows.Forms.Label
    $descLabel.Location = New-Object System.Drawing.Point(118, 2)
    $descLabel.Size = New-Object System.Drawing.Size(374, 18)
    $descLabel.ForeColor = $script:Colors.TextMuted
    $descLabel.Font = New-Object System.Drawing.Font("Segoe UI", 8)
    $descLabel.Text = $Description
    Set-ControlTagData -Control $descLabel -NewData @{ Role = "description" }
    
    $panel.Controls.Add($dot)
    $panel.Controls.Add($keyLabel)
    $panel.Controls.Add($descLabel)
    
    return $panel
}

# Update a key status row's dot color
function Update-KeyStatusRow {
    param(
        [System.Windows.Forms.Panel]$Row,
        [bool]$IsApplied
    )
    
    $dot = $Row.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "dot" }
    if ($dot) {
        if ($IsApplied) {
            $dot.BackColor = $script:Colors.Success
        }
        else {
            $dot.BackColor = $script:Colors.Danger
        }
    }
}

# Check if a specific registry key/value exists
function Test-RegistryKeyValue {
    param(
        [string]$Path,
        [string]$Name = $null,
        [int]$ExpectedValue = $null
    )
    
    try {
        if (-not (Test-Path $Path)) {
            return $false
        }
        
        if ($Name) {
            $value = Get-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue
            if ($null -eq $value) { return $false }
            
            $propValue = $value | Select-Object -ExpandProperty $Name -ErrorAction SilentlyContinue
            if ($null -ne $ExpectedValue) {
                return ($propValue -eq $ExpectedValue)
            }
            return ($null -ne $propValue)
        }
        else {
            $value = Get-ItemProperty -Path $Path -Name "(Default)" -ErrorAction SilentlyContinue
            return ($null -ne $value)
        }
    }
    catch {
        return $false
    }
}

# Authoritative Drive Detection using MSFT_Disk and Win32_DiskDrive
function Get-SystemDrives {
    $drives = [System.Collections.ArrayList]::new()
    
    try {
        # 1. Get Physical Disks via Storage Module (Primary Source for BusType/Attributes)
        # Namespace is present on Win10/11/Server2016+
        $msftDisks = Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_Disk -ErrorAction Stop
        
        # 2. Get WMI Disks for PNP/Model correlation
        $win32Disks = Get-CimInstance -ClassName Win32_DiskDrive -ErrorAction SilentlyContinue
        
        foreach ($mDisk in $msftDisks) {
            # Correlate via Index/Number
            $wDisk = $win32Disks | Where-Object { $_.Index -eq $mDisk.Number } | Select-Object -First 1
            
            # Determine Name/Model
            $friendlyName = if ($wDisk) { $wDisk.Model } else { $mDisk.FriendlyName }
            $pnpId = if ($wDisk) { $wDisk.PNPDeviceID } else { "Unknown" }
            
            # BusType Detection (MSFT_Disk.BusType Enum)
            # 17 = NVMe, 11 = SATA, 7 = USB, 8 = RAID
            $busEnum = $mDisk.BusType
            $isNVMe = ($busEnum -eq 17)
            
            # Fallback for "SCSI" masquerading as NVMe
            if (-not $isNVMe -and ($pnpId -match "NVMe" -or $friendlyName -match "NVMe")) {
                $isNVMe = $true
            }
            
            # Bus Label
            $busLabel = switch ($busEnum) {
                17 { "NVMe" }
                11 { "SATA" }
                7  { "USB" }
                8  { "RAID" }
                default { 
                    if ($isNVMe) { "NVMe" } else { "Other" }
                }
            }
            
            # System/Boot Status
            $isBoot = ($mDisk.IsBoot -eq $true -or $mDisk.IsSystem -eq $true)
            
            # Size Formatting
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
        
        # Sort by Disk Number
        $drives = [System.Collections.ArrayList]@($drives | Sort-Object Number)
    }
    catch {
        Write-Log "Error scanning drives: $($_.Exception.Message)" -Level "WARNING"
    }
    
    return $drives
}

# Check if any NVMe drives exist in the system
function Test-NVMePresent {
    $drives = Get-SystemDrives
    foreach ($drive in $drives) {
        if ($drive.IsNVMe) {
            return $true
        }
    }
    return $false
}

# Modern Drive Status Row with Iconography and Tooltips
function New-DriveStatusRow {
    param(
        [System.Drawing.Point]$Location,
        [PSCustomObject]$DriveObject
    )
    
    # Adjust width to account for possible scrollbar (460px safe width)
    $panel = New-Object System.Windows.Forms.Panel
    $panel.Location = $Location
    $panel.Size = New-Object System.Drawing.Size(470, 24)
    $panel.BackColor = [System.Drawing.Color]::Transparent
    
    # --- Iconography ---
    $iconChar = "🖴" # Default generic
    $iconColor = $script:Colors.TextMuted
    
    switch ($DriveObject.BusType) {
        "NVMe" { 
            $iconChar = "⚡" 
            $iconColor = $script:Colors.Warning 
        }
        "USB" { 
            $iconChar = "🔌" 
            $iconColor = $script:Colors.Info 
        }
        "SATA" {
            $iconChar = "🖴"
            $iconColor = $script:Colors.TextSecondary
        }
    }
    
    # Icon Label
    $iconLabel = New-Object System.Windows.Forms.Label
    $iconLabel.Location = New-Object System.Drawing.Point(0, 0)
    $iconLabel.Size = New-Object System.Drawing.Size(24, 24)
    $iconLabel.Text = $iconChar
    $iconLabel.ForeColor = $iconColor
    $iconLabel.Font = New-Object System.Drawing.Font("Segoe UI Symbol", 10)
    $iconLabel.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    
    # Disk Number Label
    $numLabel = New-Object System.Windows.Forms.Label
    $numLabel.Location = New-Object System.Drawing.Point(26, 3)
    $numLabel.Size = New-Object System.Drawing.Size(55, 18)
    $numLabel.ForeColor = $script:Colors.TextPrimary
    $numLabel.Font = New-Object System.Drawing.Font("Segoe UI", 8, [System.Drawing.FontStyle]::Bold)
    $numLabel.Text = "Disk $($DriveObject.Number)"
    
    # Model Label
    $nameLabel = New-Object System.Windows.Forms.Label
    $nameLabel.Location = New-Object System.Drawing.Point(85, 3)
    $nameLabel.Size = New-Object System.Drawing.Size(200, 18)
    $nameLabel.ForeColor = $script:Colors.TextSecondary
    $nameLabel.Font = New-Object System.Drawing.Font("Segoe UI", 8)
    # Truncate
    $displayName = if ($DriveObject.Name.Length -gt 28) { $DriveObject.Name.Substring(0, 26) + "..." } else { $DriveObject.Name }
    $nameLabel.Text = $displayName
    
    # Badges (BusType + Boot)
    $badgePanel = New-Object System.Windows.Forms.FlowLayoutPanel
    $badgePanel.Location = New-Object System.Drawing.Point(290, 3)
    $badgePanel.Size = New-Object System.Drawing.Size(100, 18)
    $badgePanel.FlowDirection = [System.Windows.Forms.FlowDirection]::LeftToRight
    $badgePanel.WrapContents = $false
    
    # Bus Badge
    $busLabel = New-Object System.Windows.Forms.Label
    $busLabel.AutoSize = $true
    $busLabel.Text = $DriveObject.BusType
    $busLabel.Font = New-Object System.Drawing.Font("Segoe UI", 7.5, [System.Drawing.FontStyle]::Bold)
    $busLabel.ForeColor = if ($DriveObject.IsNVMe) { $script:Colors.Success } else { $script:Colors.TextDimmed }
    $busLabel.Padding = New-Object System.Windows.Forms.Padding(0, 0, 5, 0)
    $badgePanel.Controls.Add($busLabel)
    
    # Boot Badge
    if ($DriveObject.IsBoot) {
        $bootLabel = New-Object System.Windows.Forms.Label
        $bootLabel.AutoSize = $true
        $bootLabel.Text = "BOOT"
        $bootLabel.Font = New-Object System.Drawing.Font("Segoe UI", 7.5, [System.Drawing.FontStyle]::Bold)
        $bootLabel.ForeColor = $script:Colors.Accent
        $badgePanel.Controls.Add($bootLabel)
    }
    
    # Size Label
    $sizeLabel = New-Object System.Windows.Forms.Label
    $sizeLabel.Location = New-Object System.Drawing.Point(395, 3)
    $sizeLabel.Size = New-Object System.Drawing.Size(70, 18)
    $sizeLabel.ForeColor = $script:Colors.TextDimmed
    $sizeLabel.Font = New-Object System.Drawing.Font("Segoe UI", 8)
    $sizeLabel.Text = $DriveObject.Size
    $sizeLabel.TextAlign = [System.Drawing.ContentAlignment]::TopRight
    
    $panel.Controls.Add($iconLabel)
    $panel.Controls.Add($numLabel)
    $panel.Controls.Add($nameLabel)
    $panel.Controls.Add($badgePanel)
    $panel.Controls.Add($sizeLabel)
    
    # Tooltip Logic
    $tipText = "PNP ID: $($DriveObject.PNPDeviceID)`nModel: $($DriveObject.Name)"
    $script:ToolTipProvider.SetToolTip($panel, $tipText)
    $script:ToolTipProvider.SetToolTip($nameLabel, $tipText)
    $script:ToolTipProvider.SetToolTip($iconLabel, $tipText)
    
    return $panel
}

# Global variable to track NVMe presence
$script:HasNVMeDrives = $false

# ===========================================================================
# SECTION 4: LOGGING SYSTEM
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
        [System.Windows.Forms.Application]::DoEvents()
    }
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

# ===========================================================================
# SECTION 5: SYSTEM VALIDATION FUNCTIONS
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
        return $true  # Allow to continue with warning
    }
}

function Test-PatchStatus {
    Write-Log "Checking current patch status (5 components)..." -Level "DEBUG"
    
    $count = 0
    $appliedKeys = [System.Collections.ArrayList]::new()
    
    # 1. Check Feature Flags (3 Items)
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
    
    # 2. Check SafeBoot Minimal (1 Item)
    if (Test-Path $script:Config.SafeBootMinimal) {
        $val = Get-ItemProperty -Path $script:Config.SafeBootMinimal -Name "(Default)" -ErrorAction SilentlyContinue
        if ($val -and $val."(Default)" -eq $script:Config.SafeBootValue) {
            $count++
            [void]$appliedKeys.Add("SafeBootMinimal")
        }
    }
    
    # 3. Check SafeBoot Network (1 Item)
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
    
    if ($result.Applied) {
        Write-Log "Patch status: APPLIED ($count/$($script:Config.TotalComponents) components)" -Level "SUCCESS"
    }
    elseif ($result.Partial) {
        Write-Log "Patch status: PARTIAL ($count/$($script:Config.TotalComponents) components)" -Level "WARNING"
    }
    else {
        Write-Log "Patch status: NOT APPLIED (0/$($script:Config.TotalComponents) components)" -Level "INFO"
    }
    
    return $result
}

function Update-StatusIndicator {
    param(
        [System.Windows.Forms.Panel]$Panel,
        [ValidateSet("OK", "Warning", "Error", "Neutral", "Checking")]
        [string]$Status,
        [string]$Text
    )
    
    $icon = $Panel.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "icon" }
    $label = $Panel.Controls | Where-Object { (Get-ControlTagValue -Control $_ -Key 'Role') -eq "status" }
    
    $color = switch ($Status) {
        "OK"       { $script:Colors.Success }
        "Warning"  { $script:Colors.Warning }
        "Error"    { $script:Colors.Danger }
        "Checking" { $script:Colors.Info }
        default    { $script:Colors.TextMuted }
    }
    
    if ($icon) { $icon.BackColor = $color }
    
    if ($label) { 
        $label.Text = $Text 
        $label.ForeColor = $script:Colors.TextSecondary
    }
}

# ===========================================================================
# SECTION 6: SYSTEM PROTECTION & RESTORE POINT FUNCTIONS
# ===========================================================================

function Enable-SystemProtectionSafe {
    Write-Log "Configuring System Protection..."
    
    # Start VSS Service
    try {
        $vss = Get-Service -Name "vss" -ErrorAction Stop
        if ($vss.StartType -eq 'Disabled') {
            Write-Log "Enabling VSS service..."
            Set-Service -Name "vss" -StartupType Manual -ErrorAction Stop
        }
        if ($vss.Status -ne 'Running') {
            Write-Log "Starting VSS service..."
            Start-Service -Name "vss" -ErrorAction Stop
            Start-Sleep -Milliseconds 500
        }
        Write-Log "VSS service ready" -Level "SUCCESS"
    }
    catch {
        Write-Log "VSS service error: $($_.Exception.Message)" -Level "WARNING"
    }
    
    # Start SWPRV Service
    try {
        $swprv = Get-Service -Name "swprv" -ErrorAction Stop
        if ($swprv.StartType -eq 'Disabled') {
            Write-Log "Enabling Shadow Copy Provider..."
            Set-Service -Name "swprv" -StartupType Manual -ErrorAction Stop
        }
        if ($swprv.Status -ne 'Running') {
            Write-Log "Starting Shadow Copy Provider..."
            Start-Service -Name "swprv" -ErrorAction Stop
            Start-Sleep -Milliseconds 500
        }
        Write-Log "Shadow Copy Provider ready" -Level "SUCCESS"
    }
    catch {
        Write-Log "Shadow Copy Provider error: $($_.Exception.Message)" -Level "WARNING"
    }
    
    # Enable System Restore on System Drive
    try {
        $systemDrive = "$env:SystemDrive\"
        Write-Log "Enabling System Protection on $systemDrive..."
        Enable-ComputerRestore -Drive $systemDrive -ErrorAction Stop
        Write-Log "System Protection enabled on $systemDrive" -Level "SUCCESS"
        return $true
    }
    catch {
        if ($_.Exception.Message -match "already enabled") {
            Write-Log "System Protection already enabled" -Level "SUCCESS"
            return $true
        }
        Write-Log "Could not enable System Protection: $($_.Exception.Message)" -Level "ERROR"
        return $false
    }
}

function New-SafeRestorePoint {
    param(
        [string]$Description = "NVMe Patcher Backup"
    )
    
    Write-Log "Creating system restore point..."
    
    $protectionReady = Enable-SystemProtectionSafe
    
    if (-not $protectionReady) {
        $result = [System.Windows.Forms.MessageBox]::Show(
            "System Protection could not be verified.`n`nDo you want to continue WITHOUT a restore point?`n`nThis is NOT recommended!",
            "Protection Warning",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        )
        
        if ($result -ne [System.Windows.Forms.DialogResult]::Yes) {
            Write-Log "Operation cancelled by user" -Level "WARNING"
            return $false
        }
        
        Write-Log "User chose to continue without restore point" -Level "WARNING"
        return $true
    }
    
    try {
        Checkpoint-Computer -Description $Description -RestorePointType "MODIFY_SETTINGS" -ErrorAction Stop
        Write-Log "Restore point created: '$Description'" -Level "SUCCESS"
        return $true
    }
    catch {
        $errorMsg = $_.Exception.Message
        
        if ($errorMsg -match "1111|24.hour|frequency") {
            Write-Log "Note: Windows limits restore points (one per 24 hours)" -Level "WARNING"
            Write-Log "A recent restore point exists - proceeding safely" -Level "INFO"
            return $true
        }
        
        Write-Log "Restore point failed: $errorMsg" -Level "ERROR"
        
        $result = [System.Windows.Forms.MessageBox]::Show(
            "Could not create restore point.`n`nError: $errorMsg`n`nContinue anyway?",
            "Restore Point Failed",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        )
        
        return ($result -eq [System.Windows.Forms.DialogResult]::Yes)
    }
}

# ===========================================================================
# SECTION 7: REGISTRY OPERATIONS
# ===========================================================================

function Install-NVMePatch {
    Write-Log "========================================" -Level "INFO"
    Write-Log "STARTING PATCH INSTALLATION" -Level "INFO"
    Write-Log "Target Components: $($script:Config.TotalComponents)" -Level "DEBUG"
    Write-Log "========================================" -Level "INFO"
    
    Set-ButtonsEnabled -Enabled $false
    $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    
    $successCount = 0
    
    try {
        # --- PREPARATION ---
        Write-Log "Step 1/3: Creating system backup..."
        $restoreOK = New-SafeRestorePoint -Description "Pre-NVMe-Driver-Patch"
        if (-not $restoreOK) {
            Write-Log "Installation cancelled" -Level "WARNING"
            return
        }
        
        # --- COMPONENT INSTALLATION ---
        Write-Log "Step 2/3: Applying 5 registry components..."
        
        # 1. Feature Management Keys
        if (-not (Test-Path $script:Config.RegistryPath)) {
            Write-Log "Creating registry path: Overrides" -Level "INFO"
            New-Item -Path $script:Config.RegistryPath -Force | Out-Null
        }
        
        foreach ($id in $script:Config.FeatureIDs) {
            try {
                New-ItemProperty -Path $script:Config.RegistryPath -Name $id -Value 1 -PropertyType DWORD -Force | Out-Null
                
                # Verify immediately
                $verify = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
                if ($verify.$id -eq 1) {
                    Write-Log "  [OK] Feature Flag: $id" -Level "SUCCESS"
                    $successCount++
                } else {
                    Write-Log "  [FAIL] Feature Flag: $id" -Level "ERROR"
                }
            }
            catch {
                Write-Log "  [FAIL] Feature Flag $id - $($_.Exception.Message)" -Level "ERROR"
            }
        }
        
        # 2. SafeBoot Minimal
        try {
            if (-not (Test-Path $script:Config.SafeBootMinimal)) {
                New-Item -Path $script:Config.SafeBootMinimal -Force | Out-Null
            }
            Set-ItemProperty -Path $script:Config.SafeBootMinimal -Name "(Default)" -Value $script:Config.SafeBootValue -Force
            
            # Verify
            $val = Get-ItemProperty -Path $script:Config.SafeBootMinimal -Name "(Default)" -ErrorAction SilentlyContinue
            if ($val."(Default)" -eq $script:Config.SafeBootValue) {
                Write-Log "  [OK] SafeBoot Minimal Support" -Level "SUCCESS"
                $successCount++
            } else {
                Write-Log "  [FAIL] SafeBoot Minimal Support" -Level "ERROR"
            }
        }
        catch {
            Write-Log "  [FAIL] SafeBoot Minimal: $($_.Exception.Message)" -Level "ERROR"
        }
        
        # 3. SafeBoot Network
        try {
            if (-not (Test-Path $script:Config.SafeBootNetwork)) {
                New-Item -Path $script:Config.SafeBootNetwork -Force | Out-Null
            }
            Set-ItemProperty -Path $script:Config.SafeBootNetwork -Name "(Default)" -Value $script:Config.SafeBootValue -Force
            
            # Verify
            $val = Get-ItemProperty -Path $script:Config.SafeBootNetwork -Name "(Default)" -ErrorAction SilentlyContinue
            if ($val."(Default)" -eq $script:Config.SafeBootValue) {
                Write-Log "  [OK] SafeBoot Network Support" -Level "SUCCESS"
                $successCount++
            } else {
                Write-Log "  [FAIL] SafeBoot Network Support" -Level "ERROR"
            }
        }
        catch {
            Write-Log "  [FAIL] SafeBoot Network: $($_.Exception.Message)" -Level "ERROR"
        }
        
        # --- FINAL SUMMARY ---
        Write-Log "Step 3/3: Validating installation..."
        Write-Log "========================================" -Level "INFO"
        
        if ($successCount -eq $script:Config.TotalComponents) {
            Write-Log "Patch Status: SUCCESS – Applied 5/5 components" -Level "SUCCESS"
            Write-Log "Please RESTART your computer to apply changes" -Level "WARNING"
            
            $result = [System.Windows.Forms.MessageBox]::Show(
                "Patch applied successfully (5/5 components).`n`nRestart your computer now to enable the new NVMe driver?",
                "Installation Complete",
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Question
            )
            
            if ($result -eq [System.Windows.Forms.DialogResult]::Yes) {
                Write-Log "Initiating system restart..."
                Start-Process "shutdown.exe" -ArgumentList "/r /t 10 /c `"NVMe Driver Patch - Restarting in 10 seconds`""
            }
        }
        else {
            Write-Log "Patch Status: PARTIAL – Applied $successCount/$($script:Config.TotalComponents) components" -Level "WARNING"
            [System.Windows.Forms.MessageBox]::Show(
                "The patch was only partially applied ($successCount/5).`nCheck the log for details.",
                "Partial Success",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning
            )
        }
        Write-Log "========================================" -Level "INFO"
    }
    catch {
        Write-Log "INSTALLATION FAILED: $($_.Exception.Message)" -Level "ERROR"
    }
    finally {
        $script:form.Cursor = [System.Windows.Forms.Cursors]::Default
        Set-ButtonsEnabled -Enabled $true
        Refresh-StatusDisplay
    }
}

function Uninstall-NVMePatch {
    Write-Log "========================================" -Level "INFO"
    Write-Log "STARTING PATCH REMOVAL" -Level "INFO"
    Write-Log "========================================" -Level "INFO"
    
    Set-ButtonsEnabled -Enabled $false
    $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    
    $removedCount = 0
    
    try {
        Write-Log "Removing 5 registry components..."
        
        # 1. Remove Feature Flags (3 items)
        if (Test-Path $script:Config.RegistryPath) {
            foreach ($id in $script:Config.FeatureIDs) {
                $exists = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
                if ($exists) {
                    try {
                        Remove-ItemProperty -Path $script:Config.RegistryPath -Name $id -Force -ErrorAction Stop
                        Write-Log "  [REMOVED] Feature Flag: $id" -Level "SUCCESS"
                        $removedCount++
                    } catch {
                        Write-Log "  [FAIL] Failed to remove $($id): $($_.Exception.Message)" -Level "ERROR"
                    }
                } else {
                    Write-Log "  [ABSENT] Feature Flag: $id (Already gone)" -Level "INFO"
                }
            }
        } else {
            Write-Log "  [ABSENT] Feature Flags path not found" -Level "INFO"
        }
        
        # 2. Remove SafeBoot Minimal
        if (Test-Path $script:Config.SafeBootMinimal) {
            try {
                Remove-Item -Path $script:Config.SafeBootMinimal -Force -ErrorAction Stop
                Write-Log "  [REMOVED] SafeBoot Minimal" -Level "SUCCESS"
                $removedCount++
            } catch {
                Write-Log "  [FAIL] SafeBoot Minimal: $($_.Exception.Message)" -Level "ERROR"
            }
        } else {
            Write-Log "  [ABSENT] SafeBoot Minimal (Already gone)" -Level "INFO"
        }
        
        # 3. Remove SafeBoot Network
        if (Test-Path $script:Config.SafeBootNetwork) {
            try {
                Remove-Item -Path $script:Config.SafeBootNetwork -Force -ErrorAction Stop
                Write-Log "  [REMOVED] SafeBoot Network" -Level "SUCCESS"
                $removedCount++
            } catch {
                Write-Log "  [FAIL] SafeBoot Network: $($_.Exception.Message)" -Level "ERROR"
            }
        } else {
            Write-Log "  [ABSENT] SafeBoot Network (Already gone)" -Level "INFO"
        }
        
        Write-Log "========================================" -Level "INFO"
        
        # Logic for summary based on what was actually done
        if ($removedCount -eq 5) {
             Write-Log "Patch Status: REMOVED – Removed 5/5 components" -Level "SUCCESS"
        }
        elseif ($removedCount -gt 0) {
             Write-Log "Patch Status: PARTIAL – Removed $removedCount/5 components" -Level "WARNING"
        }
        else {
             # If nothing was found to remove, verify system is clean
             $finalStatus = Test-PatchStatus
             if ($finalStatus.Count -eq 0) {
                 Write-Log "Patch Status: CLEAN – No components found to remove" -Level "SUCCESS"
             } else {
                 Write-Log "Patch Status: ERROR – Removal failed or incomplete" -Level "ERROR"
             }
        }
        
        Write-Log "Please RESTART your computer" -Level "WARNING"
        Write-Log "========================================" -Level "INFO"
    }
    catch {
        Write-Log "REMOVAL FAILED: $($_.Exception.Message)" -Level "ERROR"
    }
    finally {
        $script:form.Cursor = [System.Windows.Forms.Cursors]::Default
        Set-ButtonsEnabled -Enabled $true
        Refresh-StatusDisplay
    }
}

# ===========================================================================
# SECTION 8: UI HELPER FUNCTIONS
# ===========================================================================

function Set-ButtonsEnabled {
    param([bool]$Enabled)
    
    if ($script:btnApply) { $script:btnApply.Enabled = $Enabled }
    if ($script:btnRemove) { $script:btnRemove.Enabled = $Enabled }
    if ($script:btnBackup) { $script:btnBackup.Enabled = $Enabled }
    if ($script:btnExport) { $script:btnExport.Enabled = $Enabled }
    
    [System.Windows.Forms.Application]::DoEvents()
}

function Refresh-StatusDisplay {
    $status = Test-PatchStatus
    
    # Update individual key dots
    if ($script:keyRows) {
        # Features
        foreach ($id in $script:Config.FeatureIDs) {
            if ($script:keyRows.ContainsKey($id)) {
                $isPresent = ($status.Keys -contains $id)
                Update-KeyStatusRow -Row $script:keyRows[$id] -IsApplied $isPresent
            }
        }
        
        # SafeBoot
        if ($script:keyRows.ContainsKey("SafeBootMinimal")) {
            $isPresent = ($status.Keys -contains "SafeBootMinimal")
            Update-KeyStatusRow -Row $script:keyRows["SafeBootMinimal"] -IsApplied $isPresent
        }
        if ($script:keyRows.ContainsKey("SafeBootNetwork")) {
            $isPresent = ($status.Keys -contains "SafeBootNetwork")
            Update-KeyStatusRow -Row $script:keyRows["SafeBootNetwork"] -IsApplied $isPresent
        }
    }
    
    # Update Main Status Panel & Button Colors
    if ($status.Applied) {
        # 5/5 Present
        Update-StatusIndicator -Panel $script:pnlPatchStatus -Status "OK" -Text "Patch Applied"
        if ($script:btnApply) {
            $script:btnApply.Text = "REINSTALL"
            $tag = $script:btnApply.Tag
            if ($tag -is [hashtable]) {
                $tag['Original'] = $script:Colors.SurfaceLight
                $tag['Hover'] = $script:Colors.SurfaceHover
            }
            $script:btnApply.BackColor = $script:Colors.SurfaceLight
            
            $tagRemove = $script:btnRemove.Tag
            if ($tagRemove -is [hashtable]) {
                $tagRemove['Original'] = $script:Colors.Danger
                $tagRemove['Hover'] = $script:Colors.DangerHover
            }
            $script:btnRemove.BackColor = $script:Colors.Danger
        }
    }
    elseif ($status.Partial) {
        # 1 to 4 Present
        Update-StatusIndicator -Panel $script:pnlPatchStatus -Status "Warning" -Text "Partial ($($status.Count)/5)"
    }
    else {
        # 0 Present
        Update-StatusIndicator -Panel $script:pnlPatchStatus -Status "Neutral" -Text "Not Applied"
        if ($script:btnApply) {
            $script:btnApply.Text = "APPLY PATCH"
            $tag = $script:btnApply.Tag
            if ($tag -is [hashtable]) {
                $tag['Original'] = $script:Colors.Success
                $tag['Hover'] = $script:Colors.SuccessHover
            }
            $script:btnApply.BackColor = $script:Colors.Success
            
            $tagRemove = $script:btnRemove.Tag
            if ($tagRemove -is [hashtable]) {
                $tagRemove['Original'] = $script:Colors.SurfaceLight
                $tagRemove['Hover'] = $script:Colors.SurfaceHover
            }
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
    
    if ($CheckNVMe -and -not $script:HasNVMeDrives) {
        $noNVMeResult = [System.Windows.Forms.MessageBox]::Show(
            "NO NVMe DRIVES DETECTED ON THIS SYSTEM!`n`nThis patch only affects NVMe drives using the Windows inbox driver.`nYour system appears to have no NVMe drives.`n`nDo you still want to continue?",
            "No NVMe Detected",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        )
        
        if ($noNVMeResult -ne [System.Windows.Forms.DialogResult]::Yes) {
            Write-Log "Operation cancelled - No NVMe drives detected" -Level "WARNING"
            return $false
        }
    }
    
    $fullMessage = $Message
    if ($WarningText) {
        $fullMessage += "`n`nWARNING: $WarningText"
    }
    
    $result = [System.Windows.Forms.MessageBox]::Show(
        $fullMessage,
        $Title,
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question
    )
    
    return ($result -eq [System.Windows.Forms.DialogResult]::Yes)
}

# ===========================================================================
# SECTION 9: MAIN FORM CONSTRUCTION
# ===========================================================================

# Create tooltip provider
$script:ToolTipProvider = New-Object System.Windows.Forms.ToolTip
$script:ToolTipProvider.AutoPopDelay = 10000
$script:ToolTipProvider.InitialDelay = 500
$script:ToolTipProvider.ReshowDelay = 200

# Main Form - WIDE LAYOUT REDESIGN
$script:form = New-Object System.Windows.Forms.Form
$script:form.Text = "$($script:Config.AppName) v$($script:Config.AppVersion)"
$script:form.Size = New-Object System.Drawing.Size(1125, 760) # Increased width significantly
$script:form.StartPosition = "CenterScreen"
$script:form.FormBorderStyle = "FixedSingle"
$script:form.MaximizeBox = $false
$script:form.BackColor = $script:Colors.Background
$script:form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

$script:form.GetType().GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic).SetValue($script:form, $true, $null)

# ============ HEADER SECTION ============
$pnlHeader = New-Object System.Windows.Forms.Panel
$pnlHeader.Location = New-Object System.Drawing.Point(0, 0)
$pnlHeader.Size = New-Object System.Drawing.Size(1125, 101) # Full width
$pnlHeader.BackColor = $script:Colors.Surface

$pnlHeader.GetType().GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic).SetValue($pnlHeader, $true, $null)

$lblIcon = New-Object System.Windows.Forms.Label
$lblIcon.Text = "NVMe"
$lblIcon.Location = New-Object System.Drawing.Point(24, 24)
$lblIcon.Size = New-Object System.Drawing.Size(60, 52)
$lblIcon.ForeColor = $script:Colors.Accent
$lblIcon.Font = New-Object System.Drawing.Font("Segoe UI Black", 11)
$lblIcon.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$lblIcon.BackColor = $script:Colors.SurfaceLight
Set-RoundedCorners -Control $lblIcon -Radius 8
$pnlHeader.Controls.Add($lblIcon)

$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = "NVMe Driver Patcher"
$lblTitle.Location = New-Object System.Drawing.Point(96, 22)
$lblTitle.Size = New-Object System.Drawing.Size(400, 32)
$lblTitle.ForeColor = $script:Colors.TextPrimary
$lblTitle.Font = New-Object System.Drawing.Font("Segoe UI Light", 20)
$pnlHeader.Controls.Add($lblTitle)

$lblSubtitle = New-Object System.Windows.Forms.Label
$lblSubtitle.Text = "Enable experimental Server 2025 NVMe driver for Windows 11"
$lblSubtitle.Location = New-Object System.Drawing.Point(97, 56)
$lblSubtitle.Size = New-Object System.Drawing.Size(450, 20)
$lblSubtitle.ForeColor = $script:Colors.TextMuted
$lblSubtitle.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$pnlHeader.Controls.Add($lblSubtitle)

$pnlHeader.Add_Paint({
    param($sender, $e)
    $pen = $null
    try {
        $pen = New-Object System.Drawing.Pen($script:Colors.SurfaceLight, 1)
        $e.Graphics.DrawLine($pen, 0, ($sender.Height - 1), $sender.Width, ($sender.Height - 1))
    }
    finally {
        if ($pen) { $pen.Dispose() }
    }
})

$script:form.Controls.Add($pnlHeader)

# ============ LEFT COLUMN (System Status, Drives, Keys) ============

# 1. STATUS CARD
$cardStatus = New-CardPanel -Location (New-Object System.Drawing.Point(20, 120)) -Size (New-Object System.Drawing.Size(524, 96)) -Title "System Status"

$script:pnlPatchStatus = New-StatusIndicator -Location (New-Object System.Drawing.Point(16, 36)) -Size (New-Object System.Drawing.Size(240, 50)) -Label "Driver Patch"
$cardStatus.Controls.Add($script:pnlPatchStatus)

$script:pnlWinStatus = New-StatusIndicator -Location (New-Object System.Drawing.Point(270, 36)) -Size (New-Object System.Drawing.Size(240, 50)) -Label "Windows Version"
$cardStatus.Controls.Add($script:pnlWinStatus)

$script:form.Controls.Add($cardStatus)

# 2. DETECTED DRIVES CARD
$script:cardDrives = New-CardPanel -Location (New-Object System.Drawing.Point(20, 230)) -Size (New-Object System.Drawing.Size(524, 180)) -Title "Detected Drives"

$script:lblDrivesStatus = New-Object System.Windows.Forms.Label
$script:lblDrivesStatus.Location = New-Object System.Drawing.Point(16, 40)
$script:lblDrivesStatus.Size = New-Object System.Drawing.Size(492, 20)
$script:lblDrivesStatus.ForeColor = $script:Colors.TextMuted
$script:lblDrivesStatus.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$script:lblDrivesStatus.Text = "Scanning drives..."
$script:cardDrives.Controls.Add($script:lblDrivesStatus)

# Scrollable Drive List Container
$script:pnlDrivesList = New-Object System.Windows.Forms.Panel
$script:pnlDrivesList.Location = New-Object System.Drawing.Point(16, 36)
$script:pnlDrivesList.Size = New-Object System.Drawing.Size(492, 136) # Adjusted height
$script:pnlDrivesList.BackColor = [System.Drawing.Color]::Transparent
$script:pnlDrivesList.AutoScroll = $true
$script:pnlDrivesList.Visible = $false
$script:cardDrives.Controls.Add($script:pnlDrivesList)

$script:form.Controls.Add($script:cardDrives)

# 3. REGISTRY KEYS STATUS CARD
$cardKeys = New-CardPanel -Location (New-Object System.Drawing.Point(20, 424)) -Size (New-Object System.Drawing.Size(524, 164)) -Title "Registry Keys Status"

$script:keyRows = @{}

$script:keyRows["735209102"] = New-KeyStatusRow -Location (New-Object System.Drawing.Point(16, 36)) -KeyName "735209102" -Description "NVMe Feature Flag 1 - Primary driver enable"
$cardKeys.Controls.Add($script:keyRows["735209102"])

$script:keyRows["1853569164"] = New-KeyStatusRow -Location (New-Object System.Drawing.Point(16, 60)) -KeyName "1853569164" -Description "NVMe Feature Flag 2 - Extended functionality"
$cardKeys.Controls.Add($script:keyRows["1853569164"])

$script:keyRows["156965516"] = New-KeyStatusRow -Location (New-Object System.Drawing.Point(16, 84)) -KeyName "156965516" -Description "NVMe Feature Flag 3 - Performance optimizations"
$cardKeys.Controls.Add($script:keyRows["156965516"])

$script:keyRows["SafeBootMinimal"] = New-KeyStatusRow -Location (New-Object System.Drawing.Point(16, 114)) -KeyName "SafeBoot" -Description "Safe Mode support (Minimal) - Prevents boot issues"
$cardKeys.Controls.Add($script:keyRows["SafeBootMinimal"])

$script:keyRows["SafeBootNetwork"] = New-KeyStatusRow -Location (New-Object System.Drawing.Point(16, 138)) -KeyName "SafeBoot/Net" -Description "Safe Mode with Networking support"
$cardKeys.Controls.Add($script:keyRows["SafeBootNetwork"])

$script:form.Controls.Add($cardKeys)

# ============ RIGHT COLUMN (Warning, Actions, Log) ============

# 1. WARNING CARD (Enhanced)
$cardWarning = New-Object System.Windows.Forms.Panel
$cardWarning.Location = New-Object System.Drawing.Point(564, 120)
$cardWarning.Size = New-Object System.Drawing.Size(524, 56)
$cardWarning.BackColor = $script:Colors.WarningDim
Set-ControlTagData -Control $cardWarning -NewData @{ CornerRadius = 8 }
Set-RoundedCorners -Control $cardWarning -Radius 8

# Warning Accent Bar
$warningAccent = New-Object System.Windows.Forms.Panel
$warningAccent.Location = New-Object System.Drawing.Point(0, 0)
$warningAccent.Size = New-Object System.Drawing.Size(6, 56)
$warningAccent.BackColor = $script:Colors.Warning
$cardWarning.Controls.Add($warningAccent)

$lblWarningIcon = New-Object System.Windows.Forms.Label
$lblWarningIcon.Text = "!"
$lblWarningIcon.Location = New-Object System.Drawing.Point(22, 12)
$lblWarningIcon.Size = New-Object System.Drawing.Size(32, 32)
$lblWarningIcon.ForeColor = $script:Colors.Warning
$lblWarningIcon.Font = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
$lblWarningIcon.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$lblWarningIcon.BackColor = [System.Drawing.Color]::Transparent
Set-RoundedCorners -Control $lblWarningIcon -Radius 16
$cardWarning.Controls.Add($lblWarningIcon)

$lblWarningText = New-Object System.Windows.Forms.Label
$lblWarningText.Text = "This tool modifies system registry. A restore point will be created automatically before any changes are made."
$lblWarningText.Location = New-Object System.Drawing.Point(64, 12)
$lblWarningText.Size = New-Object System.Drawing.Size(440, 32)
$lblWarningText.ForeColor = $script:Colors.TextSecondary
$lblWarningText.Font = New-Object System.Drawing.Font("Segoe UI", 8.5)
$lblWarningText.BackColor = [System.Drawing.Color]::Transparent
$cardWarning.Controls.Add($lblWarningText)

$script:form.Controls.Add($cardWarning)

# 2. ACTIONS CARD (Improved Grouping)
$cardActions = New-CardPanel -Location (New-Object System.Drawing.Point(564, 190)) -Size (New-Object System.Drawing.Size(524, 150)) -Title "Actions"

# Primary Operations Group
$script:btnApply = New-ModernButton `
    -Text "APPLY PATCH" `
    -Location (New-Object System.Drawing.Point(16, 40)) `
    -Size (New-Object System.Drawing.Size(240, 44)) `
    -BackColor $script:Colors.Success `
    -HoverColor $script:Colors.SuccessHover `
    -ToolTip "Apply NVMe driver enhancement patch" `
    -IsPrimary $true `
    -CornerRadius 6 `
    -OnClick {
        if (Show-ConfirmDialog -Title "Apply Patch" -Message "Apply the NVMe driver enhancement patch?" -WarningText "This will modify system registry settings." -CheckNVMe $true) {
            Install-NVMePatch
        }
    }
$cardActions.Controls.Add($script:btnApply)

$script:btnRemove = New-ModernButton `
    -Text "REMOVE PATCH" `
    -Location (New-Object System.Drawing.Point(268, 40)) `
    -Size (New-Object System.Drawing.Size(240, 44)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Remove all NVMe patch registry entries" `
    -IsPrimary $false `
    -CornerRadius 6 `
    -OnClick {
        if (Show-ConfirmDialog -Title "Remove Patch" -Message "Remove the NVMe driver patch?" -WarningText "This will revert to default Windows behavior." -CheckNVMe $true) {
            Uninstall-NVMePatch
        }
    }
$cardActions.Controls.Add($script:btnRemove)

# Safety Operations Group
$script:btnBackup = New-ModernButton `
    -Text "CREATE RESTORE POINT" `
    -Location (New-Object System.Drawing.Point(16, 96)) `
    -Size (New-Object System.Drawing.Size(492, 36)) `
    -BackColor $script:Colors.AccentDark `
    -HoverColor $script:Colors.Accent `
    -ToolTip "Create a manual system restore point" `
    -IsPrimary $false `
    -CornerRadius 6 `
    -OnClick {
        Set-ButtonsEnabled -Enabled $false
        $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        New-SafeRestorePoint -Description "Manual NVMe Backup $(Get-Date -Format 'yyyy-MM-dd')"
        $script:form.Cursor = [System.Windows.Forms.Cursors]::Default
        Set-ButtonsEnabled -Enabled $true
    }
$cardActions.Controls.Add($script:btnBackup)

$script:form.Controls.Add($cardActions)

# 3. LOG CARD
$cardLog = New-CardPanel -Location (New-Object System.Drawing.Point(564, 354)) -Size (New-Object System.Drawing.Size(524, 234)) -Title "Activity Log"

$script:btnExport = New-ModernButton `
    -Text "Export" `
    -Location (New-Object System.Drawing.Point(450, 6)) `
    -Size (New-Object System.Drawing.Size(58, 24)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Export log to file" `
    -CornerRadius 4 `
    -OnClick { Export-LogFile }
$cardLog.Controls.Add($script:btnExport)

$script:rtbOutput = New-Object System.Windows.Forms.RichTextBox
$script:rtbOutput.Location = New-Object System.Drawing.Point(16, 36)
$script:rtbOutput.Size = New-Object System.Drawing.Size(492, 182)
$script:rtbOutput.ReadOnly = $true
$script:rtbOutput.BackColor = $script:Colors.Surface
$script:rtbOutput.ForeColor = $script:Colors.TextSecondary
$script:rtbOutput.BorderStyle = "None"
$script:rtbOutput.Font = New-Object System.Drawing.Font("Cascadia Code, Consolas, Courier New", 9)
$script:rtbOutput.ScrollBars = "Vertical"
Set-ControlTagData -Control $script:rtbOutput -NewData @{ CornerRadius = 6 }
Set-RoundedCorners -Control $script:rtbOutput -Radius 6
$cardLog.Controls.Add($script:rtbOutput)

$script:form.Controls.Add($cardLog)

# ============ FOOTER ============
$lblFooter = New-Object System.Windows.Forms.Label
$lblFooter.Text = "v$($script:Config.AppVersion)  |  Registry: ...\FeatureManagement\Overrides"
$lblFooter.Location = New-Object System.Drawing.Point(20, 680)
$lblFooter.Size = New-Object System.Drawing.Size(1080, 16)
$lblFooter.ForeColor = $script:Colors.TextDimmed
$lblFooter.Font = New-Object System.Drawing.Font("Segoe UI", 7.5)
$lblFooter.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$script:form.Controls.Add($lblFooter)

# ===========================================================================
# SECTION 10: FORM EVENT HANDLERS & STARTUP
# ===========================================================================

$script:form.Add_Load({
    Write-Log "$($script:Config.AppName) v$($script:Config.AppVersion) started"
    Write-Log "Theme detected: $currentTheme"
    Write-Log "Working directory: $($script:Config.WorkingDir)"
    Write-Log "----------------------------------------"
    
    # Scan and display drives using MSFT_Disk/Win32_DiskDrive pipeline
    Write-Log "Scanning system drives (Storage/CIM)..."
    $drives = Get-SystemDrives
    $nvmeCount = 0
    
    if ($drives.Count -eq 0) {
        $script:lblDrivesStatus.Text = "No physical drives detected"
        $script:lblDrivesStatus.ForeColor = $script:Colors.Warning
        Write-Log "No physical drives detected" -Level "WARNING"
    }
    else {
        $script:lblDrivesStatus.Visible = $false
        $script:pnlDrivesList.Visible = $true
        
        # Suspend layout for better performance during population
        $script:pnlDrivesList.SuspendLayout()
        
        $yOffset = 0
        
        foreach ($drive in $drives) {
            # Add new row
            $driveRow = New-DriveStatusRow `
                -Location (New-Object System.Drawing.Point(0, $yOffset)) `
                -DriveObject $drive
            
            $script:pnlDrivesList.Controls.Add($driveRow)
            $yOffset += 24 # 24px height per row
            
            # Log discovery
            if ($drive.IsNVMe) {
                $nvmeCount++
                Write-Log "  [NVMe] Disk $($drive.Number): $($drive.Name)" -Level "SUCCESS"
            }
            else {
                Write-Log "  [$($drive.BusType)] Disk $($drive.Number): $($drive.Name)" -Level "INFO"
            }
        }
        
        $script:pnlDrivesList.ResumeLayout()
    }
    
    # Set global NVMe presence flag
    $script:HasNVMeDrives = ($nvmeCount -gt 0)
    
    if ($nvmeCount -eq 0) {
        Write-Log "WARNING: No NVMe drives detected on this system!" -Level "WARNING"
    }
    else {
        Write-Log "$nvmeCount NVMe drive(s) detected" -Level "SUCCESS"
    }
    
    Write-Log "----------------------------------------"
    
    # Check Windows compatibility
    Update-StatusIndicator -Panel $script:pnlWinStatus -Status "Checking" -Text "Checking..."
    if (Test-WindowsCompatibility) {
        Update-StatusIndicator -Panel $script:pnlWinStatus -Status "OK" -Text "Windows 11 OK"
    }
    else {
        Update-StatusIndicator -Panel $script:pnlWinStatus -Status "Warning" -Text "Compatibility Unknown"
    }
    
    # Check patch status
    Update-StatusIndicator -Panel $script:pnlPatchStatus -Status "Checking" -Text "Checking..."
    Refresh-StatusDisplay
    
    Write-Log "----------------------------------------"
    Write-Log "Ready. Select an action above."
})

$script:form.Add_FormClosing({
    if ($script:ToolTipProvider) {
        $script:ToolTipProvider.Dispose()
    }
})

# ===========================================================================
# SECTION 11: RUN APPLICATION
# ===========================================================================

[void]$script:form.ShowDialog()

# Cleanup form resources
$script:form.Dispose()
