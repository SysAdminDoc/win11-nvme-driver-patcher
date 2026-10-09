# Validate-LegacyPowerShellBoundary.ps1
# Release gate for the required legacy artifact. The script may inspect and remove old state,
# but it must never regain an enable, reinstall, forced-bind, FeatureStore, or hot-swap path.
# After checking the real artifact it feeds itself one fixture per known defect shape, so a
# pattern that quietly stops matching fails the gate instead of certifying the artifact.
[CmdletBinding()]
param(
    [string]$ScriptPath
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty inside param-block defaults on Windows PowerShell 5.1 (it is only
# populated once binding completes), so the default must be resolved here in the script body.
# Resolving it in the param default made a direct `powershell -File ...` run of this release
# gate crash instead of validating.
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'NVMe_Driver_Patcher.ps1'
}

$resolved = (Resolve-Path -LiteralPath $ScriptPath).Path

function Get-BoundaryFailure {
    param([string]$Source)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput(
        $Source,
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
        'Set-Item',
        'Copy-ItemProperty',
        'Rename-ItemProperty',
        'Move-ItemProperty',
        'Clear-ItemProperty',
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
        $text = $command.Extent.Text
        $line = $command.Extent.StartLineNumber
        if ($name -and $prohibitedCommands -contains $name) {
            $failures.Add("prohibited mutation command remains reachable at line ${line}: $name")
        }
        # Registry key creation is never allowed. Plain filesystem New-Item (working directories) stays legal.
        if ($name -eq 'New-Item' -and $text -match '(?i)\bHKLM\b|\bHKCU\b|HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER|Registry::|RegistryPath|SafeBoot') {
            $failures.Add("prohibited registry creation remains reachable at line $line")
        }
        # Removal stays generic and value-aware (Remove-OwnedSafeBootKey). A direct SafeBoot reference is the
        # shape of the old unconditional GUID-key deletion, which took OS-owned values with it.
        if ($name -in @('Remove-Item', 'Remove-ItemProperty') -and $text -match '(?i)SafeBoot') {
            $failures.Add("prohibited direct SafeBoot key removal remains reachable at line ${line}: $name")
        }
        # `& "$env:SystemRoot\System32\reg.exe"` has no static command name, so fall back to the first element.
        $toolName = if ($name) { $name } elseif ($command.CommandElements.Count -gt 0) { $command.CommandElements[0].Extent.Text.Trim('"', "'") } else { '' }
        if ($toolName) {
            $leaf = ($toolName -split '[\\/]')[-1]
            if ($leaf -match '(?i)^regedit(32)?(\.exe)?$' -or
                ($leaf -match '(?i)^reg(\.exe)?$' -and $text -match '(?i)\b(add|delete|import|copy|restore|load|unload)\b')) {
                $failures.Add("prohibited registry tool call remains reachable at line ${line}: $toolName")
            }
            elseif ($toolName -match '(?i)^(Start-Process|Invoke-Expression|iex|Invoke-Command)$' -and $text -match '(?i)\breg(edit)?(32)?\.exe') {
                $failures.Add("prohibited registry tool launch remains reachable at line ${line}: $name")
            }
        }
    }

    $memberCalls = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst]
    }, $true))
    foreach ($call in $memberCalls) {
        $memberName = $call.Member.Extent.Text
        $line = $call.Extent.StartLineNumber
        if ($memberName -in @('SetValue', 'CreateSubKey')) {
            $failures.Add("prohibited $memberName call remains reachable at line $line")
        }
        # OpenSubKey(path, $true) opens the key writable, which is how a variable-held hive handle gets mutated.
        if ($memberName -eq 'OpenSubKey' -and $call.Arguments.Count -ge 2 -and $call.Arguments[1].Extent.Text -notmatch '^\$false$') {
            $failures.Add("prohibited writable OpenSubKey call remains reachable at line $line")
        }
        # CreateEventSource writes an HKLM event-log key; only Initialize-EventLogSource may do it.
        if ($memberName -eq 'CreateEventSource') {
            $owner = $call.Parent
            while ($owner -and -not ($owner -is [System.Management.Automation.Language.FunctionDefinitionAst])) { $owner = $owner.Parent }
            if (-not $owner -or $owner.Name -ne 'Initialize-EventLogSource') {
                $failures.Add("CreateEventSource is reachable outside Initialize-EventLogSource at line $line")
            }
        }
    }

    # Initialize-EventLogSource creates an HKLM key, so a pure -Status query must never reach it.
    foreach ($command in $commands) {
        if ($command.GetCommandName() -ne 'Initialize-EventLogSource') { continue }
        $guarded = $false
        $walker = $command.Parent
        while ($walker) {
            if ($walker -is [System.Management.Automation.Language.IfStatementAst]) {
                foreach ($clause in $walker.Clauses) {
                    if ($clause.Item1.Extent.Text -match '\$Status\b') { $guarded = $true }
                }
            }
            if ($walker -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $guarded = $true; break }
            $walker = $walker.Parent
        }
        if (-not $guarded) {
            $failures.Add("Initialize-EventLogSource is called at line $($command.Extent.StartLineNumber) without a -Status guard (it writes HKLM)")
        }
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
        if ($Source.IndexOf($requiredText, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $failures.Add("retirement guidance is missing: $requiredText")
        }
    }

    return $failures.ToArray()
}

$failures = New-Object System.Collections.Generic.List[string]
foreach ($failure in @(Get-BoundaryFailure -Source ([System.IO.File]::ReadAllText($resolved)))) {
    $failures.Add($failure)
}

# Self-check: the gate must pass a clean artifact and flag every real defect shape.
$fixtureBase = @'
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Remove,
    [switch]$Status,
    [switch]$ExportDiagnostics,
    [switch]$GenerateVerifyScript,
    [switch]$ExportRecoveryKit
)
$script:MutationRetiredExitCode = 5
$script:MutationRetiredGuidance = "Use NVMeDriverPatcher.exe or NVMeDriverPatcher.Cli.exe apply --safe; retained: -Status -Remove -ExportDiagnostics -GenerateVerifyScript -ExportRecoveryKit"
if ($Apply) {
    [Console]::Error.WriteLine($script:MutationRetiredGuidance)
    exit $script:MutationRetiredExitCode
}
function Test-Administrator { return $true }
function Initialize-EventLogSource {
    [System.Diagnostics.EventLog]::CreateEventSource('Src', 'Application')
}
#EVENTLOG#
function Test-PatchStatus { return $null }
function Uninstall-NVMePatch {
    New-Item -Path (Join-Path $env:TEMP 'x') -ItemType Directory -Force | Out-Null
    Remove-Item -LiteralPath $Path -Recurse -Force
    #BODY#
    return $true
}
function Export-SystemDiagnostics { return $null }
function New-VerificationScript { return $null }
function Export-RecoveryKit { return $null }
'@
$gatedEventLog = 'if (-not $Status) { Initialize-EventLogSource }'
$safeBootKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}'
$fixtures = @(
    @{ Name = 'clean artifact'; Body = ''; Expect = $null },
    @{ Name = 'SafeBoot GUID-key Remove-Item (literal path)'; Body = "Remove-Item -LiteralPath '$safeBootKey' -Recurse -Force"; Expect = 'SafeBoot key removal' },
    @{ Name = 'SafeBoot GUID-key Remove-Item (config property)'; Body = 'Remove-Item -LiteralPath $script:Config.SafeBootMinimal -Recurse -Force'; Expect = 'SafeBoot key removal' },
    @{ Name = 'Set-Item'; Body = "Set-Item -Path 'HKLM:\SOFTWARE\X' -Value 1"; Expect = 'Set-Item' },
    @{ Name = 'Copy-ItemProperty'; Body = "Copy-ItemProperty -Path 'HKLM:\A' -Name n -Destination 'HKLM:\B'"; Expect = 'Copy-ItemProperty' },
    @{ Name = 'Rename-ItemProperty'; Body = "Rename-ItemProperty -Path 'HKLM:\A' -Name n -NewName m"; Expect = 'Rename-ItemProperty' },
    @{ Name = 'reg.exe add by path'; Body = '& "$env:SystemRoot\System32\reg.exe" add "HKLM\SOFTWARE\X" /v n /d 1 /f'; Expect = 'registry tool call' },
    @{ Name = 'bare reg add'; Body = 'reg add "HKLM\SOFTWARE\X" /v n /d 1 /f'; Expect = 'registry tool call' },
    @{ Name = 'regedit /s'; Body = 'regedit /s C:\x.reg'; Expect = 'registry tool call' },
    @{ Name = 'Start-Process reg.exe'; Body = 'Start-Process -FilePath reg.exe -ArgumentList "add HKLM\X"'; Expect = 'registry tool launch' },
    @{ Name = 'HKLM New-Item'; Body = "New-Item -Path 'HKLM:\SOFTWARE\Unrelated\Key' -Force"; Expect = 'registry creation' },
    @{ Name = 'Registry:: New-Item'; Body = "New-Item -Path 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\X' -Force"; Expect = 'registry creation' },
    @{ Name = 'CreateSubKey via variable'; Body = '$k = [Microsoft.Win32.Registry]::LocalMachine; $k.CreateSubKey("SOFTWARE\X") | Out-Null'; Expect = 'CreateSubKey' },
    @{ Name = 'SetValue via variable'; Body = '$k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SOFTWARE\X", $false); $k.SetValue("n", 1)'; Expect = 'SetValue' },
    @{ Name = 'writable OpenSubKey'; Body = '$k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SOFTWARE\X", $true)'; Expect = 'writable OpenSubKey' },
    @{ Name = 'CreateEventSource outside its function'; Body = "[System.Diagnostics.EventLog]::CreateEventSource('Src', 'Application')"; Expect = 'outside Initialize-EventLogSource' },
    @{ Name = 'ungated Initialize-EventLogSource'; Body = ''; EventLog = 'Initialize-EventLogSource'; Expect = 'without a -Status guard' }
)
foreach ($fixture in $fixtures) {
    $eventLog = if ($fixture.ContainsKey('EventLog')) { $fixture.EventLog } else { $gatedEventLog }
    $text = $fixtureBase.Replace('#EVENTLOG#', $eventLog).Replace('#BODY#', $fixture.Body)
    $found = @(Get-BoundaryFailure -Source $text)
    if ($null -eq $fixture.Expect) {
        if ($found.Count -gt 0) {
            $failures.Add("self-check: fixture '$($fixture.Name)' should pass but the gate reported: $($found -join ' | ')")
        }
    }
    elseif (-not ($found | Where-Object { $_ -like "*$($fixture.Expect)*" })) {
        $failures.Add("self-check: the gate did not flag fixture '$($fixture.Name)' (expected '$($fixture.Expect)')")
    }
}

if ($failures.Count -gt 0) {
    throw "Legacy PowerShell mutation boundary violations:`n - $($failures -join "`n - ")"
}

Write-Host 'Legacy PowerShell boundary check passed: apply/hot-swap is retired; status/removal/recovery exports remain.'
