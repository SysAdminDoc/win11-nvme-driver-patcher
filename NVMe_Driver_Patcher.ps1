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
    - Registry backup before modifications
    - System registry (HKLM) backup option
    
    REGISTRY KEYS MODIFIED:
    HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides
    - 735209102  (NVMe Feature Flag 1)
    - 1853569164 (NVMe Feature Flag 2)
    - 156965516  (NVMe Feature Flag 3)
    
    Safe Mode Support (prevents boot issues):
    HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}
    HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}

.NOTES
    Version: 2.2.0
    Author:  System Administrator
    Requires: Windows 11, Administrator privileges
    
.LINK
    https://learn.microsoft.com/en-us/windows-server/storage/

#>

#Requires -Version 5.1

# ===========================================================================
# SECTION 1: INITIALIZATION & PRIVILEGE ELEVATION
# ===========================================================================

# Error handling preference (strict mode removed - causes issues with WinForms property access)
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
    AppVersion     = "2.2.0"
    RegistryPath   = "HKLM:\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides"
    FeatureIDs     = @("735209102", "1853569164", "156965516")
    SafeBootMinimal = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"
    SafeBootNetwork = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}"
    SafeBootValue   = "Storage Disks"
    MinWinBuild    = 22000  # Windows 11 minimum build
    LogHistory     = [System.Collections.ArrayList]::new()
    WorkingDir     = $null  # Set at startup
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

# Modern Windows 11 Dark Mode Color Palette
$script:Colors = @{
    Background      = [System.Drawing.Color]::FromArgb(32, 32, 32)
    Surface         = [System.Drawing.Color]::FromArgb(40, 40, 40)
    SurfaceLight    = [System.Drawing.Color]::FromArgb(50, 50, 50)
    SurfaceHover    = [System.Drawing.Color]::FromArgb(55, 55, 55)
    CardBackground  = [System.Drawing.Color]::FromArgb(44, 44, 44)
    CardBorder      = [System.Drawing.Color]::FromArgb(60, 60, 60)
    Border          = [System.Drawing.Color]::FromArgb(70, 70, 70)
    TextPrimary     = [System.Drawing.Color]::FromArgb(255, 255, 255)
    TextSecondary   = [System.Drawing.Color]::FromArgb(200, 200, 200)
    TextMuted       = [System.Drawing.Color]::FromArgb(140, 140, 140)
    TextDimmed      = [System.Drawing.Color]::FromArgb(100, 100, 100)
    Accent          = [System.Drawing.Color]::FromArgb(96, 205, 255)
    AccentDark      = [System.Drawing.Color]::FromArgb(0, 120, 212)
    AccentHover     = [System.Drawing.Color]::FromArgb(116, 225, 255)
    Success         = [System.Drawing.Color]::FromArgb(108, 203, 95)
    SuccessHover    = [System.Drawing.Color]::FromArgb(128, 223, 115)
    SuccessDim      = [System.Drawing.Color]::FromArgb(45, 80, 40)
    Warning         = [System.Drawing.Color]::FromArgb(252, 185, 65)
    WarningHover    = [System.Drawing.Color]::FromArgb(255, 205, 85)
    WarningDim      = [System.Drawing.Color]::FromArgb(80, 65, 30)
    Danger          = [System.Drawing.Color]::FromArgb(243, 80, 80)
    DangerHover     = [System.Drawing.Color]::FromArgb(255, 100, 100)
    DangerDim       = [System.Drawing.Color]::FromArgb(80, 40, 40)
    Info            = [System.Drawing.Color]::FromArgb(96, 165, 250)
}

# ===========================================================================
# SECTION 3: CUSTOM UI COMPONENTS
# ===========================================================================

# Helper function to create rounded region (DPI-safe, with proper GDI cleanup)
# Returns Region object; caller is responsible for disposing old region if replacing
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
        
        # Create region from path - region copies the path data
        $region = New-Object System.Drawing.Region($path)
    }
    finally {
        # Always dispose the path - region has its own copy of the data
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
        # Merge new data into existing hashtable
        foreach ($key in $NewData.Keys) {
            $existingTag[$key] = $NewData[$key]
        }
    }
    else {
        # Existing tag is not a hashtable - create new with _OriginalTag preserved
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

# Apply rounded corners with resize handler for DPI safety (no leaks, no stacking)
function Set-RoundedCorners {
    param(
        [System.Windows.Forms.Control]$Control,
        [int]$Radius = 8
    )
    
    # Merge corner radius into existing Tag data
    Set-ControlTagData -Control $Control -NewData @{ 
        CornerRadius = $Radius 
        _ResizeHandlerAttached = $true
    }
    
    # Apply initial region
    $region = Get-RoundedRegion -Width $Control.Width -Height $Control.Height -Radius $Radius
    if ($region) {
        $oldRegion = $Control.Region
        $Control.Region = $region
        if ($oldRegion) {
            $oldRegion.Dispose()
        }
    }
    
    # Only attach resize handler if not already attached
    $alreadyAttached = Get-ControlTagValue -Control $Control -Key '_ResizeHandlerAttached' -Default $false
    if (-not $alreadyAttached) {
        Set-ControlTagData -Control $Control -NewData @{ _ResizeHandlerAttached = $true }
    }
    
    # Attach resize handler (PowerShell doesn't easily allow checking existing handlers)
    # Use a flag in Tag to track, but handler will be idempotent
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

# Create a modern card panel with optional title and border
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
    
    # Enable double buffering to reduce flicker
    $card.GetType().GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic).SetValue($card, $true, $null)
    
    # Set Tag data (will be merged by Set-RoundedCorners)
    Set-ControlTagData -Control $card -NewData @{ 
        CornerRadius = $CornerRadius
        Title = $Title 
    }
    
    # Apply rounded region with proper cleanup
    $region = Get-RoundedRegion -Width $Size.Width -Height $Size.Height -Radius $CornerRadius
    if ($region) {
        $card.Region = $region
    }
    
    # Handle resize for DPI safety with proper GDI cleanup
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
    
    # Draw border via Paint event with proper GDI disposal
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
    
    # Add title label if provided
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

# Modern flat button with hover effects and rounded corners
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
    
    # Set all Tag data at once to avoid overwriting
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
    
    # Apply rounded region with proper cleanup
    $region = Get-RoundedRegion -Width $Size.Width -Height $Size.Height -Radius $CornerRadius
    if ($region) {
        $button.Region = $region
    }
    
    # Handle resize for DPI safety with proper GDI cleanup
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

# Enhanced status indicator with icon (text stays neutral, icon gets color)
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
    
    # Label text (category) - positioned at top with positive margin
    $labelCtrl = New-Object System.Windows.Forms.Label
    $labelCtrl.Location = New-Object System.Drawing.Point(0, 2)
    $labelCtrl.Size = New-Object System.Drawing.Size($Size.Width, 16)
    $labelCtrl.ForeColor = $script:Colors.TextDimmed
    $labelCtrl.Font = New-Object System.Drawing.Font("Segoe UI", 7.5)
    $labelCtrl.Text = $Label.ToUpper()
    Set-ControlTagData -Control $labelCtrl -NewData @{ Role = "title" }
    
    # Status icon circle - positioned to align with status text
    $iconPanel = New-Object System.Windows.Forms.Panel
    $iconPanel.Size = New-Object System.Drawing.Size(10, 10)
    $iconPanel.Location = New-Object System.Drawing.Point(0, 26)
    $iconPanel.BackColor = $script:Colors.TextMuted
    Set-ControlTagData -Control $iconPanel -NewData @{ Role = "icon" }
    
    # Make it circular with proper GDI cleanup
    $iconPath = $null
    try {
        $iconPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $iconPath.AddEllipse(0, 0, 10, 10)
        $iconPanel.Region = New-Object System.Drawing.Region($iconPath)
    }
    finally {
        if ($iconPath) { $iconPath.Dispose() }
    }
    
    # Status text (value) - positioned below title with clear separation
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
    
    # Store in history
    [void]$script:Config.LogHistory.Add($logEntry)
    
    # Append to RichTextBox with color coding
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
    Write-Log "Checking current patch status..."
    
    $result = [PSCustomObject]@{
        Applied = $false
        Partial = $false
        Keys    = @()
        Total   = $script:Config.FeatureIDs.Count
    }
    
    if (-not (Test-Path $script:Config.RegistryPath)) {
        Write-Log "Registry path does not exist - Patch NOT applied" -Level "INFO"
        return $result
    }
    
    $appliedKeys = [System.Collections.ArrayList]::new()
    foreach ($id in $script:Config.FeatureIDs) {
        try {
            $value = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
            if ($null -ne $value) {
                $propValue = $value | Select-Object -ExpandProperty $id -ErrorAction SilentlyContinue
                if ($propValue -eq 1) {
                    [void]$appliedKeys.Add($id)
                }
            }
        }
        catch {
            # Key doesn't exist, continue
        }
    }
    
    $result.Keys = $appliedKeys.ToArray()
    $result.Applied = ($appliedKeys.Count -eq $script:Config.FeatureIDs.Count)
    $result.Partial = ($appliedKeys.Count -gt 0 -and $appliedKeys.Count -lt $script:Config.FeatureIDs.Count)
    
    if ($result.Applied) {
        Write-Log "Patch status: APPLIED ($($appliedKeys.Count)/$($result.Total) keys)" -Level "SUCCESS"
    }
    elseif ($result.Partial) {
        Write-Log "Patch status: PARTIAL ($($appliedKeys.Count)/$($result.Total) keys)" -Level "WARNING"
    }
    else {
        Write-Log "Patch status: NOT APPLIED" -Level "INFO"
    }
    
    return $result
}

# Status indicator update - icon gets color, text stays neutral
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
    
    # Only icon gets the status color
    if ($icon) { $icon.BackColor = $color }
    
    # Text stays neutral for readability
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
    
    # Ensure protection is enabled first
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
        # Attempt to create restore point
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

function Backup-RegistryPath {
    param([string]$BackupName = "NVMe_RegBackup")
    
    $backupDir = $script:Config.WorkingDir
    $backupFile = Join-Path $backupDir "$BackupName`_$(Get-Date -Format 'yyyyMMdd_HHmmss').reg"
    
    try {
        if (-not (Test-Path $backupDir)) {
            New-Item -Path $backupDir -ItemType Directory -Force | Out-Null
        }
        
        if (Test-Path $script:Config.RegistryPath) {
            $regExportPath = $script:Config.RegistryPath -replace "HKLM:\\", "HKEY_LOCAL_MACHINE\"
            $process = Start-Process -FilePath "reg.exe" -ArgumentList "export `"$regExportPath`" `"$backupFile`" /y" -Wait -PassThru -NoNewWindow
            
            if ($process.ExitCode -eq 0 -and (Test-Path $backupFile)) {
                Write-Log "Registry backup saved: $backupFile" -Level "SUCCESS"
                return $backupFile
            }
        }
        else {
            Write-Log "No existing registry path to backup" -Level "INFO"
            return $null
        }
    }
    catch {
        Write-Log "Registry backup failed: $($_.Exception.Message)" -Level "WARNING"
    }
    
    return $null
}

# System Registry (HKLM) Backup
function Backup-SystemRegistry {
    Write-Log "========================================" -Level "INFO"
    Write-Log "STARTING SYSTEM REGISTRY (HKLM) BACKUP" -Level "INFO"
    Write-Log "========================================" -Level "INFO"
    
    Set-ButtonsEnabled -Enabled $false
    $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    
    try {
        $backupDir = $script:Config.WorkingDir
        
        if (-not (Test-Path $backupDir)) {
            Write-Log "Creating backup folder: $backupDir"
            New-Item -Path $backupDir -ItemType Directory -Force | Out-Null
            Write-Log "Backup folder created" -Level "SUCCESS"
        }
        
        $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
        $backupFile = Join-Path $backupDir "SystemRegistry_HKLM_$timestamp.reg"
        
        Write-Log "Exporting HKLM registry to: $backupFile"
        Write-Log "This may take several minutes..." -Level "WARNING"
        
        [System.Windows.Forms.Application]::DoEvents()
        
        # Export HKLM (system registry hive)
        $process = Start-Process -FilePath "reg.exe" -ArgumentList "export HKLM `"$backupFile`" /y" -Wait -PassThru -NoNewWindow
        
        if ($process.ExitCode -eq 0 -and (Test-Path $backupFile)) {
            $fileInfo = Get-Item $backupFile
            $fileSizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
            
            Write-Log "========================================" -Level "INFO"
            Write-Log "SYSTEM REGISTRY BACKUP COMPLETE" -Level "SUCCESS"
            Write-Log "Location: $backupFile" -Level "INFO"
            Write-Log "Size: $fileSizeMB MB" -Level "INFO"
            Write-Log "========================================" -Level "INFO"
            
            [System.Windows.Forms.MessageBox]::Show(
                "System registry (HKLM) backup completed successfully!`n`nLocation: $backupFile`nSize: $fileSizeMB MB",
                "Backup Complete",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Information
            )
        }
        else {
            Write-Log "Registry export failed with exit code: $($process.ExitCode)" -Level "ERROR"
            [System.Windows.Forms.MessageBox]::Show(
                "Failed to create registry backup.`n`nPlease check the activity log for details.",
                "Backup Failed",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Error
            )
        }
    }
    catch {
        Write-Log "BACKUP FAILED: $($_.Exception.Message)" -Level "ERROR"
        [System.Windows.Forms.MessageBox]::Show(
            "An error occurred during backup:`n`n$($_.Exception.Message)",
            "Backup Error",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        )
    }
    finally {
        $script:form.Cursor = [System.Windows.Forms.Cursors]::Default
        Set-ButtonsEnabled -Enabled $true
    }
}

function Install-NVMePatch {
    Write-Log "========================================" -Level "INFO"
    Write-Log "STARTING PATCH INSTALLATION" -Level "INFO"
    Write-Log "========================================" -Level "INFO"
    
    # Disable buttons during operation
    Set-ButtonsEnabled -Enabled $false
    $script:form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    
    try {
        # Step 1: Create restore point
        Write-Log "Step 1/5: Creating system backup..."
        $restoreOK = New-SafeRestorePoint -Description "Pre-NVMe-Driver-Patch"
        if (-not $restoreOK) {
            Write-Log "Installation cancelled" -Level "WARNING"
            return
        }
        
        # Step 2: Backup existing registry
        Write-Log "Step 2/5: Backing up registry..."
        Backup-RegistryPath -BackupName "Pre_Install"
        
        # Step 3: Create registry path if needed
        Write-Log "Step 3/5: Preparing registry..."
        if (-not (Test-Path $script:Config.RegistryPath)) {
            Write-Log "Creating registry path..."
            New-Item -Path $script:Config.RegistryPath -Force | Out-Null
            Write-Log "Registry path created" -Level "SUCCESS"
        }
        
        # Step 4: Apply feature flags
        Write-Log "Step 4/5: Applying feature flags..."
        $successCount = 0
        foreach ($id in $script:Config.FeatureIDs) {
            try {
                New-ItemProperty -Path $script:Config.RegistryPath -Name $id -Value 1 -PropertyType DWORD -Force | Out-Null
                Write-Log "  Applied: $id = 1" -Level "SUCCESS"
                $successCount++
            }
            catch {
                Write-Log "  Failed: $id - $($_.Exception.Message)" -Level "ERROR"
            }
        }
        
        # Step 5: Add Safe Mode support keys (prevents boot issues in Safe Mode)
        Write-Log "Step 5/5: Configuring Safe Mode support..."
        $safeBootSuccess = $true
        
        # SafeBoot\Minimal key (Safe Mode)
        try {
            if (-not (Test-Path $script:Config.SafeBootMinimal)) {
                New-Item -Path $script:Config.SafeBootMinimal -Force | Out-Null
            }
            Set-ItemProperty -Path $script:Config.SafeBootMinimal -Name "(Default)" -Value $script:Config.SafeBootValue -Force
            Write-Log "  SafeBoot\Minimal configured" -Level "SUCCESS"
        }
        catch {
            Write-Log "  Failed to configure SafeBoot\Minimal: $($_.Exception.Message)" -Level "ERROR"
            $safeBootSuccess = $false
        }
        
        # SafeBoot\Network key (Safe Mode with Networking)
        try {
            if (-not (Test-Path $script:Config.SafeBootNetwork)) {
                New-Item -Path $script:Config.SafeBootNetwork -Force | Out-Null
            }
            Set-ItemProperty -Path $script:Config.SafeBootNetwork -Name "(Default)" -Value $script:Config.SafeBootValue -Force
            Write-Log "  SafeBoot\Network configured" -Level "SUCCESS"
        }
        catch {
            Write-Log "  Failed to configure SafeBoot\Network: $($_.Exception.Message)" -Level "ERROR"
            $safeBootSuccess = $false
        }
        
        Write-Log "========================================" -Level "INFO"
        if ($successCount -eq $script:Config.FeatureIDs.Count -and $safeBootSuccess) {
            Write-Log "INSTALLATION COMPLETE" -Level "SUCCESS"
            Write-Log "Please RESTART your computer to apply changes" -Level "WARNING"
            
            $result = [System.Windows.Forms.MessageBox]::Show(
                "Patch applied successfully!`n`nRestart your computer now to enable the new NVMe driver?",
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
            Write-Log "PARTIAL INSTALLATION (some components may have failed)" -Level "WARNING"
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
    
    try {
        # Backup before removal
        Write-Log "Backing up current registry state..."
        Backup-RegistryPath -BackupName "Pre_Uninstall"
        
        # Remove feature flags
        Write-Log "Removing feature flags..."
        $removedCount = 0
        
        if (Test-Path $script:Config.RegistryPath) {
            foreach ($id in $script:Config.FeatureIDs) {
                try {
                    $prop = Get-ItemProperty -Path $script:Config.RegistryPath -Name $id -ErrorAction SilentlyContinue
                    if ($null -ne $prop) {
                        Remove-ItemProperty -Path $script:Config.RegistryPath -Name $id -Force -ErrorAction Stop
                        Write-Log "  Removed: $id" -Level "SUCCESS"
                        $removedCount++
                    }
                    else {
                        Write-Log "  Skipped: $id (not present)" -Level "INFO"
                    }
                }
                catch {
                    Write-Log "  Failed to remove: $id - $($_.Exception.Message)" -Level "ERROR"
                }
            }
        }
        else {
            Write-Log "Feature flags registry path does not exist" -Level "INFO"
        }
        
        # Remove Safe Mode keys
        Write-Log "Removing Safe Mode configuration..."
        
        # Remove SafeBoot\Minimal key
        if (Test-Path $script:Config.SafeBootMinimal) {
            try {
                Remove-Item -Path $script:Config.SafeBootMinimal -Force -ErrorAction Stop
                Write-Log "  Removed: SafeBoot\Minimal" -Level "SUCCESS"
            }
            catch {
                Write-Log "  Failed to remove SafeBoot\Minimal: $($_.Exception.Message)" -Level "WARNING"
            }
        }
        else {
            Write-Log "  Skipped: SafeBoot\Minimal (not present)" -Level "INFO"
        }
        
        # Remove SafeBoot\Network key
        if (Test-Path $script:Config.SafeBootNetwork) {
            try {
                Remove-Item -Path $script:Config.SafeBootNetwork -Force -ErrorAction Stop
                Write-Log "  Removed: SafeBoot\Network" -Level "SUCCESS"
            }
            catch {
                Write-Log "  Failed to remove SafeBoot\Network: $($_.Exception.Message)" -Level "WARNING"
            }
        }
        else {
            Write-Log "  Skipped: SafeBoot\Network (not present)" -Level "INFO"
        }
        
        Write-Log "========================================" -Level "INFO"
        Write-Log "REMOVAL COMPLETE ($removedCount feature flags removed)" -Level "SUCCESS"
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
    if ($script:btnFullBackup) { $script:btnFullBackup.Enabled = $Enabled }
    if ($script:btnExport) { $script:btnExport.Enabled = $Enabled }
    
    [System.Windows.Forms.Application]::DoEvents()
}

function Refresh-StatusDisplay {
    $status = Test-PatchStatus
    
    # Safely access properties
    $isApplied = $false
    $isPartial = $false
    $keyCount = 0
    $totalCount = $script:Config.FeatureIDs.Count
    
    if ($null -ne $status) {
        $isApplied = [bool]$status.Applied
        $isPartial = [bool]$status.Partial
        if ($null -ne $status.Keys) {
            $keyCount = @($status.Keys).Count
        }
    }
    
    if ($isApplied) {
        Update-StatusIndicator -Panel $script:pnlPatchStatus -Status "OK" -Text "Patch Applied"
        if ($script:btnApply) {
            $script:btnApply.Text = "REINSTALL"
            # Swap visual emphasis - Remove becomes primary when patch is applied
            # Update Tag safely
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
    elseif ($isPartial) {
        Update-StatusIndicator -Panel $script:pnlPatchStatus -Status "Warning" -Text "Partial ($keyCount/$totalCount)"
    }
    else {
        Update-StatusIndicator -Panel $script:pnlPatchStatus -Status "Neutral" -Text "Not Applied"
        if ($script:btnApply) {
            $script:btnApply.Text = "APPLY PATCH"
            # Apply is primary when patch is not installed
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
        [string]$WarningText = ""
    )
    
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

# Main Form
$script:form = New-Object System.Windows.Forms.Form
$script:form.Text = "$($script:Config.AppName) v$($script:Config.AppVersion)"
$script:form.Size = New-Object System.Drawing.Size(580, 786)
$script:form.StartPosition = "CenterScreen"
$script:form.FormBorderStyle = "FixedSingle"
$script:form.MaximizeBox = $false
$script:form.BackColor = $script:Colors.Background
$script:form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

# Enable double buffering on form to reduce flicker
$script:form.GetType().GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic).SetValue($script:form, $true, $null)

# ============ HEADER SECTION ============
$pnlHeader = New-Object System.Windows.Forms.Panel
$pnlHeader.Location = New-Object System.Drawing.Point(0, 0)
$pnlHeader.Size = New-Object System.Drawing.Size(580, 101)
$pnlHeader.BackColor = $script:Colors.Surface

# Enable double buffering
$pnlHeader.GetType().GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic).SetValue($pnlHeader, $true, $null)

# App Icon/Logo area
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

# Header divider line with proper GDI cleanup
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

# ============ STATUS CARD ============
$cardStatus = New-CardPanel -Location (New-Object System.Drawing.Point(20, 117)) -Size (New-Object System.Drawing.Size(524, 96)) -Title "System Status"

# Patch Status Indicator
$script:pnlPatchStatus = New-StatusIndicator -Location (New-Object System.Drawing.Point(16, 36)) -Size (New-Object System.Drawing.Size(240, 50)) -Label "Driver Patch"
$cardStatus.Controls.Add($script:pnlPatchStatus)

# Windows Status Indicator
$script:pnlWinStatus = New-StatusIndicator -Location (New-Object System.Drawing.Point(270, 36)) -Size (New-Object System.Drawing.Size(240, 50)) -Label "Windows Version"
$cardStatus.Controls.Add($script:pnlWinStatus)

$script:form.Controls.Add($cardStatus)

# ============ WARNING CARD ============
$cardWarning = New-Object System.Windows.Forms.Panel
$cardWarning.Location = New-Object System.Drawing.Point(20, 225)
$cardWarning.Size = New-Object System.Drawing.Size(524, 56)
$cardWarning.BackColor = $script:Colors.WarningDim
Set-ControlTagData -Control $cardWarning -NewData @{ CornerRadius = 8 }
Set-RoundedCorners -Control $cardWarning -Radius 8

# Warning icon
$lblWarningIcon = New-Object System.Windows.Forms.Label
$lblWarningIcon.Text = "!"
$lblWarningIcon.Location = New-Object System.Drawing.Point(16, 12)
$lblWarningIcon.Size = New-Object System.Drawing.Size(32, 32)
$lblWarningIcon.ForeColor = $script:Colors.Warning
$lblWarningIcon.Font = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
$lblWarningIcon.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$lblWarningIcon.BackColor = [System.Drawing.Color]::FromArgb(60, 50, 30)
Set-RoundedCorners -Control $lblWarningIcon -Radius 16
$cardWarning.Controls.Add($lblWarningIcon)

$lblWarningText = New-Object System.Windows.Forms.Label
$lblWarningText.Text = "This tool modifies system registry. A restore point will be created automatically before any changes are made."
$lblWarningText.Location = New-Object System.Drawing.Point(58, 12)
$lblWarningText.Size = New-Object System.Drawing.Size(450, 32)
$lblWarningText.ForeColor = $script:Colors.Warning
$lblWarningText.Font = New-Object System.Drawing.Font("Segoe UI", 8.5)
$cardWarning.Controls.Add($lblWarningText)

$script:form.Controls.Add($cardWarning)

# ============ ACTIONS CARD ============
$cardActions = New-CardPanel -Location (New-Object System.Drawing.Point(20, 293)) -Size (New-Object System.Drawing.Size(524, 150)) -Title "Actions"

# Row 1: Primary Action Buttons (consistent height: 44px)
$script:btnApply = New-ModernButton `
    -Text "APPLY PATCH" `
    -Location (New-Object System.Drawing.Point(16, 40)) `
    -Size (New-Object System.Drawing.Size(156, 44)) `
    -BackColor $script:Colors.Success `
    -HoverColor $script:Colors.SuccessHover `
    -ToolTip "Apply NVMe driver enhancement patch" `
    -IsPrimary $true `
    -CornerRadius 6 `
    -OnClick {
        if (Show-ConfirmDialog -Title "Apply Patch" -Message "Apply the NVMe driver enhancement patch?" -WarningText "This will modify system registry settings.") {
            Install-NVMePatch
        }
    }
$cardActions.Controls.Add($script:btnApply)

$script:btnRemove = New-ModernButton `
    -Text "REMOVE PATCH" `
    -Location (New-Object System.Drawing.Point(184, 40)) `
    -Size (New-Object System.Drawing.Size(156, 44)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Remove all NVMe patch registry entries" `
    -IsPrimary $false `
    -CornerRadius 6 `
    -OnClick {
        if (Show-ConfirmDialog -Title "Remove Patch" -Message "Remove the NVMe driver patch?" -WarningText "This will revert to default Windows behavior.") {
            Uninstall-NVMePatch
        }
    }
$cardActions.Controls.Add($script:btnRemove)

$script:btnBackup = New-ModernButton `
    -Text "CREATE RESTORE POINT" `
    -Location (New-Object System.Drawing.Point(352, 40)) `
    -Size (New-Object System.Drawing.Size(156, 44)) `
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

# Row 2: Secondary Action - System Registry Backup (consistent height: 36px)
$script:btnFullBackup = New-ModernButton `
    -Text "BACKUP SYSTEM REGISTRY (HKLM)" `
    -Location (New-Object System.Drawing.Point(16, 96)) `
    -Size (New-Object System.Drawing.Size(492, 36)) `
    -BackColor $script:Colors.SurfaceLight `
    -HoverColor $script:Colors.SurfaceHover `
    -ToolTip "Export HKEY_LOCAL_MACHINE registry to $($script:Config.WorkingDir)" `
    -IsPrimary $false `
    -CornerRadius 6 `
    -OnClick {
        $confirm = [System.Windows.Forms.MessageBox]::Show(
            "This will export the system registry (HKEY_LOCAL_MACHINE).`n`nThe backup file will be saved to:`n$($script:Config.WorkingDir)`n`nThis may take several minutes and the file can be large (100-500 MB).`n`nContinue?",
            "System Registry Backup",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Question
        )
        
        if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
            Backup-SystemRegistry
        }
    }
$cardActions.Controls.Add($script:btnFullBackup)

$script:form.Controls.Add($cardActions)

# ============ LOG CARD ============
$cardLog = New-CardPanel -Location (New-Object System.Drawing.Point(20, 455)) -Size (New-Object System.Drawing.Size(524, 268)) -Title "Activity Log"

# Export Log Button
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

# RichTextBox for colorized log output
$script:rtbOutput = New-Object System.Windows.Forms.RichTextBox
$script:rtbOutput.Location = New-Object System.Drawing.Point(16, 36)
$script:rtbOutput.Size = New-Object System.Drawing.Size(492, 216)
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
$lblFooter.Location = New-Object System.Drawing.Point(20, 734)
$lblFooter.Size = New-Object System.Drawing.Size(524, 16)
$lblFooter.ForeColor = $script:Colors.TextDimmed
$lblFooter.Font = New-Object System.Drawing.Font("Segoe UI", 7.5)
$lblFooter.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$script:form.Controls.Add($lblFooter)

# ===========================================================================
# SECTION 10: FORM EVENT HANDLERS & STARTUP
# ===========================================================================

$script:form.Add_Load({
    Write-Log "$($script:Config.AppName) v$($script:Config.AppVersion) started"
    Write-Log "Working directory: $($script:Config.WorkingDir)"
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
    # Dispose tooltip provider
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
