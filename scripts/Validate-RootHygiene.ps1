# Validate-RootHygiene.ps1
# Fails when unsupported release/test leftovers sit in the repository root.
# Keep root-level surfaces limited to the tracked legacy script, icon, docs, and project files.
[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
} else {
    $RepoRoot = Resolve-Path $RepoRoot
}

$allowedRootPowerShell = @(
    'NVMe_Driver_Patcher.ps1'
)

$failures = New-Object System.Collections.Generic.List[string]

foreach ($file in Get-ChildItem -LiteralPath $RepoRoot -File -Force) {
    $reasons = New-Object System.Collections.Generic.List[string]
    $name = $file.Name

    if ($name -like '* - Copy*' -or $name -like '* Copy*') {
        $reasons.Add('duplicate/copy artifact belongs outside the repo root')
    }

    if ($name -match '(?i)backup') {
        $reasons.Add('backup artifact belongs outside the repo root')
    }

    if ($name -match '(?i)^LibreSpot(\.|$)') {
        $reasons.Add('unrelated project artifact')
    }

    if ($file.Extension -ieq '.ps1' -and ($allowedRootPowerShell -notcontains $name)) {
        $reasons.Add('unsupported root PowerShell script; move scripts under scripts/ or packaging/')
    }

    if ($reasons.Count -gt 0) {
        $failures.Add(("{0}: {1}" -f $name, ($reasons -join '; ')))
    }
}

if ($failures.Count -gt 0) {
    Write-Host 'Root hygiene violations detected:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  FAIL $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Root hygiene check passed.' -ForegroundColor Green
exit 0
