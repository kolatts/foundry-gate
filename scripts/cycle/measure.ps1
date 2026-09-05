<#
.SYNOPSIS
    Prove measurement: wait for the gateway's LLM logs to land, then run the repo's
    reconciliation KQL against them and check the de-duplication assumption.

.DESCRIPTION
    Stage 5 of the cycle. Enforcement is real-time at the gateway (smoke.ps1 proved that);
    this stage proves the OTHER half — that the per-developer token accounting the control
    plane reconciles against is actually there and actually adds up.

    Three things happen:

      M1  poll until ApiManagementGatewayLlmLog has rows. Log Analytics ingestion is not
          instant — the 2026-09-01 validation ended with T6/T7 "pending ingestion" — so this
          polls for up to -TimeoutMinutes rather than asserting once and failing.

      M2  run src/FoundryGate.Functions/Kql/UsageBySubscription.kql verbatim (comment lines
          stripped so it survives being passed as a single CLI argument) and print
          per-subscription totals, next to what this cycle actually spent according to the
          gateway's own quota headers. These should agree to an order of magnitude; exact
          agreement is not expected, because the LLM log is documented to miss tokens on
          broken streams and the KQL totals are therefore a floor.

      M3  check D-017 empirically. The reconciliation KQL collapses the LLM log to one row
          per CorrelationId with max() before summing, on the assumption that a request can
          emit SEVERAL entries carrying repeated per-request counts. This reports whether
          duplicate CorrelationIds actually appear in this workspace, and how far a naive
          sum() would have been off — which is the KQL half of #178.

    Also fetches the FoundryGate App Insights token metric if the extension is present;
    that is best-effort and never fails the stage (dashboards, not billing).

.EXAMPLE
    pwsh scripts/cycle/measure.ps1
#>
[CmdletBinding()]
param(
    [string] $Environment = 'test',
    [int] $TimeoutMinutes = 15,
    [Parameter(HelpMessage = 'How far back to query. Must cover the whole cycle.')]
    [string] $Timespan = 'PT4H'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/_common.ps1"

$state = Get-CycleState -Environment $Environment -Required
$subscription = $state.subscription
$workspaceId = $state.outputs.logAnalyticsWorkspaceCustomerId
$failures = 0

function Assert-Check {
    param([string] $Id, [string] $Name, [bool] $Condition, [string] $Detail)
    Add-CycleCheck -State $state -Id $Id -Name $Name -Status ($Condition ? 'PASS' : 'FAIL') -Detail $Detail
    if (-not $Condition) { $script:failures++ }
}

<#
 Runs a KQL query and returns the rows as an array of hashtables, or $null on failure.
 The query is flattened to a single line: `az ... --analytics-query` takes one argument,
 and a multi-line KQL string does not survive Windows argument parsing intact.
 Flattening means `//` comments must be stripped first, or everything after the first one
 is commented out.
#>
function Invoke-Kql {
    param([Parameter(Mandatory)][string] $Query)
    $flat = (($Query -split "`r?`n" | Where-Object { $_ -notmatch '^\s*//' }) -join ' ') -replace '\s+', ' '
    $result = Invoke-Az -Subscription $subscription -AllowFailure -Arguments @(
        'monitor', 'log-analytics', 'query',
        '--workspace', $workspaceId,
        '--analytics-query', $flat,
        '--timespan', $Timespan
    )
    if ($null -eq $result) { Write-CycleInfo "KQL failed: $(Get-LastAzError)" }
    return $result
}

# `az monitor log-analytics query` lives in an extension, not core az. Installing it on
# demand keeps the cycle runnable on a fresh machine; --only-show-errors and the explicit
# --yes stop it prompting, which would hang an unattended run.
$extensions = @(Invoke-Az -Subscription $subscription -AllowFailure -Arguments @('extension', 'list'))
if (-not ($extensions | Where-Object { $_.name -eq 'log-analytics' })) {
    Write-CycleInfo 'Installing the az log-analytics extension.'
    & az extension add --name log-analytics --yes --only-show-errors 2>$null | Out-Null
}

# ---- M1: wait for ingestion -------------------------------------------------------
Write-CycleHeading "M1 — waiting for ApiManagementGatewayLlmLog rows (workspace $workspaceId)"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$rowCount = 0
$correlationCount = 0
while ((Get-Date) -lt $deadline) {
    $probe = Invoke-Kql -Query 'ApiManagementGatewayLlmLog | summarize Rows = count(), Correlations = dcount(CorrelationId)'
    if ($null -ne $probe -and @($probe).Count -gt 0) {
        $rowCount = [int]$probe[0].Rows
        $correlationCount = [int]$probe[0].Correlations
        if ($rowCount -gt 0) { break }
    }
    Write-CycleInfo 'no rows yet, sleeping 60s'
    Start-Sleep -Seconds 60
}

Assert-Check -Id 'M1' -Name 'ApiManagementGatewayLlmLog rows present in Log Analytics' -Condition ($rowCount -gt 0) `
    -Detail "$rowCount log entries across $correlationCount distinct CorrelationIds within $Timespan"

if ($rowCount -eq 0) {
    Write-Host "  No LLM log rows within $TimeoutMinutes min. Ingestion lag can exceed that; re-run measure.ps1 later against the same state file." -ForegroundColor Yellow
    Add-CycleCheck -State $state -Id 'M2' -Name 'Reconciliation KQL returns per-developer totals' -Status 'SKIP' -Detail 'No rows to reconcile.'
    Add-CycleCheck -State $state -Id 'M3' -Name 'D-017 de-duplication assumption checked against live data' -Status 'SKIP' -Detail 'No rows to inspect.'
    $state.measureCompletedUtc = Get-CycleTimestamp
    Save-CycleState -State $state
    exit ($failures -gt 0 ? 1 : 0)
}

# ---- M2: the repo's reconciliation query ------------------------------------------
Write-CycleHeading 'M2 — reconciliation (src/FoundryGate.Functions/Kql/UsageBySubscription.kql)'
$kqlPath = Join-Path (Get-CycleRepoRoot) 'src' 'FoundryGate.Functions' 'Kql' 'UsageBySubscription.kql'
$usage = Invoke-Kql -Query (Get-Content -Path $kqlPath -Raw)

$usageRows = @()
if ($null -ne $usage) {
    foreach ($row in @($usage)) {
        $usageRows += @{
            apimSubscriptionId = [string]$row.ApimSubscriptionId
            promptTokens       = [int]$row.PromptTokens
            completionTokens   = [int]$row.CompletionTokens
            totalTokens        = [int]$row.TotalTokens
            requestCount       = [int]$row.RequestCount
        }
        Write-Host ("  {0,-24} prompt={1,-8} completion={2,-8} total={3,-8} requests={4}" -f `
                $row.ApimSubscriptionId, $row.PromptTokens, $row.CompletionTokens, $row.TotalTokens, $row.RequestCount)
    }
}
$state.usageBySubscription = $usageRows

Assert-Check -Id 'M2' -Name 'Reconciliation KQL returns per-developer totals' -Condition ($usageRows.Count -gt 0) `
    -Detail (($usageRows | ForEach-Object { "$($_.apimSubscriptionId)=$($_.totalTokens) tokens/$($_.requestCount) req" }) -join '; ')

# Sanity: the standard tier's monthly cap is what smoke.ps1 drove dev-alice into, so the
# busiest subscription's total should be in the same neighbourhood as that cap. Reported,
# not asserted — the LLM log is a floor by design (broken streams lose counts).
$standardQuota = @($state.quotaTiers | Where-Object { $_.name -eq 'standard' })[0].monthlyTokenQuota
if ($usageRows.Count -gt 0) {
    $top = ($usageRows | Sort-Object -Property totalTokens -Descending)[0]
    $ratio = if ($standardQuota -gt 0) { [math]::Round($top.totalTokens / $standardQuota, 2) } else { 0 }
    Write-CycleInfo "Busiest subscription $($top.apimSubscriptionId): $($top.totalTokens) tokens vs the $standardQuota standard-tier cap the gateway enforced (ratio $ratio)."
    $state.measurementRatioToQuota = $ratio
}

# ---- M3: D-017 -------------------------------------------------------------------
Write-CycleHeading 'M3 — D-017: do duplicate CorrelationIds actually occur? (#178)'
$dupe = Invoke-Kql -Query @'
ApiManagementGatewayLlmLog
| summarize Entries = count(), DistinctTotals = dcount(TotalTokens), SumTotals = sum(TotalTokens), MaxTotals = max(TotalTokens) by CorrelationId
| summarize Correlations = count(), MultiEntry = countif(Entries > 1), MaxEntriesPerRequest = max(Entries), NaiveSum = sum(SumTotals), DedupedSum = sum(MaxTotals)
'@

if ($null -ne $dupe -and @($dupe).Count -gt 0) {
    $d = $dupe[0]
    $multi = [int]$d.MultiEntry
    $naive = [long]$d.NaiveSum
    $deduped = [long]$d.DedupedSum
    $inflation = if ($deduped -gt 0) { [math]::Round($naive / $deduped, 3) } else { 0 }
    $state.d017 = @{
        correlations         = [int]$d.Correlations
        multiEntry           = $multi
        maxEntriesPerRequest = [int]$d.MaxEntriesPerRequest
        naiveSum             = $naive
        dedupedSum           = $deduped
        inflationFactor      = $inflation
    }
    $verdict = if ($multi -gt 0) {
        "CONFIRMED: $multi of $($d.Correlations) CorrelationIds carry more than one entry (max $($d.MaxEntriesPerRequest) per request). A naive sum() would report $naive tokens against the de-duplicated $deduped — an inflation factor of $inflation."
    }
    else {
        "NOT EXERCISED on this traffic: every one of $($d.Correlations) CorrelationIds produced exactly one entry, so max() and sum() agree ($deduped tokens). The de-duplication is still correct, but this cycle's request shapes (small, non-streamed) did not produce the chunked multi-entry case the D-017 note is about."
    }
    Add-CycleCheck -State $state -Id 'M3' -Name 'D-017 de-duplication assumption checked against live data' -Status 'PASS' -Detail $verdict
    Write-CycleInfo $verdict
}
else {
    Add-CycleCheck -State $state -Id 'M3' -Name 'D-017 de-duplication assumption checked against live data' -Status 'SKIP' `
        -Detail 'The duplicate-CorrelationId query returned nothing.'
}

# ---- Best-effort: App Insights token metric --------------------------------------
Write-CycleHeading 'App Insights token metric (best effort — dashboards, not billing)'
$aiRows = Invoke-Kql -Query @'
customMetrics
| where name has 'Token'
| summarize Total = sum(valueSum), Count = sum(valueCount) by name
'@
if ($null -ne $aiRows -and @($aiRows).Count -gt 0) {
    foreach ($row in @($aiRows)) { Write-Host ("  {0}: total={1} count={2}" -f $row.name, $row.Total, $row.Count) }
    $state.appInsightsTokenMetrics = @($aiRows | ForEach-Object { @{ name = [string]$_.name; total = [double]$_.Total; count = [double]$_.Count } })
}
else {
    Write-CycleInfo 'No customMetrics rows (the metric flows to Azure Monitor metrics, and App Insights ingestion lags too). Not a failure.'
}

$state.measureCompletedUtc = Get-CycleTimestamp
Save-CycleState -State $state

if ($failures -gt 0) { Write-Host "$failures measurement check(s) FAILED." -ForegroundColor Red; exit 1 }
Write-Host 'Measurement checks complete.' -ForegroundColor Green
exit 0
