# Validate-DocumentationFacts.ps1
# Fails when user-facing repository facts drift from their authoritative source files.
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [int]$DiscoveredTestCount = -1
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
} else {
    $RepoRoot = Resolve-Path $RepoRoot
}
$RepoRoot = $RepoRoot.ToString()

$failures = New-Object System.Collections.Generic.List[string]

function Relative([string]$Path) {
    $Path.Substring($RepoRoot.Length).TrimStart(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Read-Required([string]$RelativePath) {
    $full = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        $failures.Add("${RelativePath}: required source file is missing")
        return $null
    }
    return Get-Content -Raw -LiteralPath $full
}

$readme = Read-Required 'README.md'
$propsText = Read-Required 'Directory.Build.props'
$globalJsonText = Read-Required 'global.json'
$registryText = Read-Required 'src/NVMeDriverPatcher.Cli/CliCommandRegistry.cs'

# Command count comes from the canonical descriptor array, not aliases or switch arms.
$commandCount = 0
if ($null -ne $registryText) {
    $allBlock = [regex]::Match(
        $registryText,
        '(?s)public static readonly CliCommandDescriptor\[\] All\s*=\s*\{(?<body>.*?)\n\s*\};')
    if (-not $allBlock.Success) {
        $failures.Add('src/NVMeDriverPatcher.Cli/CliCommandRegistry.cs: could not locate CliCommandRegistry.All')
    } else {
        $commandCount = [regex]::Matches($allBlock.Groups['body'].Value, '(?m)^\s*new\("').Count
        $documented = [regex]::Match($readme, 'Extended CLI \(C# binary[^\r\n]*?(?<count>[0-9]+) commands\)')
        if (-not $documented.Success) {
            $failures.Add('README.md: missing Extended CLI command-count heading')
        } elseif ([int]$documented.Groups['count'].Value -ne $commandCount) {
            $failures.Add("README.md command count '$($documented.Groups['count'].Value)' should be '$commandCount' from CliCommandRegistry.All")
        }
    }
}

# Version and runtime facts come from MSBuild/global.json. Every shipped project must agree.
$canonicalVersion = $null
if ($null -ne $propsText) {
    $match = [regex]::Match($propsText, '<VersionPrefix>(?<value>[^<]+)</VersionPrefix>')
    if ($match.Success) { $canonicalVersion = $match.Groups['value'].Value.Trim() }
    else { $failures.Add('Directory.Build.props: VersionPrefix is missing') }
}

$projectFiles = @(
    'src/NVMeDriverPatcher.Core/NVMeDriverPatcher.Core.csproj',
    'src/NVMeDriverPatcher/NVMeDriverPatcher.csproj',
    'src/NVMeDriverPatcher.Cli/NVMeDriverPatcher.Cli.csproj',
    'src/NVMeDriverPatcher.Tray/NVMeDriverPatcher.Tray.csproj',
    'src/NVMeDriverPatcher.Watchdog/NVMeDriverPatcher.Watchdog.csproj'
)
$targetFrameworks = New-Object System.Collections.Generic.List[string]
foreach ($project in $projectFiles) {
    $text = Read-Required $project
    if ($null -eq $text) { continue }
    $match = [regex]::Match($text, '<TargetFramework>(?<value>[^<]+)</TargetFramework>')
    if (-not $match.Success) {
        $failures.Add("${project}: TargetFramework is missing")
    } else {
        $targetFrameworks.Add($match.Groups['value'].Value.Trim())
    }
}

$targetFramework = $targetFrameworks | Select-Object -First 1
if (($targetFrameworks | Select-Object -Unique).Count -gt 1) {
    $failures.Add("project TargetFramework values disagree: $($targetFrameworks -join ', ')")
}
$runtimeMajor = $null
if ($targetFramework -match '^net(?<major>[0-9]+)\.0-windows') {
    $runtimeMajor = [int]$Matches['major']
} elseif ($null -ne $targetFramework) {
    $failures.Add("project runtime target '$targetFramework' is not the expected netN.0-windows form")
}

if ($null -ne $globalJsonText -and $null -ne $runtimeMajor) {
    try {
        $sdk = ($globalJsonText | ConvertFrom-Json).sdk.version
        $sdkMajor = [int]($sdk.ToString().Split('.')[0])
        if ($sdkMajor -ne $runtimeMajor) {
            $failures.Add("global.json SDK '$sdk' does not match project runtime major '$runtimeMajor'")
        }
    } catch {
        $failures.Add("global.json: invalid SDK metadata: $($_.Exception.Message)")
    }
}

if ($null -ne $readme) {
    $versionBadge = [regex]::Match($readme, 'Version-(?<value>[0-9]+\.[0-9]+\.[0-9]+)-')
    if ($null -ne $canonicalVersion -and
        (-not $versionBadge.Success -or $versionBadge.Groups['value'].Value -ne $canonicalVersion)) {
        $actual = if ($versionBadge.Success) { $versionBadge.Groups['value'].Value } else { '(missing)' }
        $failures.Add("README.md version badge '$actual' should be '$canonicalVersion' from Directory.Build.props")
    }

    $runtimeBadge = [regex]::Match($readme, '\.NET-(?<major>[0-9]+)\.0-')
    if ($null -ne $runtimeMajor -and
        (-not $runtimeBadge.Success -or [int]$runtimeBadge.Groups['major'].Value -ne $runtimeMajor)) {
        $actual = if ($runtimeBadge.Success) { $runtimeBadge.Groups['major'].Value } else { '(missing)' }
        $failures.Add("README.md .NET badge major '$actual' should be '$runtimeMajor' from project TargetFramework")
    }
}

# Discover actual xUnit cases unless a deterministic count was supplied by a validator test.
$testFloor = $null
if ($null -ne $readme) {
    $floorMatch = [regex]::Match(
        $readme,
        'Automated verification[^\r\n]*?(?<floor>[0-9][0-9,]*)\+ discovered test cases',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $floorMatch.Success) {
        $failures.Add('README.md: missing Automated verification discovered-test floor')
    } else {
        $testFloor = [int]($floorMatch.Groups['floor'].Value.Replace(',', ''))
    }
}

if ($DiscoveredTestCount -lt 0) {
    $testProject = Join-Path $RepoRoot 'tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj'
    if (Test-Path -LiteralPath $testProject -PathType Leaf) {
        $output = & dotnet test $testProject --no-restore --list-tests --verbosity quiet 2>&1
        if ($LASTEXITCODE -ne 0) {
            $failures.Add("test discovery failed with exit $LASTEXITCODE")
            $DiscoveredTestCount = 0
        } else {
            $DiscoveredTestCount = @($output | Where-Object {
                $_ -match '^\s+NVMeDriverPatcher\.Tests\.'
            }).Count
        }
    } else {
        $failures.Add('tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj: test project is missing')
        $DiscoveredTestCount = 0
    }
}
if ($null -ne $testFloor -and $DiscoveredTestCount -lt $testFloor) {
    $failures.Add("README.md discovered-test floor '$testFloor' exceeds actual discovery count '$DiscoveredTestCount'")
}

# Every explicit repository path in maintained docs must still resolve. CLAUDE.md is local-only,
# so validate it when present without making a clean release checkout depend on ignored files.
foreach ($docRelative in @('README.md', 'CLAUDE.md')) {
    $docPath = Join-Path $RepoRoot $docRelative
    if (-not (Test-Path -LiteralPath $docPath -PathType Leaf)) { continue }
    $docText = Get-Content -Raw -LiteralPath $docPath
    foreach ($match in [regex]::Matches($docText, '`(?<path>(?:src|tests|scripts|packaging)/[^`\s:*?"<>|]+)`')) {
        $relativePath = $match.Groups['path'].Value.TrimEnd('.', ',', ';')
        $fullPath = Join-Path $RepoRoot ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $fullPath)) {
            $failures.Add("$docRelative repository path '$relativePath' does not exist")
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host 'Documentation fact drift detected:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  FAIL $_" -ForegroundColor Red }
    exit 1
}

Write-Host ("Documentation facts valid: {0} commands; {1} discovered tests (floor {2}); v{3}; {4}; {5} shipped projects." -f
    $commandCount, $DiscoveredTestCount, $testFloor, $canonicalVersion, $targetFramework, $projectFiles.Count) -ForegroundColor Green
exit 0
