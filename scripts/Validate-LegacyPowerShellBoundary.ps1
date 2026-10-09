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

# GetCommandName() returns what was typed, so `sp`, `ni` or `rm` would walk a prohibited cmdlet past every
# name check. Names resolve through Get-Alias on the host running this gate, plus the Windows PowerShell 5.1
# aliases the artifact itself runs under (and the mkdir function, which is New-Item -ItemType Directory and
# so creates a key on a registry path) in case the gate host lacks any of them.
$script:CommandAliases = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($alias in @(Get-Alias)) {
    if ($alias.Definition) { $script:CommandAliases[$alias.Name] = $alias.Definition }
}
$windowsPowerShellAliases = @{
    sp = 'Set-ItemProperty'; si = 'Set-Item'; ni = 'New-Item'; mkdir = 'New-Item'; md = 'New-Item'
    ri = 'Remove-Item'; rm = 'Remove-Item'; rmdir = 'Remove-Item'; rd = 'Remove-Item'; del = 'Remove-Item'; erase = 'Remove-Item'
    rp = 'Remove-ItemProperty'; cpp = 'Copy-ItemProperty'; rnp = 'Rename-ItemProperty'; mp = 'Move-ItemProperty'; clp = 'Clear-ItemProperty'
    cli = 'Clear-Item'; cpi = 'Copy-Item'; cp = 'Copy-Item'; copy = 'Copy-Item'; mi = 'Move-Item'; mv = 'Move-Item'; move = 'Move-Item'
    rni = 'Rename-Item'; ren = 'Rename-Item'; saps = 'Start-Process'; start = 'Start-Process'; iex = 'Invoke-Expression'
    icm = 'Invoke-Command'; ii = 'Invoke-Item'; sajb = 'Start-Job'; ndr = 'New-PSDrive'; mount = 'New-PSDrive'
    cd = 'Set-Location'; chdir = 'Set-Location'; sl = 'Set-Location'; pushd = 'Push-Location'
    sal = 'Set-Alias'; nal = 'New-Alias'; ipal = 'Import-Alias'
}
foreach ($entry in $windowsPowerShellAliases.GetEnumerator()) {
    if (-not $script:CommandAliases.ContainsKey($entry.Key)) { $script:CommandAliases[$entry.Key] = $entry.Value }
}

function Resolve-CommandName {
    param([string]$Name)
    if (-not $Name) { return $Name }
    # A module-qualified name (Microsoft.PowerShell.Management\Set-ItemProperty) runs the same cmdlet.
    if ($Name -match '^[A-Za-z][\w.]*\\([^\\/]+)$') { $Name = $Matches[1] }
    for ($hop = 0; $hop -lt 8 -and $script:CommandAliases.ContainsKey($Name); $hop++) {
        $Name = $script:CommandAliases[$Name]
    }
    return $Name
}

function Get-VariableKey {
    param($Variable)
    $path = $Variable.VariablePath.UserPath
    if (-not $path -or $path -match '^env:') { return $null }
    return ($path -replace '^(script|global|local|private|using):', '').ToLowerInvariant()
}

# `$x` is keyed as "x" and `$x.Name` as "x.name", so a member read follows only that member's assignments
# instead of the whole object (the config table also holds the SafeBoot paths removal reads).
function Get-ReferenceKey {
    param($Node)
    if ($Node -is [System.Management.Automation.Language.ConvertExpressionAst]) { $Node = $Node.Child }
    if ($Node -is [System.Management.Automation.Language.VariableExpressionAst]) { return Get-VariableKey $Node }
    if ($Node -is [System.Management.Automation.Language.MemberExpressionAst] -and
        -not ($Node -is [System.Management.Automation.Language.InvokeMemberExpressionAst]) -and
        $Node.Expression -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $Node.Member -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
        $base = Get-VariableKey $Node.Expression
        if ($base) { return "$base.$($Node.Member.Value.ToLowerInvariant())" }
    }
    return $null
}

# Every expression that feeds a variable: assignments, hashtable entries, foreach sources, parameter defaults.
function Get-VariableOriginIndex {
    param($Ast)
    $index = @{}
    $pairs = New-Object System.Collections.Generic.List[object]
    foreach ($node in @($Ast.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.AssignmentStatementAst] -or
        $n -is [System.Management.Automation.Language.ForEachStatementAst] -or
        $n -is [System.Management.Automation.Language.ParameterAst]
    }, $true))) {
        if ($node -is [System.Management.Automation.Language.AssignmentStatementAst]) {
            $key = Get-ReferenceKey $node.Left
            $pairs.Add(@($key, $node.Right))
            $value = $node.Right
            if ($value -is [System.Management.Automation.Language.CommandExpressionAst]) { $value = $value.Expression }
            if ($value -is [System.Management.Automation.Language.ConvertExpressionAst]) { $value = $value.Child }
            if ($key -and $value -is [System.Management.Automation.Language.HashtableAst]) {
                foreach ($kv in $value.KeyValuePairs) {
                    if ($kv.Item1 -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                        $pairs.Add(@("$key.$($kv.Item1.Value.ToLowerInvariant())", $kv.Item2))
                    }
                }
            }
        }
        elseif ($node -is [System.Management.Automation.Language.ForEachStatementAst]) {
            $pairs.Add(@((Get-VariableKey $node.Variable), $node.Condition))
        }
        else {
            $pairs.Add(@((Get-VariableKey $node.Name), $node.DefaultValue))
        }
    }
    foreach ($pair in $pairs) {
        if (-not $pair[0] -or -not $pair[1]) { continue }
        if (-not $index.ContainsKey($pair[0])) { $index[$pair[0]] = New-Object System.Collections.Generic.List[object] }
        $index[$pair[0]].Add($pair[1])
    }
    return $index
}

# The text of a node plus, transitively, every origin of the variables it reads, so
# `$key = 'HKLM:\...'; New-Item $key` is judged by what $key holds rather than by its name.
function Get-ReachableText {
    param($Node, [hashtable]$Index)
    $texts = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    $queue = New-Object System.Collections.Generic.Queue[object]
    $queue.Enqueue($Node)
    while ($queue.Count -gt 0 -and $texts.Count -lt 256) {
        $current = $queue.Dequeue()
        $texts.Add($current.Extent.Text)
        foreach ($variable in @($current.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.VariableExpressionAst]
        }, $true))) {
            $key = Get-VariableKey $variable
            if (-not $key) { continue }
            $parent = $variable.Parent
            if ($parent -is [System.Management.Automation.Language.MemberExpressionAst] -and
                $parent.Expression.Extent.StartOffset -eq $variable.Extent.StartOffset -and
                $parent.Expression.Extent.EndOffset -eq $variable.Extent.EndOffset) {
                $memberKey = Get-ReferenceKey $parent
                if ($memberKey -and $Index.ContainsKey($memberKey)) { $key = $memberKey }
            }
            if ($seen.ContainsKey($key) -or -not $Index.ContainsKey($key)) { continue }
            $seen[$key] = $true
            foreach ($origin in $Index[$key]) { $queue.Enqueue($origin) }
        }
    }
    return ($texts -join "`n")
}

function Get-OwningFunctionName {
    param($Node)
    $owner = $Node.Parent
    while ($owner -and -not ($owner -is [System.Management.Automation.Language.FunctionDefinitionAst])) { $owner = $owner.Parent }
    if ($owner) { return $owner.Name }
    return $null
}

# Functions allowed to call Initialize-EventLogSource without their own -Status guard. The shipped
# artifact needs none: its only call is the top-level `if (-not $Status)` guard.
$script:EventLogInitializerCallers = @()

# True when the call sits in the true branch of `if (-not $Status)` / `if (!$Status)` with no deferred
# script block or unlisted function between them. A function body or script block can be invoked from
# anywhere, so an enclosing guard outside it proves nothing.
function Test-StatusGuardedCall {
    param($Command)
    $child = $Command
    $walker = $Command.Parent
    while ($walker) {
        if ($walker -is [System.Management.Automation.Language.IfStatementAst]) {
            foreach ($clause in $walker.Clauses) {
                $isThisBranch = $clause.Item2.Extent.StartOffset -eq $child.Extent.StartOffset -and
                    $clause.Item2.Extent.EndOffset -eq $child.Extent.EndOffset
                if ($isThisBranch -and $clause.Item1.Extent.Text.Trim() -match '^(-not\s*|!\s*)\$Status$') { return $true }
            }
        }
        if ($walker -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
            return ($script:EventLogInitializerCallers -contains $walker.Name)
        }
        if ($walker -is [System.Management.Automation.Language.ScriptBlockAst] -and $walker.Parent -and
            -not ($walker.Parent -is [System.Management.Automation.Language.FunctionDefinitionAst])) {
            return $false
        }
        $child = $walker
        $walker = $walker.Parent
    }
    return $false
}

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
        'Clear-Item',
        'Remove-EventLog',
        'Limit-EventLog',
        'Enable-PnpDevice',
        'Disable-PnpDevice',
        'Update-PnpDevice',
        'devcon.exe',
        'pnputil.exe'
    )
    # A registry path in what the command reads, directly or through the variables feeding it.
    $registryMarker = '(?i)\bHK(LM|CU|CR|CC|U)\b|HKEY_|Registry::|RegistryPath|SafeBoot'
    $originIndex = Get-VariableOriginIndex -Ast $ast
    $commands = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst]
    }, $true))
    foreach ($command in $commands) {
        $typedName = $command.GetCommandName()
        $name = Resolve-CommandName $typedName
        $text = $command.Extent.Text
        $line = $command.Extent.StartLineNumber
        # `& "$env:SystemRoot\System32\reg.exe"` has no static command name, so fall back to the first element.
        $toolName = if ($name) { $name } elseif ($command.CommandElements.Count -gt 0) { $command.CommandElements[0].Extent.Text.Trim('"', "'") } else { '' }
        $leaf = if ($toolName) { ($toolName -split '[\\/]')[-1] } else { '' }
        $label = if ($typedName -and $typedName -ne $name) { "$name (typed as $typedName)" } else { $toolName }

        # A tool counts whether it is named bare, with .exe, or by full path.
        foreach ($candidate in @($name, $leaf, "$leaf.exe")) {
            if ($candidate -and $prohibitedCommands -contains $candidate) {
                $typedSuffix = if ($typedName -and $typedName -ne $candidate) { " (typed as $typedName)" } else { '' }
                $failures.Add("prohibited mutation command remains reachable at line ${line}: $candidate$typedSuffix")
                break
            }
        }
        # Aliases defined by the artifact itself would hide a command name from every check here.
        if ($name -in @('Set-Alias', 'New-Alias', 'Import-Alias')) {
            $failures.Add("prohibited alias definition remains reachable at line ${line}: $label")
        }
        # Registry key creation is never allowed. Plain filesystem New-Item (working directories) stays legal.
        # Copy, move and rename create the destination key; a registry location makes a relative New-Item a key.
        # The check follows variables and pipeline input, since the registry provider ignores -ItemType.
        if ($name -in @('New-Item', 'Copy-Item', 'Move-Item', 'Rename-Item', 'Set-Location', 'Push-Location')) {
            $scope = if ($command.Parent -is [System.Management.Automation.Language.PipelineAst]) { $command.Parent } else { $command }
            $reachable = Get-ReachableText -Node $scope -Index $originIndex
            if ($reachable -match $registryMarker) {
                if ($name -in @('Set-Location', 'Push-Location')) {
                    $failures.Add("prohibited registry location change remains reachable at line ${line}: $label")
                }
                else {
                    $failures.Add("prohibited registry creation remains reachable at line ${line}: $label")
                }
            }
            if ($name -eq 'New-Item' -and $reachable -match '(?i)\b(alias|function):') {
                $failures.Add("prohibited alias definition remains reachable at line ${line}: $label on the alias: or function: drive")
            }
        }
        # Only a FileSystem drive is allowed; a Registry drive turns any later path into a key path.
        if ($name -eq 'New-PSDrive' -and $text -notmatch '(?i)-PSProvider\s*:?\s*[''"]?FileSystem\b') {
            $failures.Add("prohibited registry drive mapping remains reachable at line ${line}: $label")
        }
        # New-EventLog writes the same HKLM event-log key as CreateEventSource, so it shares its one owner.
        if ($name -eq 'New-EventLog' -and (Get-OwningFunctionName $command) -ne 'Initialize-EventLogSource') {
            $failures.Add("New-EventLog is reachable outside Initialize-EventLogSource at line $line")
        }
        # Removal stays generic and value-aware (Remove-OwnedSafeBootKey). A direct SafeBoot reference is the
        # shape of the old unconditional GUID-key deletion, which took OS-owned values with it.
        if ($name -in @('Remove-Item', 'Remove-ItemProperty') -and $text -match '(?i)SafeBoot') {
            $failures.Add("prohibited direct SafeBoot key removal remains reachable at line ${line}: $label")
        }
        if ($toolName) {
            $isLauncher = $name -in @('Start-Process', 'Invoke-Expression', 'Invoke-Command', 'Invoke-Item', 'Start-Job') -or
                $leaf -match '(?i)^(cmd|powershell|pwsh)(\.exe)?$' -or
                $toolName -match '(?i)^\$env:ComSpec$'
            if ($leaf -match '(?i)^regedit(32)?(\.exe)?$' -or
                ($leaf -match '(?i)^reg(\.exe)?$' -and $text -match '(?i)\b(add|delete|import|copy|restore|load|unload)\b')) {
                $failures.Add("prohibited registry tool call remains reachable at line ${line}: $label")
            }
            elseif ($isLauncher) {
                # `cmd /c reg add`, `Start-Process reg`: any reg/regedit launched through another process.
                # A .reg file name (x.reg) is not the tool, so a leading dot does not count here.
                if ($text -match '(?i)(?<![.\w-])reg(edit)?(32)?(\.exe)?(?![\w-])') {
                    $failures.Add("prohibited registry tool launch remains reachable at line ${line}: $label")
                }
                # Opening a .reg file runs a registry merge.
                if ($name -in @('Start-Process', 'Invoke-Item') -and $text -match '(?i)\.reg\b') {
                    $failures.Add("prohibited registry file import remains reachable at line ${line}: $label")
                }
                # `powershell -Command "Set-ItemProperty ..."` and `iex '...'` hide the command inside a string.
                foreach ($prohibited in $prohibitedCommands) {
                    if ($text -match "(?i)(?<![\w-])$([regex]::Escape($prohibited))(?![\w-])") {
                        $failures.Add("prohibited mutation command launched through $label at line ${line}: $prohibited")
                        break
                    }
                }
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
        if ($memberName -eq 'CreateEventSource' -and (Get-OwningFunctionName $call) -ne 'Initialize-EventLogSource') {
            $failures.Add("CreateEventSource is reachable outside Initialize-EventLogSource at line $line")
        }
    }

    # Initialize-EventLogSource creates an HKLM key, so a pure -Status query must never reach it. Mentioning
    # $Status is not enough (`if ($Status)` is the inverse); the call must sit in the true branch of
    # `if (-not $Status)`, or inside a function named in $script:EventLogInitializerCallers.
    foreach ($command in $commands) {
        if ((Resolve-CommandName $command.GetCommandName()) -ne 'Initialize-EventLogSource') { continue }
        if (-not (Test-StatusGuardedCall $command)) {
            $failures.Add("Initialize-EventLogSource is called at line $($command.Extent.StartLineNumber) without a -Status guard (it must sit in the true branch of if (-not `$Status) because it writes HKLM)")
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
    @{ Name = 'ungated Initialize-EventLogSource'; Body = ''; EventLog = 'Initialize-EventLogSource'; Expect = 'without a -Status guard' },
    @{ Name = 'inverted -Status guard'; Body = ''; EventLog = 'if ($Status) { Initialize-EventLogSource }'; Expect = 'without a -Status guard' },
    @{ Name = 'else branch of the -Status guard'; Body = ''; EventLog = 'if (-not $Status) { } else { Initialize-EventLogSource }'; Expect = 'without a -Status guard' },
    @{ Name = 'Initialize-EventLogSource in an unlisted function'; Body = ''; EventLog = 'function Start-AppLogging { Initialize-EventLogSource }'; Expect = 'without a -Status guard' },
    @{ Name = 'Initialize-EventLogSource in a deferred script block'; Body = ''; EventLog = 'if (-not $Status) { $init = { Initialize-EventLogSource } }'; Expect = 'without a -Status guard' },
    @{ Name = '!$Status guard'; Body = ''; EventLog = 'if (!$Status) { Initialize-EventLogSource }'; Expect = $null },
    @{ Name = 'New-EventLog outside its function'; Body = 'New-EventLog -LogName Application -Source Src'; Expect = 'New-EventLog is reachable outside Initialize-EventLogSource' },
    @{ Name = 'Remove-EventLog'; Body = 'Remove-EventLog -Source Src'; Expect = 'Remove-EventLog' },
    @{ Name = 'Limit-EventLog'; Body = 'Limit-EventLog -LogName Application -MaximumSize 1MB'; Expect = 'Limit-EventLog' },
    @{ Name = 'module-qualified Set-ItemProperty'; Body = "Microsoft.PowerShell.Management\Set-ItemProperty -Path 'HKLM:\SOFTWARE\X' -Name n -Value 1"; Expect = 'Set-ItemProperty' },
    @{ Name = 'pnputil by full path'; Body = '& "$env:SystemRoot\System32\pnputil.exe" /add-driver x.inf /install'; Expect = 'pnputil.exe' },
    @{ Name = 'bare pnputil'; Body = 'pnputil /add-driver x.inf /install'; Expect = 'pnputil.exe' },
    @{ Name = 'Set-Alias to a prohibited cmdlet'; Body = 'Set-Alias -Name setp -Value Set-ItemProperty'; Expect = 'alias definition' },
    @{ Name = 'New-Item on the alias: drive'; Body = 'New-Item -Path alias:setp -Value Set-ItemProperty'; Expect = 'alias definition' },
    @{ Name = 'cmd /c reg add'; Body = 'cmd /c reg add "HKLM\SOFTWARE\X" /v n /d 1 /f'; Expect = 'registry tool launch' },
    @{ Name = 'ComSpec reg add'; Body = '& $env:ComSpec /c "reg add HKLM\SOFTWARE\X /v n /d 1 /f"'; Expect = 'registry tool launch' },
    @{ Name = 'Start-Process reg without .exe'; Body = 'Start-Process -FilePath reg -ArgumentList "add HKLM\X /f"'; Expect = 'registry tool launch' },
    @{ Name = 'saps alias launching reg'; Body = 'saps reg "add HKLM\X /f"'; Expect = 'registry tool launch' },
    @{ Name = 'Invoke-Item on a .reg file'; Body = "Invoke-Item 'C:\kit\NVMe_Remove_Patch.reg'"; Expect = 'registry file import' },
    @{ Name = 'powershell -Command hiding Set-ItemProperty'; Body = 'powershell.exe -NoProfile -Command "Set-ItemProperty -Path HKLM:\X -Name n -Value 1"'; Expect = 'launched through' },
    @{ Name = 'iex hiding Set-ItemProperty'; Body = "iex 'Set-ItemProperty -Path HKLM:\X -Name n -Value 1'"; Expect = 'launched through' },
    @{ Name = 'registry New-PSDrive'; Body = 'New-PSDrive -Name HKX -PSProvider Registry -Root HKEY_LOCAL_MACHINE | Out-Null'; Expect = 'registry drive mapping' },
    @{ Name = 'positional registry New-PSDrive'; Body = 'ndr HKX Registry HKEY_LOCAL_MACHINE | Out-Null'; Expect = 'registry drive mapping' },
    @{ Name = 'FileSystem New-PSDrive'; Body = 'New-PSDrive -Name Kit -PSProvider FileSystem -Root $env:TEMP | Out-Null'; Expect = $null },
    @{ Name = 'New-Item through a variable'; Body = '$key = ''HKLM:\SOFTWARE\X''; New-Item -Path $key -Force | Out-Null'; Expect = 'registry creation' },
    @{ Name = 'New-Item through a chain of variables'; Body = '$hive = ''HKCU:\Software''; $key = "$hive\X"; New-Item -Path $key -Force | Out-Null'; Expect = 'registry creation' },
    @{ Name = 'New-Item through a hashtable member'; Body = '$paths = @{ Target = ''Registry::HKEY_CURRENT_USER\Software\X'' }; New-Item -Path $paths.Target -Force | Out-Null'; Expect = 'registry creation' },
    @{ Name = 'New-Item through a foreach variable'; Body = 'foreach ($key in @(''HKLM:\SOFTWARE\X'')) { New-Item -Path $key -Force | Out-Null }'; Expect = 'registry creation' },
    @{ Name = 'New-Item from pipeline input'; Body = '''HKLM:\SOFTWARE\X'' | New-Item -Force | Out-Null'; Expect = 'registry creation' },
    @{ Name = 'New-Item through a parameter default'; Body = 'function New-Key { param([string]$KeyPath = ''HKLM:\SOFTWARE\X'') New-Item -Path $KeyPath -Force }'; Expect = 'registry creation' },
    @{ Name = 'Copy-Item into the registry'; Body = "Copy-Item -Path 'HKLM:\SOFTWARE\A' -Destination 'HKLM:\SOFTWARE\B' -Recurse"; Expect = 'registry creation' },
    @{ Name = 'Rename-Item in the registry'; Body = "Rename-Item -Path 'HKLM:\SOFTWARE\A' -NewName B"; Expect = 'registry creation' },
    @{ Name = 'Set-Location into the registry'; Body = "Set-Location 'HKLM:\SOFTWARE'; New-Item -Name X -Force | Out-Null"; Expect = 'registry location change' },
    @{ Name = 'filesystem New-Item through a variable'; Body = '$dir = Join-Path $env:TEMP ''kit''; New-Item -Path $dir -ItemType Directory -Force | Out-Null'; Expect = $null },
    @{ Name = 'filesystem New-Item through a config member beside a SafeBoot path'; Body = "`$cfg = @{ WorkingDir = (Join-Path `$env:TEMP 'w'); SafeBootMinimal = '$safeBootKey' }; New-Item -Path `$cfg.WorkingDir -ItemType Directory -Force | Out-Null"; Expect = $null },
    @{ Name = 'read-only reg query'; Body = 'reg query "HKLM\SOFTWARE\X" /v n'; Expect = $null }
)
# Every alias of a watched cmdlet must resolve to it: Get-Alias on this host, plus the 5.1 table above
# (mkdir is a function, so it only resolves through that table).
foreach ($typed in @('sp', 'si', 'cpp', 'rnp', 'mp', 'clp', 'cli')) {
    $definition = Resolve-CommandName $typed
    $fixtures += @{ Name = "alias $typed"; Body = "$typed -Path 'HKLM:\SOFTWARE\X' -Name n"; Expect = "$definition (typed as $typed)" }
}
foreach ($typed in @('ri', 'rm', 'del', 'erase', 'rd', 'rp')) {
    $fixtures += @{ Name = "alias $typed on the SafeBoot key"; Body = "$typed -LiteralPath '$safeBootKey' -Recurse -Force"; Expect = 'SafeBoot key removal' }
}
foreach ($typed in @('ni', 'md', 'mkdir')) {
    $fixtures += @{ Name = "alias $typed with a registry path"; Body = "$typed -Path 'HKLM:\SOFTWARE\X' -Force"; Expect = 'registry creation' }
}
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
