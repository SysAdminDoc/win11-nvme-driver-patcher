$ErrorActionPreference = 'Stop'

$packageArgs = @{
    packageName    = 'nvme-driver-patcher'
    fileType       = 'exe'
    url64bit       = 'https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v5.4.0/NVMeDriverPatcher.exe'
    checksum64     = 'f318a65f5827c0f5de58a377c5318dd57ede8472e4f20bf882f65a3ae6f1e7e9'
    checksumType64 = 'sha256'
    softwareName   = 'NVMe Driver Patcher*'
    unzipLocation  = "$(Split-Path -Parent $MyInvocation.MyCommand.Definition)"
}

$toolsDir = "$(Split-Path -Parent $MyInvocation.MyCommand.Definition)"
$exePath = Join-Path $toolsDir 'NVMeDriverPatcher.exe'

Get-ChocolateyWebFile @packageArgs -FileFullPath $exePath

# Create a shim so 'nvme-driver-patcher' is on PATH.
# The exe has an admin manifest so Chocolatey's shimgen will auto-elevate.
