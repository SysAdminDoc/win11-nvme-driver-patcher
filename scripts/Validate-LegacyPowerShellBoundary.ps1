# Validate-LegacyPowerShellBoundary.ps1
# Release gate for the required legacy artifact. The script may inspect and remove old state,
# but it must never regain an enable, reinstall, forced-bind, FeatureStore, or hot-swap path.
[CmdletBinding()]
param(
    [string]$ScriptPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'NVMe_Driver_Patcher.ps1')
)

$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $ScriptPath).Path
$source = [System.IO.File]::ReadAllText($resolved)
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $resolved,
    [ref]$tokens,
    [ref]$parseErrors)

$failures = New-Object System.Collections.Generic.List[string]
foreach ($parseError in @($parseErrors)) {
    $failures.Add("PowerShell parse error at line $($parseError.Extent.StartLineNumber): $($parseError.Message)")
}

$parameterNames = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
foreach ($required in @('Apply', 'Remove', 'Status', 'ExportDiagnostics', 'GenerateVerifyScript', 'ExportRecoveryKit')) {
    if ($parameterNames -notcontains $required) {
        $failures.Add("required legacy parameter is missing: -$required")
    }
}

$functions = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
}, $true))
if ($functions.Name -contains 'Install-NVMePatch') {
    $failures.Add('Install-NVMePatch must not exist in the read/recover-only artifact')
}
foreach ($requiredFunction in @(
    'Test-PatchStatus',
    'Uninstall-NVMePatch',
    'Export-SystemDiagnostics',
    'New-VerificationScript',
    'Export-RecoveryKit'
)) {
    if ($functions.Name -notcontains $requiredFunction) {
        $failures.Add("required read/recovery function is missing: $requiredFunction")
    }
}

$prohibitedCommands = @(
    'New-ItemProperty',
    'Set-ItemProperty',
    'Enable-PnpDevice',
    'Disable-PnpDevice',
    'Update-PnpDevice',
    'devcon.exe',
    'pnputil.exe'
)
$commands = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst]
}, $true))
foreach ($command in $commands) {
    $name = $command.GetCommandName()
    if ($name -and $prohibitedCommands -contains $name) {
        $failures.Add("prohibited mutation command remains reachable at line $($command.Extent.StartLineNumber): $name")
    }
    if ($name -eq 'New-Item' -and $command.Extent.Text -match '(?i)RegistryPath|SafeBoot(Minimal|Network)') {
        $failures.Add("prohibited registry creation remains reachable at line $($command.Extent.StartLineNumber)")
    }
}

$setValueCalls = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
        $node.Member.Extent.Text -eq 'SetValue'
}, $true))
foreach ($call in $setValueCalls) {
    $failures.Add("prohibited SetValue call remains reachable at line $($call.Extent.StartLineNumber)")
}

$adminFunction = $functions | Where-Object Name -eq 'Test-Administrator' | Select-Object -First 1
$earlyApplyGuard = $ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst]
}, $true) | Where-Object {
    (!$adminFunction -or $_.Extent.StartOffset -lt $adminFunction.Extent.StartOffset) -and
    $_.Clauses.Count -gt 0 -and
    $_.Clauses[0].Item1.Extent.Text -match '\$Apply'
} | Select-Object -First 1

if (-not $earlyApplyGuard) {
    $failures.Add('-Apply is not rejected before administrator/elevation logic')
}
else {
    $exitStatements = @($earlyApplyGuard.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.ExitStatementAst]
    }, $true))
    if ($exitStatements.Count -eq 0) {
        $failures.Add('the pre-elevation -Apply guard does not exit nonzero')
    }
    if ($earlyApplyGuard.Extent.Text -notmatch 'MutationRetiredGuidance' -or
        $earlyApplyGuard.Extent.Text -notmatch 'MutationRetiredExitCode') {
        $failures.Add('the pre-elevation -Apply guard does not emit the canonical retirement guidance/exit code')
    }
}

foreach ($requiredText in @(
    'NVMeDriverPatcher.exe',
    'NVMeDriverPatcher.Cli.exe apply --safe',
    '-Status',
    '-Remove',
    '-ExportDiagnostics',
    '-GenerateVerifyScript',
    '-ExportRecoveryKit'
)) {
    if ($source.IndexOf($requiredText, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        $failures.Add("retirement guidance is missing: $requiredText")
    }
}

if ($failures.Count -gt 0) {
    throw "Legacy PowerShell mutation boundary violations:`n - $($failures -join "`n - ")"
}

Write-Host 'Legacy PowerShell boundary check passed: apply/hot-swap is retired; status/removal/recovery exports remain.'
