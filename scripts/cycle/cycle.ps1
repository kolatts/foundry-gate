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
        if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { $ok = $false }
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
