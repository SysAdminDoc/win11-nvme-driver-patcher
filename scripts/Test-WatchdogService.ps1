# Test-WatchdogService.ps1
# Destructive local packaging smoke: installs the published watchdog when absent, proves the
# SCM contract and live LocalService startup/readability path, then restores the prior state.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$WatchdogExe,
    [switch]$KeepInstalled,
    # The flush loop retries a failing evaluation twice at 30s spacing before it gives up, so a
    # service that cannot read its own state dies at roughly t+60-90s. A 3-second probe proved only
    # that the process started and let exactly that defect ship; stay past the third failure.
    [ValidateRange(5, 600)] [int]$LivenessSeconds = 150
)

$ErrorActionPreference = 'Stop'
$serviceName = 'NVMeDriverPatcherWatchdog'
$exePath = (Resolve-Path $WatchdogExe).Path
# Resolved to System32, not looked up on $PATH: this smoke runs elevated, so a planted sc.exe in an
# earlier PATH entry or in the working directory would run with administrator rights -- and a stub
# returning success would make every SCM assertion below pass against a service that is not there.
$scExe = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'sc.exe'
$installedBySmoke = $false
$startedBySmoke = $false

function Invoke-ScQuery {
    param([Parameter(Mandatory)] [string]$Command)
    $output = & $scExe $Command $serviceName 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $Command failed ($LASTEXITCODE): $($output -join ' ')"
    }
    return $output -join "`n"
}

try {
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        & $exePath /install
        if ($LASTEXITCODE -ne 0) { throw "Watchdog /install failed with exit $LASTEXITCODE." }
        $installedBySmoke = $true
        $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    }

    if ($service.StartName -notmatch '^(NT AUTHORITY\\)?LocalService$') {
        throw "Service account is '$($service.StartName)', expected NT AUTHORITY\LocalService."
    }
    if ($service.StartMode -ne 'Auto') { throw "Service start mode is '$($service.StartMode)', expected Auto." }
    if ($service.PathName -notmatch [regex]::Escape($exePath)) {
        throw "Service ImagePath '$($service.PathName)' does not target '$exePath'."
    }

    $failure = Invoke-ScQuery 'qfailure'
    if ([regex]::Matches($failure, 'RESTART', 'IgnoreCase').Count -lt 2) {
        throw 'First and second SCM failure actions are not both restart.'
    }
    if ($failure -match 'REBOOT') { throw 'Watchdog recovery must never reboot the machine.' }

    $failureFlag = Invoke-ScQuery 'qfailureflag'
    if ($failureFlag -notmatch 'FAILURE_ACTIONS_ON_NONCRASH_FAILURES\s*:\s*TRUE') {
        throw 'Non-crash failure actions are not enabled.'
    }

    $privileges = Invoke-ScQuery 'qprivs'
    $privilegeNames = [regex]::Matches($privileges, 'Se[A-Za-z]+Privilege') |
        ForEach-Object { $_.Value } | Select-Object -Unique
    if ($privilegeNames.Count -ne 1 -or $privilegeNames[0] -ne 'SeChangeNotifyPrivilege') {
        throw "Required privilege contract differs from SeChangeNotifyPrivilege only: $($privilegeNames -join ', ')"
    }

    $serviceKey = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    if ($serviceKey.ServiceSidType -ne 3) { throw 'Service SID type is not Restricted (3).' }

    $sidOutput = & $scExe showsid $serviceName 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe showsid failed: $($sidOutput -join ' ')" }
    $serviceSid = [regex]::Match(($sidOutput -join "`n"), 'S-1-5-80-(?:\d+-){4}\d+').Value
    if ([string]::IsNullOrWhiteSpace($serviceSid)) { throw 'Service SID could not be resolved.' }
    $stateDir = Join-Path $env:ProgramData 'NVMePatcher\Watchdog'
    $stateAcl = Get-Acl -LiteralPath $stateDir
    $stateAccess = $stateAcl.Access | Where-Object {
        try {
            $sid = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            $sid -eq $serviceSid -and $_.AccessControlType -eq 'Allow' -and
                (($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Modify) -eq
                 [System.Security.AccessControl.FileSystemRights]::Modify)
        }
        catch { $false }
    }
    if (-not $stateAccess) { throw "State directory does not grant Modify to service SID $serviceSid." }

    if ($service.State -ne 'Running') {
        Start-Service $serviceName
        $startedBySmoke = $true
    }
    (Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))

    # ExecuteAsync performs a live System-channel read before its flush loop, then flushes state
    # immediately and every 5 minutes after. Both halves have to survive under the LocalService
    # token: the readiness probe reads the System channel, the flush reads AND writes the protected
    # ProgramData state. Watching only the first one is what let a service that could never load its
    # own state pass this smoke and ship.
    Write-Host "Watching the service for $LivenessSeconds s (past the flush loop's third failure)..."
    $watchStart = Get-Date
    $deadline = $watchStart.AddSeconds($LivenessSeconds)
    while ((Get-Date) -lt $deadline) {
        $running = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
        if ($running.State -ne 'Running' -or $running.ProcessId -le 0) {
            $elapsed = [int]((Get-Date) - $watchStart).TotalSeconds
            throw "Service stopped after ${elapsed}s (state '$($running.State)'). Check the Application log for the watchdog's flush failures."
        }
        Start-Sleep -Seconds 5
    }

    # The flush loop only clears its failure counter after a successful Evaluate, so a service that
    # is still Running here has published state at least once. Prove that directly too.
    $statePath = Join-Path $env:ProgramData 'NVMePatcher\Watchdog\watchdog.json'
    if (-not (Test-Path -LiteralPath $statePath)) {
        throw "Service stayed running but never published '$statePath'; its flush path is not working."
    }
    $stateAge = (Get-Date) - (Get-Item -LiteralPath $statePath).LastWriteTime
    Write-Host ("Watchdog state published {0:N0}s ago." -f $stateAge.TotalSeconds)

    Write-Host 'Watchdog packaging smoke passed: identity, least privilege, service-SID state ACL, recovery actions, live System-log readability, and a surviving flush loop with published state.' -ForegroundColor Green
}
finally {
    if ($startedBySmoke -and -not $KeepInstalled) {
        Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    }
    if ($installedBySmoke -and -not $KeepInstalled) {
        & $exePath /uninstall | Out-Null
    }
}
