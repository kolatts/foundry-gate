<#
.SYNOPSIS
    Run the whole spin-up -> test -> spin-down cycle and write a markdown evidence report.

.DESCRIPTION
    The one command a human or an agent runs. It executes, in order:

      up.ps1             deploy the gateway data plane (APIM v2 dominates: 5-10 min)
      subscriptions.ps1  issue dev-alice / dev-bob / dev-carol keys against tier products
      smoke.ps1          the enforcement matrix: 401, alias 403, TPM 429, quota 403
      codex-test.ps1     real Codex (and Claude Code) harnesses into the same two walls
      measure.ps1        Log Analytics reconciliation + the D-017 de-duplication check
      down.ps1           teardown, KeepFoundry by default

    and then renders validation/<date>-gateway-cycle.md from the state file.

    STAGE FAILURES DO NOT STOP THE CYCLE. A failed smoke check is evidence, not a reason to
    leave APIM running and bill the owner overnight — so every stage's outcome is recorded
    and the run continues to teardown. The script's own exit code is non-zero if any stage
    failed, so CI still notices.

    -Teardown None leaves the gateway up (for interactive debugging). Remember that APIM
    StandardV2 is ~$0.28/hr; scripts/cycle/status.ps1 will remind you.

.EXAMPLE
    pwsh scripts/cycle/cycle.ps1 -Subscription "Imagile Paid"

.EXAMPLE
    pwsh scripts/cycle/cycle.ps1 -Subscription "Imagile Paid" -Teardown None
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Subscription,
    [string] $Environment = 'test',
    [string] $Location = 'eastus2',
    [int] $MonthlyTokenQuota = 40000,
    # Must exceed one codex exec (~10K) or the harness deadlocks at the 429 wall and can
    # never reach the monthly quota — see up.ps1's parameter comment.
    [int] $Tpm = 12000,
    [switch] $CreateModelDeployments,
    [switch] $SkipClaude,
    [Parameter(HelpMessage = 'Run against an already-deployed environment instead of deploying one. Implied (and forced) for dev/prod.')]
    [switch] $AttachOnly,
    [Parameter(HelpMessage = 'Delete the fgcycle-* fixture subscriptions after the run. Use it on a shared environment.')]
    [switch] $CleanupSubscriptions,
    [ValidateSet('KeepFoundry', 'Full', 'None')]
    [string] $Teardown = 'KeepFoundry',
    [Parameter(HelpMessage = 'Skip the harness stage (codex/claude CLIs not installed, or not wanted).')]
    [switch] $SkipHarness,
    [string] $ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/_common.ps1"

$cycleStarted = Get-Date
$stageResults = [ordered]@{}

# On dev/prod the cycle ATTACHES and never tears down. Both are consequences of the same
# fact — CI owns those environments — and both are forced rather than defaulted, because a
# `-Teardown KeepFoundry` typed out of habit against dev would delete its APIM, its SQL
# server and its Container App. down.ps1 refuses independently; this stops the run reaching it.
$managed = Test-CycleManagedEnvironment -Environment $Environment
if ($managed) {
    Write-Host "'$Environment' is a CI-managed environment: attaching to it, and teardown is disabled for this run." -ForegroundColor Yellow
    if ($Teardown -ne 'None') {
        Write-Host "  (-Teardown $Teardown ignored. Use the infra-destroy.yml workflow if you genuinely mean to destroy '$Environment'.)" -ForegroundColor Yellow
        $Teardown = 'None'
    }
}

function Invoke-Stage {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Script,
        [hashtable] $Arguments = @{},
        [switch] $Fatal
    )
    Write-Host ''
    Write-Host ("##### STAGE {0} #####" -f $Name) -ForegroundColor Magenta
    $t0 = Get-Date
    $ok = $true
    try {
        & (Join-Path $PSScriptRoot $Script) @Arguments
        # Null guard FIRST: under Set-StrictMode an unset $LASTEXITCODE is a terminating
        # error, so testing it before the guard meant the guard could never run.
        if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { $ok = $false }
    }
    catch {
        $ok = $false
        Write-Host "  Stage $Name threw: $($_.Exception.Message)" -ForegroundColor Red
    }
    $elapsed = (Get-Date) - $t0
    $stageResults[$Name] = @{ ok = $ok; seconds = [math]::Round($elapsed.TotalSeconds) }
    Write-Host ("##### STAGE {0}: {1} in {2:mm\:ss} #####" -f $Name, ($ok ? 'OK' : 'FAILED'), $elapsed) -ForegroundColor ($ok ? 'Magenta' : 'Red')
    if (-not $ok -and $Fatal) { throw "Stage $Name failed and the rest of the cycle depends on it." }
    return $ok
}

$upArgs = @{
    Subscription      = $Subscription
    Environment       = $Environment
    Location          = $Location
    MonthlyTokenQuota = $MonthlyTokenQuota
    Tpm               = $Tpm
}
if ($CreateModelDeployments) { $upArgs.CreateModelDeployments = $true }
if ($SkipClaude) { $upArgs.SkipClaude = $true }
if ($AttachOnly -or $managed) { $upArgs.AttachOnly = $true }

try {
    # up and subscriptions are fatal: without a gateway and keys there is nothing to test,
    # and the teardown in `finally` still runs.
    Invoke-Stage -Name 'up' -Script 'up.ps1' -Arguments $upArgs -Fatal | Out-Null
    Invoke-Stage -Name 'subscriptions' -Script 'subscriptions.ps1' -Arguments @{ Environment = $Environment } -Fatal | Out-Null

    Invoke-Stage -Name 'smoke' -Script 'smoke.ps1' -Arguments @{ Environment = $Environment } | Out-Null

    if ($SkipHarness) {
        Write-Host 'Harness stage skipped (-SkipHarness).' -ForegroundColor Yellow
    }
    else {
        Invoke-Stage -Name 'harness' -Script 'codex-test.ps1' -Arguments @{ Environment = $Environment } | Out-Null
    }

    Invoke-Stage -Name 'measure' -Script 'measure.ps1' -Arguments @{ Environment = $Environment } | Out-Null
}
finally {
    # BEFORE teardown and before the report, because it is the one piece of cleanup that must
    # happen even when a stage threw: on a shared gateway these fixture keys otherwise sit
    # there holding a spent monthly counter that nothing can reset. Failing to clean up is
    # never allowed to fail the run — the keys are named `fgcycle-*` exactly so a human (or a
    # later `-Cleanup`) can find them.
    if ($CleanupSubscriptions) {
        try {
            # -OnlyThisRun: this fires automatically, and a full prefix sweep would delete a
            # concurrent cycle's freshly minted fixtures out from under it — 401s mid-stage on
            # a shared environment, which is exactly the situation attach mode invites.
            # Orphans from an interrupted run are somebody's later `-Cleanup` without this flag.
            & (Join-Path $PSScriptRoot 'subscriptions.ps1') -Environment $Environment -Subscription $Subscription -Cleanup -OnlyThisRun
        }
        catch {
            Write-Host "  Fixture-subscription cleanup failed: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "  Remove them by hand: pwsh scripts/cycle/subscriptions.ps1 -Environment $Environment -Cleanup" -ForegroundColor Red
        }
    }

    if ($Teardown -eq 'None') {
        Write-Host ''
        Write-Host 'Teardown skipped (-Teardown None). The gateway is STILL UP and APIM is billing.' -ForegroundColor Yellow
        Write-Host 'Run: pwsh scripts/cycle/down.ps1' -ForegroundColor Yellow
    }
    else {
        Invoke-Stage -Name "down ($Teardown)" -Script 'down.ps1' -Arguments @{ Environment = $Environment; Subscription = $Subscription; Mode = $Teardown } | Out-Null
    }

    # ---- Evidence report ----------------------------------------------------------
    $state = Get-CycleState -Environment $Environment
    $state.stageResults = $stageResults
    $state.cycleElapsedSeconds = [math]::Round(((Get-Date) - $cycleStarted).TotalSeconds)
    Save-CycleState -State $state

    if (-not $ReportPath) {
        $validationDir = Join-Path (Get-CycleRepoRoot) 'validation'
        if (-not (Test-Path $validationDir)) { New-Item -ItemType Directory -Path $validationDir -Force | Out-Null }
        $ReportPath = Join-Path $validationDir ("{0:yyyy-MM-dd}-gateway-cycle.md" -f $cycleStarted)
    }

    & (Join-Path $PSScriptRoot 'report.ps1') -Environment $Environment -Path $ReportPath
    Write-Host ''
    Write-Host "Evidence report: $ReportPath" -ForegroundColor Cyan
}

$failedStages = @($stageResults.Keys | Where-Object { -not $stageResults[$_].ok })
if ($failedStages.Count -gt 0) {
    Write-Host "Failed stage(s): $($failedStages -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host 'Cycle complete, all stages OK.' -ForegroundColor Green
exit 0
