# Test-InstallFolderAcl.ps1
# Destructive local packaging smoke, run elevated on a test machine. It checks two things.
#
# 1. The MSI refuses an INSTALLFOLDER outside Program Files. The DACL it pins on the install folder
#    protects the files but not the path to them: a standard user who can rename a parent like
#    C:\Tools can move the real folder aside and build their own tree at the same path. The Launch
#    condition in NVMeDriverPatcher.wxs refuses such folders; this proves it fires, for the right
#    reason, and that nothing is installed.
# 2. A default install lands under Program Files with an owner-Administrators, protected DACL that
#    gives no standard user a write right, on the folder and on the watchdog binary the deferred
#    SYSTEM custom action runs. The unit tests can only see the .wxs authoring.
#
# It then uninstalls, unless -KeepInstalled. Like any per-machine install of this UpgradeCode, it
# upgrades and then removes a copy already installed on the machine.
#
# Must run elevated (msiexec per-machine install).
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$MsiPath,
    # A folder outside Program Files that standard users can write. The MSI must refuse it.
    [string]$InstallRoot = (Join-Path $env:SystemDrive 'NVMePatcherAclSmoke'),
    [switch]$KeepInstalled
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw 'This smoke performs a per-machine MSI install and must run elevated.'
}

$msi = (Resolve-Path -LiteralPath $MsiPath).Path
$refusedFolder = Join-Path $InstallRoot 'NVMe Driver Patcher'
$msiexec = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'msiexec.exe'
$installed = $false
$createdRoot = $false

# The start of the Launch message in NVMeDriverPatcher.wxs. InstallerContentTests keeps the two in step.
$refusalText = 'NVMe Driver Patcher installs only under Program Files.'

# SIDs already trusted to write into a privileged program directory. Anything else holding a
# write, delete, WRITE_DAC or WRITE_OWNER right makes the directory plantable.
$trustedWriters = @('S-1-5-18', 'S-1-5-32-544', 'S-1-3-0')
$plantableRights =
    0x00000002 -bor 0x00000004 -bor 0x00000010 -bor 0x00000040 -bor
    0x00000100 -bor 0x00010000 -bor 0x00040000 -bor 0x00080000 -bor
    0x10000000 -bor 0x40000000

function Invoke-MsiExecRaw {
    param([Parameter(Mandatory)] [string[]]$Arguments)
    $log = Join-Path $env:TEMP ('nvme-acl-smoke-' + [guid]::NewGuid().ToString('N') + '.log')
    $proc = Start-Process -FilePath $msiexec -ArgumentList ($Arguments + @('/qn', '/l*v', $log)) -Wait -PassThru -WindowStyle Hidden
    [pscustomobject]@{ ExitCode = $proc.ExitCode; Log = $log }
}

function Invoke-MsiExec {
    param([Parameter(Mandatory)] [string[]]$Arguments)
    $run = Invoke-MsiExecRaw $Arguments
    if ($run.ExitCode -ne 0) {
        throw "msiexec $($Arguments -join ' ') failed with exit $($run.ExitCode). Log: $($run.Log)"
    }
    Remove-Item -LiteralPath $run.Log -ErrorAction SilentlyContinue
}

function Assert-NotPlantable {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$What)
    foreach ($ace in (Get-Acl -LiteralPath $Path).Access) {
        if ($ace.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if (((([int] $ace.FileSystemRights) -band $plantableRights) -band 0xFFFFFFFF) -eq 0) { continue }
        $sid = $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
        if ($trustedWriters -notcontains $sid) {
            throw "$What grants write access to '$($ace.IdentityReference)' ($sid) -- it is plantable."
        }
    }
}

try {
    # 1. Outside Program Files, under a parent a standard user can write: refused, nothing lands.
    if (-not (Test-Path -LiteralPath $InstallRoot)) {
        New-Item -ItemType Directory -Path $InstallRoot | Out-Null
        $createdRoot = $true
    }
    $rootAcl = Get-Acl -LiteralPath $InstallRoot
    $rootAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        'BUILTIN\Users', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $InstallRoot -AclObject $rootAcl

    $refused = Invoke-MsiExecRaw @('/i', $msi, "INSTALLFOLDER=$refusedFolder", 'ADDLOCAL=Main,WatchdogService')
    if ($refused.ExitCode -eq 0) {
        $installed = $true
        throw "The MSI installed into '$refusedFolder', outside Program Files. The Launch condition didn't fire."
    }
    if (-not (Select-String -LiteralPath $refused.Log -SimpleMatch $refusalText -Quiet)) {
        throw "msiexec failed with exit $($refused.ExitCode), but not with the Program Files refusal. Log: $($refused.Log)"
    }
    Remove-Item -LiteralPath $refused.Log -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $refusedFolder) {
        throw "The MSI refused the install but still created '$refusedFolder'."
    }

    # 2. The default install: under Program Files, owner-Administrators, protected, not plantable.
    Invoke-MsiExec @('/i', $msi, 'ADDLOCAL=Main,WatchdogService')
    $installed = $true

    $installFolder = (Get-ItemProperty -LiteralPath 'HKLM:\Software\SysAdminDoc\NVMeDriverPatcher' -Name InstallLocation).InstallLocation
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    if (-not $installFolder -or -not $installFolder.StartsWith($programFiles.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "The MSI recorded InstallLocation '$installFolder', which isn't under '$programFiles'."
    }
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
    Assert-NotPlantable -Path $installFolder -What 'Install folder'

    # The binaries themselves inherit the folder ACEs; check the one the SYSTEM custom action runs.
    $watchdog = Join-Path $installFolder 'NVMeDriverPatcher.Watchdog.exe'
    if (Test-Path -LiteralPath $watchdog) {
        Assert-NotPlantable -Path $watchdog -What 'Watchdog binary'
    }

    Write-Host "Install-folder smoke passed: '$refusedFolder' was refused, and '$installFolder' is owner-Administrators, protected, and writable only by SYSTEM/Administrators." -ForegroundColor Green
}
finally {
    if ($installed -and -not $KeepInstalled) {
        Invoke-MsiExec @('/x', $msi)
    }
    if ($createdRoot) {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
