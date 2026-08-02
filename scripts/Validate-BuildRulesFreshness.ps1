# Validate-BuildRulesFreshness.ps1
# Release gate for the bundled windows_build_rules.json review dates.
#
# BuildActionPolicyService treats a rule whose lastReviewed is more than
# BuildActionPolicy.DefaultStaleAfterDays (30) days old as stale, and a stale rule is
# verify/rollback-only. Because every bundled rule carries the same review date, the whole ruleset
# expires on the same day -- apply becomes globally blocked on every build with no code change, no
# release, and no user action. That is correct behaviour (these rules describe Microsoft behaviour
# that changes out-of-band, so an unreviewed rule must not authorize a mutation), but it must not
# arrive silently.
#
# So the window stays at 30 days and this gate makes the expiry visible instead: it fails a release
# whose bundled rules are already stale, or would go stale within -WarnWithinDays of shipping.
# Re-verify each rule's sourceUrl against its expectedPath, then refresh the dates.
[CmdletBinding()]
param(
    [string]$RulesPath,
    [string]$RepoRoot,
    # Must match BuildActionPolicy.DefaultStaleAfterDays.
    [ValidateRange(1, 3650)] [int]$StaleAfterDays = 30,
    # A release shipping with less than this much review life left will strand users.
    [ValidateRange(0, 3650)] [int]$WarnWithinDays = 14,
    # Evaluation date; overridable so the gate is testable.
    [datetime]$AsOf = [datetime]::UtcNow
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
if (-not $RulesPath) {
    $RulesPath = Join-Path $RepoRoot 'src\NVMeDriverPatcher.Core\windows_build_rules.json'
}

if (-not (Test-Path -LiteralPath $RulesPath -PathType Leaf)) {
    Write-Host "Build rules not found at '$RulesPath'." -ForegroundColor Red
    exit 1
}

try {
    $rules = Get-Content -Raw -LiteralPath $RulesPath | ConvertFrom-Json
} catch {
    Write-Host "Build rules are not valid JSON: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

if (-not $rules.rules -or @($rules.rules).Count -eq 0) {
    Write-Host 'Build rules contain no rules; every build would be treated as unknown.' -ForegroundColor Red
    exit 1
}

$today = $AsOf.ToUniversalTime().Date
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

foreach ($rule in $rules.rules) {
    $id = if ($rule.id) { $rule.id } else { '(unnamed rule)' }

    $reviewed = [datetime]::MinValue
    $parsed = [datetime]::TryParseExact(
        [string]$rule.lastReviewed, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None, [ref]$reviewed)
    if (-not $parsed) {
        # BuildActionPolicyService treats an unparseable date as stale, so this is already broken.
        $failures.Add("[$id] lastReviewed '$($rule.lastReviewed)' is not a yyyy-MM-dd date; the rule is permanently stale.")
        continue
    }

    $age = ($today - $reviewed.Date).TotalDays
    $daysLeft = $StaleAfterDays - $age

    if ($reviewed.Date -gt $today) {
        $failures.Add("[$id] lastReviewed $($rule.lastReviewed) is in the future.")
    } elseif ($age -gt $StaleAfterDays) {
        $failures.Add("[$id] was last reviewed $($rule.lastReviewed) ($([int]$age) days ago); it is already stale, so apply is blocked on every matching build.")
    } elseif ($daysLeft -lt $WarnWithinDays) {
        $failures.Add("[$id] was last reviewed $($rule.lastReviewed) and goes stale in $([int]$daysLeft) day(s) - inside the $WarnWithinDays-day release window. Re-verify its sourceUrl and refresh the date before shipping.")
    } elseif ($daysLeft -lt ($WarnWithinDays * 2)) {
        $warnings.Add("[$id] goes stale in $([int]$daysLeft) day(s).")
    }

    if (-not $rule.sourceUrl) {
        $failures.Add("[$id] has no sourceUrl, so its verdict cannot be re-verified.")
    }
    if (-not $rule.expectedPath) {
        $failures.Add("[$id] has no expectedPath.")
    }
}

$updated = [datetime]::MinValue
if (-not [datetime]::TryParseExact(
        [string]$rules.updated, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None, [ref]$updated)) {
    $failures.Add("The ruleset 'updated' value '$($rules.updated)' is not a yyyy-MM-dd date.")
} else {
    $newestReview = ($rules.rules |
        ForEach-Object {
            $d = [datetime]::MinValue
            if ([datetime]::TryParseExact([string]$_.lastReviewed, 'yyyy-MM-dd',
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::None, [ref]$d)) { $d }
        } | Sort-Object -Descending | Select-Object -First 1)
    if ($newestReview -and $updated.Date -lt $newestReview.Date) {
        $failures.Add("The ruleset 'updated' date ($($rules.updated)) is older than its newest rule review ($($newestReview.ToString('yyyy-MM-dd'))).")
    }
}

foreach ($warning in $warnings) { Write-Host "WARN  $warning" -ForegroundColor Yellow }

if ($failures.Count -gt 0) {
    Write-Host 'Build-rules freshness gate FAILED:' -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Re-verify each rule against its sourceUrl, correct any verdict that no longer holds,' -ForegroundColor Red
    Write-Host 'then set lastReviewed (and updated) to the review date. Do not refresh the dates blindly.' -ForegroundColor Red
    exit 1
}

Write-Host "Build-rules freshness gate passed: $(@($rules.rules).Count) rule(s), all reviewed within $($StaleAfterDays - $WarnWithinDays) days." -ForegroundColor Green
exit 0
