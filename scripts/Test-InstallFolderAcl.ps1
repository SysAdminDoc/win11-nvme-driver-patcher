# Test-InstallFolderAcl.ps1
# Destructive local packaging smoke: installs the MSI into a deliberately user-writable directory
# and proves the resulting install folder denies write to standard users, then uninstalls.
#
# INSTALLFOLDER is user-selectable through WixUI_InstallDir. Without the explicit PermissionEx DACL
# it inherits its parent's, so an install to a path like C:\Tools leaves every shipped binary
# writable by a standard user while the MSI registers the watchdog as an auto-start service and
# invokes it from a deferred SYSTEM custom action. This smoke is what proves the DACL is applied at
# install time rather than merely authored in the .wxs; the unit test can only see the authoring.
#
# Must run elevated (msiexec per-machine install).
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$MsiPath,
    [string]$InstallRoot = (Join-Path $env:SystemDrive 'NVMePatcherAclSmoke'),
    [switch]$KeepInstalled
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw 'This smoke performs a per-machine MSI install and must run elevated.'
}

$msi = (Resolve-Path -LiteralPath $MsiPath).Path
$installFolder = Join-Path $InstallRoot 'NVMe Driver Patcher'
$msiexec = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'msiexec.exe'
$installed = $false

# SIDs already trusted to write into a privileged program directory. Anything else holding a
# write, delete, WRITE_DAC or WRITE_OWNER right makes the directory plantable.
$trustedWriters = @('S-1-5-18', 'S-1-5-32-544', 'S-1-3-0')
$plantableRights =
    0x00000002 -bor 0x00000004 -bor 0x00000010 -bor 0x00000040 -bor
    0x00000100 -bor 0x00010000 -bor 0x00040000 -bor 0x00080000 -bor
    0x10000000 -bor 0x40000000

function Invoke-MsiExec {
    param([Parameter(Mandatory)] [string[]]$Arguments)
    $log = Join-Path $env:TEMP ('nvme-acl-smoke-' + [guid]::NewGuid().ToString('N') + '.log')
    $proc = Start-Process -FilePath $msiexec -ArgumentList ($Arguments + @('/qn', '/l*v', $log)) -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        throw "msiexec $($Arguments -join ' ') failed with exit $($proc.ExitCode). Log: $log"
    }
    Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
}

try {
    # A parent directory a standard user can write, so the install folder would inherit a
    # user-writable DACL if the MSI did not pin its own.
    if (-not (Test-Path -LiteralPath $InstallRoot)) { New-Item -ItemType Directory -Path $InstallRoot | Out-Null }
    $rootAcl = Get-Acl -LiteralPath $InstallRoot
    $rootAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        'BUILTIN\Users', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $InstallRoot -AclObject $rootAcl

    Invoke-MsiExec @('/i', $msi, "INSTALLFOLDER=$installFolder", 'ADDLOCAL=Main,WatchdogService')
    $installed = $true

    if (-not (Test-Path -LiteralPath $installFolder -PathType Container)) {
        throw "MSI did not create '$installFolder'."
    }

    $acl = Get-Acl -LiteralPath $installFolder
    $owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    if ($owner -ne 'S-1-5-32-544' -and $owner -ne 'S-1-5-18') {
        throw "Install folder owner is '$owner'; an owner outside SYSTEM/Administrators keeps WRITE_DAC and can restore the write right."
    }
    if (-not $acl.AreAccessRulesProtected) {
        throw 'Install folder still inherits its parent DACL; the PermissionEx protected DACL was not applied.'
    }

    foreach ($ace in $acl.Access) {
        if ($ace.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if (((([int] $ace.FileSystemRights) -band $plantableRights) -band 0xFFFFFFFF) -eq 0) { continue }
        $sid = $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
        if ($trustedWriters -notcontains $sid) {
            throw "Install folder grants write access to '$($ace.IdentityReference)' ($sid) -- the watchdog binary is plantable."
        }
    }

    # The binaries themselves inherit the folder ACEs; check the one the SYSTEM custom action runs.
    $watchdog = Join-Path $installFolder 'NVMeDriverPatcher.Watchdog.exe'
    if (Test-Path -LiteralPath $watchdog) {
        foreach ($ace in (Get-Acl -LiteralPath $watchdog).Access) {
            if ($ace.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
            if (((([int] $ace.FileSystemRights) -band $plantableRights) -band 0xFFFFFFFF) -eq 0) { continue }
            $sid = $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            if ($trustedWriters -notcontains $sid) {
                throw "Watchdog binary is writable by '$($ace.IdentityReference)' ($sid)."
            }
        }
    }

    Write-Host "Install-folder ACL smoke passed: '$installFolder' is owner-Administrators, protected, and writable only by SYSTEM/Administrators." -ForegroundColor Green
}
finally {
    if ($installed -and -not $KeepInstalled) {
        Invoke-MsiExec @('/x', $msi)
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
