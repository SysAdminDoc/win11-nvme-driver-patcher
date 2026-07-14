# Test-WatchdogService.ps1
# Destructive local packaging smoke: installs the published watchdog when absent, proves the
# SCM contract and live LocalService startup/readability path, then restores the prior state.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$WatchdogExe,
    [switch]$KeepInstalled
)

$ErrorActionPreference = 'Stop'
$serviceName = 'NVMeDriverPatcherWatchdog'
$exePath = (Resolve-Path $WatchdogExe).Path
$installedBySmoke = $false
$startedBySmoke = $false

function Invoke-ScQuery {
    param([Parameter(Mandatory)] [string]$Command)
    $output = & sc.exe $Command $serviceName 2>&1
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

    $sidOutput = & sc.exe showsid $serviceName 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe showsid failed: $($sidOutput -join ' ')" }
    $serviceSid = [regex]::Match(($sidOutput -join "`n"), 'S-1-5-80-(?:\d+-){4}\d+').Value
    if ([string]::IsNullOrWhiteSpace($serviceSid)) { throw 'Service SID could not be resolved.' }
    $stateDir = Join-Path $env:ProgramData 'NVMePatcher'
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

    # ExecuteAsync performs a live System-channel read before its flush loop. Remaining alive after
    # this grace period proves that read succeeded under the configured LocalService identity.
    Start-Sleep -Seconds 3
    $running = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($running.State -ne 'Running' -or $running.ProcessId -le 0) {
        throw 'Service did not remain running after its LocalService System-log readiness probe.'
    }

    Write-Host 'Watchdog packaging smoke passed: identity, least privilege, service-SID state ACL, recovery actions, and live System-log readability.' -ForegroundColor Green
}
finally {
    if ($startedBySmoke -and -not $KeepInstalled) {
        Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    }
    if ($installedBySmoke -and -not $KeepInstalled) {
        & $exePath /uninstall | Out-Null
    }
}
