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
    [Parameter(HelpMessage = 'How many hours back to query. Must cover the whole cycle.')]
    [int] $LookbackHours = 4
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
 Reads one column out of a KQL result row.

 Deliberately defensive. The row comes back through `az ... --output json` and
 `ConvertFrom-Json -AsHashtable`, and under Set-StrictMode a column that is absent — or
 present under a different casing than the query's alias — is a terminating error, which is
 how this stage died with "The property 'Rows' cannot be found on this object" before
 running anything. A measurement stage must degrade to "no data" rather than crash: the
 whole point of it is to report on a gateway whose logs may not have arrived yet.
#>
function Get-KqlValue {
    param(
        # NOT Mandatory: a Mandatory parameter refuses to bind $null, so the function's own
        # "$null row means no data" branch below was unreachable and an empty result set
        # became a terminating error instead of a zero.
        $Row,
        [Parameter(Mandatory)][string] $Column,
        $Default = 0
    )
    if ($null -eq $Row) { return $Default }
    $keys = if ($Row -is [hashtable] -or $Row -is [System.Collections.IDictionary]) { $Row.Keys } else { $Row.PSObject.Properties.Name }
    foreach ($k in $keys) {
        if ([string]$k -ieq $Column) {
            $v = if ($Row -is [hashtable] -or $Row -is [System.Collections.IDictionary]) { $Row[$k] } else { $Row.$k }
            if ($null -eq $v -or "$v" -eq '') { return $Default }
            return $v
        }
    }
    return $Default
}

<#
 The query window, as an explicit `start/end` pair.

 NOT an ISO-8601 duration. `az monitor log-analytics query --timespan PT4H` silently returns
 a nearly-empty window: verified live, the same query answered 2 rows with `--timespan PT4H`
 and 55 rows over the same data with an explicit range. A measurement stage that quietly
 looks at the wrong four hours is worse than one that fails, so the range is computed here
 and passed unambiguously.
#>
$script:Timespan = '{0}/{1}' -f `
    (Get-Date).ToUniversalTime().AddHours(-$LookbackHours).ToString("yyyy-MM-ddTHH:mm:ssZ"),
    (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

<#
 First row of a KQL result, or $null.

 PowerShell unrolls a single-element array on `return`, so a one-row result arrives as the
 ROW ITSELF rather than an array containing it — and `$result[0]` on a hashtable indexes it
 by the KEY 0, which is absent, yielding $null. That is exactly the shape of the summarize
 queries below, so every one of them silently produced nothing.
#>
function Get-FirstRow {
    param($Result)
    if ($null -eq $Result) { return $null }
    if ($Result -is [hashtable] -or $Result -is [System.Collections.IDictionary]) { return $Result }
    $rows = @($Result)
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

<#
 Runs a KQL query and returns the rows, or $null on failure. The query is flattened to a
 single line: `az ... --analytics-query` takes one argument and a multi-line KQL string does
 not survive Windows argument parsing intact. Flattening means `//` comment lines must be
 stripped first, or everything after the first one would be commented out.
#>
function Invoke-Kql {
    param([Parameter(Mandatory)][string] $Query)
    $flat = (($Query -split "`r?`n" | Where-Object { $_ -notmatch '^\s*//' }) -join ' ') -replace '\s+', ' '
    $result = Invoke-Az -Subscription $subscription -AllowFailure -Arguments @(
        'monitor', 'log-analytics', 'query',
        '--workspace', $workspaceId,
        '--analytics-query', $flat,
        '--timespan', $script:Timespan
    )
    # An empty result set and a failed query both arrive as $null (ConvertFrom-Json of "[]"
    # yields an empty array, which `return` unrolls to nothing), so distinguish them by
    # whether az actually said something on stderr. Reporting "KQL failed" for a query that
    # ran fine and matched no rows sends the reader hunting for a syntax error that is not
    # there.
    if ($null -eq $result) {
        $azErr = Get-LastAzError
        if ([string]::IsNullOrWhiteSpace($azErr)) { Write-CycleInfo 'Query ran and matched no rows.' }
        else { Write-CycleInfo "KQL failed: $azErr" }
    }
    return $result
}

# `az monitor log-analytics query` lives in an extension, not core az. Installing it on
# demand keeps the cycle runnable on a fresh machine; --only-show-errors and the explicit
# --yes stop it prompting, which would hang an unattended run.
# NOT through Invoke-Az: `az extension` is a client-side command group and REJECTS
# --subscription outright ("unrecognized arguments"), which Invoke-Az appends to everything.
# That is how this stage used to die before running a single query.
$extensionNames = @()
$listedJson = (& az extension list --query '[].name' --output json 2>$null) -join ''
if (-not [string]::IsNullOrWhiteSpace($listedJson)) { $extensionNames = @(ConvertFrom-Json $listedJson) }
if ($extensionNames -notcontains 'log-analytics') {
    Write-CycleInfo 'Installing the az log-analytics extension.'
    & az extension add --name log-analytics --yes --only-show-errors 2>$null | Out-Null
}

# ---- M1: wait for ingestion -------------------------------------------------------
Write-CycleHeading "M1 — waiting for ApiManagementGatewayLlmLog rows (workspace $workspaceId)"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$rowCount = 0
$correlationCount = 0
while ((Get-Date) -lt $deadline) {
    $probe = Get-FirstRow (Invoke-Kql -Query 'ApiManagementGatewayLlmLog | summarize Rows = count(), Correlations = dcount(CorrelationId)')
    if ($null -ne $probe) {
        $rowCount = [int](Get-KqlValue -Row $probe -Column 'Rows')
        $correlationCount = [int](Get-KqlValue -Row $probe -Column 'Correlations')
        if ($rowCount -gt 0) { break }
    }
    Write-CycleInfo 'no rows yet, sleeping 60s'
    Start-Sleep -Seconds 60
}

Assert-Check -Id 'M1' -Name 'ApiManagementGatewayLlmLog rows present in Log Analytics' -Condition ($rowCount -gt 0) `
    -Detail "$rowCount log entries across $correlationCount distinct CorrelationIds in $script:Timespan"

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
            apimSubscriptionId = [string](Get-KqlValue -Row $row -Column 'ApimSubscriptionId' -Default '(unknown)')
            promptTokens       = [int](Get-KqlValue -Row $row -Column 'PromptTokens')
            completionTokens   = [int](Get-KqlValue -Row $row -Column 'CompletionTokens')
            totalTokens        = [int](Get-KqlValue -Row $row -Column 'TotalTokens')
            requestCount       = [int](Get-KqlValue -Row $row -Column 'RequestCount')
        }
        Write-Host ("  {0,-24} prompt={1,-8} completion={2,-8} total={3,-8} requests={4}" -f `
                (Get-KqlValue -Row $row -Column 'ApimSubscriptionId' -Default '(unknown)'), (Get-KqlValue -Row $row -Column 'PromptTokens'), (Get-KqlValue -Row $row -Column 'CompletionTokens'), (Get-KqlValue -Row $row -Column 'TotalTokens'), (Get-KqlValue -Row $row -Column 'RequestCount'))
    }
}
$state.usageBySubscription = $usageRows

Assert-Check -Id 'M2' -Name 'Reconciliation KQL returns per-developer totals' -Condition ($usageRows.Count -gt 0) `
    -Detail (($usageRows | ForEach-Object { "$($_.apimSubscriptionId)=$($_.totalTokens) tokens/$($_.requestCount) req" }) -join '; ')

# THE cross-check worth making: dev-alice is the subscription smoke.ps1 drove into the
# standard tier's monthly 403, so her logged total should land just above that cap — the
# gateway stops her *after* the request that crosses it. Comparing the busiest subscription
# instead would compare dev-carol, who is on a different tier with a different cap, and the
# ratio would mean nothing.
# Reported, never asserted: the LLM log is a floor by design (a broken stream loses its
# counts entirely), so an exact match is not the expectation — the right order of magnitude is.
$standardQuota = @($state.quotaTiers | Where-Object { $_.name -eq 'standard' })[0].monthlyTokenQuota
$aliceId = if ($state.ContainsKey('apimSubscriptions') -and $state.apimSubscriptions.ContainsKey('dev-alice')) {
    [string]$state.apimSubscriptions['dev-alice'].subscriptionId
}
else { '' }
$aliceRow = @($usageRows | Where-Object { $_.apimSubscriptionId -eq $aliceId })
if ($aliceRow.Count -gt 0 -and $standardQuota -gt 0) {
    $ratio = [math]::Round($aliceRow[0].totalTokens / $standardQuota, 2)
    $line = "dev-alice ($aliceId) logged $($aliceRow[0].totalTokens) tokens against the $standardQuota standard-tier cap the gateway enforced against her — ratio $ratio."
    Write-CycleInfo $line
    $state.measurementRatioToQuota = $ratio
    $state.measurementCrossCheck = $line
}

# ---- M3: D-017 -------------------------------------------------------------------
Write-CycleHeading 'M3 — D-017: do duplicate CorrelationIds actually occur? (#178)'
$dupe = Invoke-Kql -Query @'
ApiManagementGatewayLlmLog
| summarize Entries = count(), DistinctTotals = dcount(TotalTokens), SumTotals = sum(TotalTokens), MaxTotals = max(TotalTokens) by CorrelationId
| summarize Correlations = count(), MultiEntry = countif(Entries > 1), MaxEntriesPerRequest = max(Entries), NaiveSum = sum(SumTotals), DedupedSum = sum(MaxTotals)
'@

$dupeRow = Get-FirstRow $dupe
if ($null -ne $dupeRow) {
    $d = $dupeRow
    $multi = [int](Get-KqlValue -Row $d -Column 'MultiEntry')
    $naive = [long](Get-KqlValue -Row $d -Column 'NaiveSum')
    $deduped = [long](Get-KqlValue -Row $d -Column 'DedupedSum')
    $inflation = if ($deduped -gt 0) { [math]::Round($naive / $deduped, 3) } else { 0 }
    $state.d017 = @{
        correlations         = [int](Get-KqlValue -Row $d -Column 'Correlations')
        multiEntry           = $multi
        maxEntriesPerRequest = [int](Get-KqlValue -Row $d -Column 'MaxEntriesPerRequest')
        naiveSum             = $naive
        dedupedSum           = $deduped
        inflationFactor      = $inflation
    }
    $verdict = if ($multi -gt 0) {
        "CONFIRMED: $multi of $(Get-KqlValue -Row $d -Column 'Correlations') CorrelationIds carry more than one entry (max $(Get-KqlValue -Row $d -Column 'MaxEntriesPerRequest') per request). A naive sum() would report $naive tokens against the de-duplicated $deduped — an inflation factor of $inflation."
    }
    else {
        "NOT EXERCISED on this traffic: every one of $(Get-KqlValue -Row $d -Column 'Correlations') CorrelationIds produced exactly one entry, so max() and sum() agree ($deduped tokens). The de-duplication is still correct, but this cycle's request shapes (small, non-streamed) did not produce the chunked multi-entry case the D-017 note is about."
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
# AppMetrics, not customMetrics: this workspace uses the workspace-based App Insights schema,
# where the classic `customMetrics` table does not exist and the query fails with
# "Failed to resolve table or column expression named 'customMetrics'".
$aiRows = Invoke-Kql -Query @'
AppMetrics
| where Name has 'Token'
| summarize Total = sum(Sum), Count = sum(ItemCount) by Name
'@
if ($null -ne $aiRows -and @($aiRows).Count -gt 0) {
    foreach ($row in @($aiRows)) { Write-Host ("  {0}: total={1} count={2}" -f (Get-KqlValue -Row $row -Column 'Name' -Default ''), (Get-KqlValue -Row $row -Column 'Total'), (Get-KqlValue -Row $row -Column 'Count')) }
    $state.appInsightsTokenMetrics = @($aiRows | ForEach-Object { @{ name = [string](Get-KqlValue -Row $_ -Column 'Name' -Default ''); total = [double](Get-KqlValue -Row $_ -Column 'Total'); count = [double](Get-KqlValue -Row $_ -Column 'Count') } })
}
else {
    Write-CycleInfo 'No customMetrics rows (the metric flows to Azure Monitor metrics, and App Insights ingestion lags too). Not a failure.'
}

$state.measureCompletedUtc = Get-CycleTimestamp
Save-CycleState -State $state

if ($failures -gt 0) { Write-Host "$failures measurement check(s) FAILED." -ForegroundColor Red; exit 1 }
Write-Host 'Measurement checks complete.' -ForegroundColor Green
exit 0
