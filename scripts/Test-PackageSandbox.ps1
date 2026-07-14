# Test-PackageSandbox.ps1
# Destructive packaging smoke isolated in Windows Sandbox: validates and installs the generated
# x64 winget manifest, queries the portable registration, uninstalls it, and rejects residue.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ManifestPath,
    [Parameter(Mandatory)] [string]$ExePath,
    [ValidateRange(120, 1800)] [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$sandboxCommand = Get-Command WindowsSandbox.exe -ErrorAction SilentlyContinue
if ($null -eq $sandboxCommand) {
    throw 'Windows Sandbox is unavailable. Enable Containers-DisposableClientVM, restart, and rerun this smoke.'
}

$manifestInput = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifestRoot = if (Test-Path -LiteralPath $manifestInput -PathType Container) {
    $manifestInput
} else {
    Split-Path -Parent $manifestInput
}
$installerManifest = Get-ChildItem -LiteralPath $manifestRoot -Filter '*.installer.yaml' -File
if (@($installerManifest).Count -ne 1) { throw 'ManifestPath must contain exactly one winget installer manifest.' }
$exe = (Resolve-Path -LiteralPath $ExePath).Path
$expectedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToUpperInvariant()
$yaml = Get-Content -Raw -LiteralPath $installerManifest.FullName

# Bind the smoke manifest to a sandbox-local HTTP endpoint while preserving the generated hash.
$lines = $yaml -split '\r?\n'
$currentArchitecture = $null
$x64UrlCount = 0
$x64HashCount = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*-\s*Architecture:\s*["'']?([^"''\s]+)') {
        $currentArchitecture = $Matches[1].ToLowerInvariant()
    } elseif ($currentArchitecture -eq 'x64' -and $lines[$i] -match '^(\s*InstallerUrl:\s*)\S+') {
        $lines[$i] = $Matches[1] + 'http://127.0.0.1:8765/NVMeDriverPatcher.exe'
        $x64UrlCount++
    } elseif ($currentArchitecture -eq 'x64' -and $lines[$i] -match '^\s*InstallerSha256:\s*(\S+)') {
        if ($Matches[1] -ne $expectedHash) { throw 'Generated winget x64 hash does not match ExePath.' }
        $x64HashCount++
    }
}
if ($x64UrlCount -ne 1 -or $x64HashCount -ne 1) {
    throw 'Generated winget manifest must contain exactly one x64 URL and hash.'
}

$workspace = Join-Path $env:TEMP "NVMeDriverPatcher.PackageSmoke.$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $workspace | Out-Null
$resultPath = Join-Path $workspace 'result.json'

$serverScript = @'
$ErrorActionPreference = 'Stop'
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add('http://127.0.0.1:8765/')
$listener.Start()
try {
    while ($true) {
        $context = $listener.GetContext()
        if ($context.Request.Url.AbsolutePath -ne '/NVMeDriverPatcher.exe') {
            $context.Response.StatusCode = 404
            $context.Response.Close()
            continue
        }
        $bytes = [System.IO.File]::ReadAllBytes('C:\NVMePackageSmoke\NVMeDriverPatcher.exe')
        $context.Response.StatusCode = 200
        $context.Response.ContentLength64 = $bytes.Length
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $context.Response.Close()
    }
}
finally { $listener.Stop(); $listener.Close() }
'@

$bootstrapScript = @'
$ErrorActionPreference = 'Stop'
$root = 'C:\NVMePackageSmoke'
$result = [ordered]@{ Success = $false; Steps = @(); Error = $null }
$server = $null
function Invoke-CheckedWinget {
    param([Parameter(Mandatory)] [string[]]$Arguments, [Parameter(Mandatory)] [string]$Step)
    $output = & winget.exe @Arguments 2>&1
    $exit = $LASTEXITCODE
    $script:result.Steps += [ordered]@{ Name = $Step; ExitCode = $exit; Output = ($output -join "`n") }
    if ($exit -ne 0) { throw "winget $Step failed with exit $exit" }
    return ($output -join "`n")
}
try {
    $server = Start-Process powershell.exe -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$root\serve.ps1") -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing -Method Head -Uri 'http://127.0.0.1:8765/NVMeDriverPatcher.exe' -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'Sandbox-local package server did not become ready.' }

    Invoke-CheckedWinget @('settings', '--enable', 'LocalManifestFiles') 'enable-local-manifests' | Out-Null
    Invoke-CheckedWinget @('validate', '--manifest', "$root\manifests") 'validate' | Out-Null
    Invoke-CheckedWinget @(
        'install', '--manifest', "$root\manifests", '--architecture', 'x64',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity') 'install' | Out-Null
    $query = Invoke-CheckedWinget @('list', '--id', 'SysAdminDoc.NVMeDriverPatcher', '--exact', '--disable-interactivity') 'query'
    if ($query -notmatch 'SysAdminDoc\.NVMeDriverPatcher') { throw 'Installed package was not queryable by exact identifier.' }

    Invoke-CheckedWinget @(
        'uninstall', '--id', 'SysAdminDoc.NVMeDriverPatcher', '--exact', '--disable-interactivity') 'uninstall' | Out-Null
    $postQuery = & winget.exe list --id SysAdminDoc.NVMeDriverPatcher --exact --disable-interactivity 2>&1
    $result.Steps += [ordered]@{ Name = 'post-uninstall-query'; ExitCode = $LASTEXITCODE; Output = ($postQuery -join "`n") }
    if (($postQuery -join "`n") -match 'NVMe Driver Patcher\s+SysAdminDoc\.NVMeDriverPatcher\s+') {
        throw 'Package registration remained after uninstall.'
    }

    $residue = @(
        "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\SysAdminDoc.NVMeDriverPatcher*",
        "$env:LOCALAPPDATA\Microsoft\WinGet\Links\NVMeDriverPatcher.exe",
        "$env:ProgramFiles\WinGet\Packages\SysAdminDoc.NVMeDriverPatcher*",
        "$env:ProgramFiles\WinGet\Links\NVMeDriverPatcher.exe"
    ) | Where-Object { Get-Item $_ -ErrorAction SilentlyContinue }
    if ($residue) { throw "Portable package residue remained: $($residue -join ', ')" }
    $result.Success = $true
}
catch { $result.Error = $_.Exception.Message }
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$root\result.json" -Encoding UTF8
    shutdown.exe /s /t 0 /f | Out-Null
}
'@

try {
    Copy-Item -LiteralPath $exe -Destination (Join-Path $workspace 'NVMeDriverPatcher.exe')
    $sandboxManifests = Join-Path $workspace 'manifests'
    New-Item -ItemType Directory -Path $sandboxManifests | Out-Null
    Copy-Item -Path (Join-Path $manifestRoot '*.yaml') -Destination $sandboxManifests
    Set-Content -LiteralPath (Join-Path $sandboxManifests $installerManifest.Name) -Value ($lines -join [Environment]::NewLine) -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $workspace 'serve.ps1') -Value $serverScript -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $workspace 'bootstrap.ps1') -Value $bootstrapScript -Encoding UTF8

    $escapedWorkspace = [System.Security.SecurityElement]::Escape($workspace)
    $wsb = @"
<Configuration>
  <Networking>Enable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$escapedWorkspace</HostFolder>
      <SandboxFolder>C:\NVMePackageSmoke</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\NVMePackageSmoke\bootstrap.ps1</Command>
  </LogonCommand>
</Configuration>
"@
    $wsbPath = Join-Path $workspace 'package-smoke.wsb'
    Set-Content -LiteralPath $wsbPath -Value $wsb -Encoding UTF8
    $sandbox = Start-Process -FilePath $sandboxCommand.Source -ArgumentList $wsbPath -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $resultPath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 2
    }
    if (-not (Test-Path -LiteralPath $resultPath)) {
        if (-not $sandbox.HasExited) { Stop-Process -Id $sandbox.Id -Force -ErrorAction SilentlyContinue }
        throw "Windows Sandbox package smoke timed out after $TimeoutSeconds seconds."
    }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if (-not $result.Success) {
        $steps = $result.Steps | ForEach-Object { "$($_.Name)=$($_.ExitCode)" }
        throw "Windows Sandbox package smoke failed: $($result.Error) (steps: $($steps -join ', '))"
    }
    Write-Host 'Windows Sandbox package smoke passed: validate, x64 install, exact query, uninstall, and zero portable residue.' -ForegroundColor Green
}
finally {
    try { Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
