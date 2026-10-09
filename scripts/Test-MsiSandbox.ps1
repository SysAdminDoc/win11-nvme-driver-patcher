# Test-MsiSandbox.ps1
# Destructive packaging smoke isolated in Windows Sandbox: installs the built MSI quietly, checks
# Program Files, the watchdog service and the scheduled tasks, runs the installed CLI, uninstalls,
# and rejects anything left behind. Nothing touches the host: the MSI folder is mapped read-only
# and the guest powers off when it finishes.
#
# Needs an x64 Windows 11 host (Pro, Enterprise or Education) with the Windows Sandbox feature
# turned on (Containers-DisposableClientVM). Without it the script fails fast with a message.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$MsiPath,
    [ValidateRange(120, 1800)] [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

# Resolved to System32 rather than looked up on $PATH: this script may run elevated, so an earlier
# $PATH entry holding a WindowsSandbox.exe would be launched with administrator rights.
$systemDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
$sandboxExe = Join-Path $systemDir 'WindowsSandbox.exe'
if (-not (Test-Path -LiteralPath $sandboxExe -PathType Leaf)) {
    throw 'Windows Sandbox is unavailable. Enable the Containers-DisposableClientVM feature, restart, and rerun this smoke.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'The MSI smoke needs an x64 Windows host.'
}

$msi = (Resolve-Path -LiteralPath $MsiPath).Path
if ([IO.Path]::GetExtension($msi) -ne '.msi') { throw "MsiPath must point at an .msi file: $msi" }
$msiName = [IO.Path]::GetFileName($msi)
$expectedVersion = ''
if ($msiName -match '^NVMeDriverPatcher-(?<v>\d+\.\d+\.\d+)\.msi$') { $expectedVersion = $Matches['v'] }

$workspace = Join-Path $env:TEMP "NVMeDriverPatcher.MsiSmoke.$([Guid]::NewGuid().ToString('N'))"
$inputDir = Join-Path $workspace 'input'
$guestDir = Join-Path $workspace 'guest'
New-Item -ItemType Directory -Path $inputDir, $guestDir | Out-Null
$resultPath = Join-Path $guestDir 'result.json'
$guestLogPath = Join-Path $guestDir 'guest.log'

# The guest script is a literal here-string: the host never expands it, and it never runs here.
$bootstrapBody = @'
$ErrorActionPreference = 'Stop'
$inputRoot = 'C:\NVMeMsiInput'
$outRoot = 'C:\NVMeMsiSmoke'
$logPath = Join-Path $outRoot 'guest.log'
$result = [ordered]@{ Success = $false; Steps = @(); Error = $null }

# Every tool by absolute path; the guest is a clean image, but the habit is the point.
$sys32 = Join-Path $env:SystemRoot 'System32'
$msiexec = Join-Path $sys32 'msiexec.exe'
$schtasks = Join-Path $sys32 'schtasks.exe'
$shutdownExe = Join-Path $sys32 'shutdown.exe'
$installDir = Join-Path $env:ProgramFiles 'NVMe Driver Patcher'
$cliExe = Join-Path $installDir 'NVMeDriverPatcher.Cli.exe'
$serviceName = 'NVMeDriverPatcherWatchdog'
$taskNames = @('SysAdminDoc\NVMePatcher\BootVerify', 'SysAdminDoc\NVMePatcher\WatchdogSweep')
$startMenuDir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\NVMe Driver Patcher'

function Write-Step {
    param([string]$Name, [int]$ExitCode, [string]$Output)
    $script:result.Steps += [ordered]@{ Name = $Name; ExitCode = $ExitCode; Output = $Output }
    Add-Content -LiteralPath $logPath -Value ("[{0}] {1} exit={2} {3}" -f (Get-Date -Format 'HH:mm:ss'), $Name, $ExitCode, $Output)
}

function Invoke-Step {
    param([string]$Name, [string]$Exe, [string[]]$Arguments, [int[]]$OkExit = @(0))
    # Windows PowerShell 5.1 turns native stderr into a terminating error under Stop; the exit
    # code is the verdict here, so let the output through.
    $ErrorActionPreference = 'Continue'
    $output = (& $Exe @Arguments 2>&1 | Out-String).Trim()
    $exit = $LASTEXITCODE
    Write-Step $Name $exit $output
    if ($OkExit -notcontains $exit) { throw "$Name failed with exit $exit" }
    return $output
}

try {
    New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
    $msiFile = Get-ChildItem -LiteralPath $inputRoot -Filter '*.msi' -File | Select-Object -First 1
    if (-not $msiFile) { throw 'No MSI was mapped into the sandbox.' }
    if (Test-Path -LiteralPath $installDir) { throw 'The clean sandbox already has an install folder.' }

    # ADDLOCAL=ALL turns on the opt-in watchdog service feature too (3010 = reboot wanted, fine).
    Invoke-Step 'install' $msiexec @('/i', $msiFile.FullName, '/qn', '/norestart', 'ADDLOCAL=ALL', '/l*v', (Join-Path $outRoot 'install.log')) @(0, 3010) | Out-Null

    foreach ($file in 'NVMeDriverPatcher.exe', 'NVMeDriverPatcher.Cli.exe', 'NVMeDriverPatcher.Tray.exe', 'NVMeDriverPatcher.Watchdog.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $installDir $file) -PathType Leaf)) {
            throw "Program Files is missing $file after install."
        }
    }
    Write-Step 'program-files' 0 $installDir

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $service) { throw 'The watchdog service was not registered by the install.' }
    Write-Step 'service-registered' 0 ("{0} {1}" -f $service.Name, $service.Status)

    $version = Invoke-Step 'cli-version' $cliExe @('version')
    if ($version -notmatch 'NVMe Driver Patcher CLI v\d+\.\d+\.\d+') { throw "Installed CLI answered unexpectedly: $version" }
    if ($expectedVersion -and $version -notmatch [regex]::Escape("v$expectedVersion")) {
        throw "Installed CLI reports '$version', expected v$expectedVersion."
    }

    # The MSI doesn't register the scheduled tasks; the CLI does, so prove that route end to end.
    Invoke-Step 'register-tasks' $cliExe @('register-tasks') | Out-Null
    foreach ($task in $taskNames) {
        Invoke-Step "task-present $task" $schtasks @('/Query', '/TN', $task) | Out-Null
    }
    Invoke-Step 'unregister-tasks' $cliExe @('unregister-tasks') | Out-Null

    Invoke-Step 'uninstall' $msiexec @('/x', $msiFile.FullName, '/qn', '/norestart', '/l*v', (Join-Path $outRoot 'uninstall.log')) @(0, 3010) | Out-Null

    $residue = @()
    if (Test-Path -LiteralPath $installDir) { $residue += $installDir }
    if (Test-Path -LiteralPath $startMenuDir) { $residue += $startMenuDir }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { $residue += "service $serviceName" }
    foreach ($task in $taskNames) {
        $queryOutput = Invoke-Step "task-gone $task" $schtasks @('/Query', '/TN', $task) @(0, 1)
        if ($LASTEXITCODE -eq 0) { $residue += "task $task" }
    }
    Write-Step 'residue-check' 0 ($residue -join '; ')
    if ($residue.Count -gt 0) { throw "Left behind after uninstall: $($residue -join ', ')" }

    $result.Success = $true
}
catch {
    $result.Error = $_.Exception.Message
    Add-Content -LiteralPath $logPath -Value ("[error] {0}" -f $_.Exception.Message)
}
finally {
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outRoot 'result.json') -Encoding UTF8
    & $shutdownExe /s /t 0 /f | Out-Null
}
'@

try {
    Copy-Item -LiteralPath $msi -Destination (Join-Path $inputDir $msiName)
    $bootstrap = "`$expectedVersion = '$expectedVersion'`r`n" + $bootstrapBody
    Set-Content -LiteralPath (Join-Path $guestDir 'bootstrap.ps1') -Value $bootstrap -Encoding UTF8

    $escapedInput = [System.Security.SecurityElement]::Escape($inputDir)
    $escapedGuest = [System.Security.SecurityElement]::Escape($guestDir)
    $wsb = @"
<Configuration>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$escapedInput</HostFolder>
      <SandboxFolder>C:\NVMeMsiInput</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$escapedGuest</HostFolder>
      <SandboxFolder>C:\NVMeMsiSmoke</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\NVMeMsiSmoke\bootstrap.ps1</Command>
  </LogonCommand>
</Configuration>
"@
    $wsbPath = Join-Path $workspace 'msi-smoke.wsb'
    Set-Content -LiteralPath $wsbPath -Value $wsb -Encoding UTF8
    $sandbox = Start-Process -FilePath $sandboxExe -ArgumentList "`"$wsbPath`"" -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $resultPath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 2
    }
    if (-not (Test-Path -LiteralPath $resultPath)) {
        if (-not $sandbox.HasExited) { Stop-Process -Id $sandbox.Id -Force -ErrorAction SilentlyContinue }
        throw "Windows Sandbox MSI smoke timed out after $TimeoutSeconds seconds."
    }

    if (Test-Path -LiteralPath $guestLogPath) {
        Write-Host '--- guest log ---'
        Get-Content -LiteralPath $guestLogPath | ForEach-Object { Write-Host $_ }
        Write-Host '--- end guest log ---'
    }
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if (-not $result.Success) {
        $steps = $result.Steps | ForEach-Object { "$($_.Name)=$($_.ExitCode)" }
        throw "Windows Sandbox MSI smoke failed: $($result.Error) (steps: $($steps -join ', '))"
    }
    Write-Host 'Windows Sandbox MSI smoke passed: install, installed CLI version, scheduled tasks, uninstall, and no files, service or task left behind.' -ForegroundColor Green
}
finally {
    try { Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
