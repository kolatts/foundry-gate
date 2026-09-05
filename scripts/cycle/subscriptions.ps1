<#
.SYNOPSIS
    Create (or read back) the demo developer keys — APIM subscriptions scoped to tier products.

.DESCRIPTION
    Stage 2 of the cycle. A FoundryGate "developer key" is an APIM subscription scoped to a
    quota TIER PRODUCT (D-013 / #82): the product carries the rendered llm-token-limit
    policy, and the quota counter is keyed on the subscription, so three subscriptions
    against two products is exactly the fixture the enforcement tests need:

      dev-alice   standard   the victim — small TPM and monthly budget, gets 429 then 403
      dev-bob     standard   the control for TPM isolation — same tier, own counter
      dev-carol   power      the control for the monthly quota — different tier, stays 200

    `az apim` has no subscription verb, so this goes through the Management REST API via
    `az rest`.

    EACH CYCLE GETS FRESH SUBSCRIPTION IDS, and that is not cosmetic. The monthly
    `token-quota` counter is keyed on `context.Subscription.Id` and there is no way to reset
    it — a subscription that has spent its budget stays spent until the calendar month rolls
    over. Reusing `dev-alice` across cycles therefore means the SECOND cycle in a month can
    never demonstrate the quota wall (dev-alice starts at 403) and can never demonstrate the
    TPM wall either (she never gets a 200 to burn). So the real APIM ids carry a cycle stamp
    — `dev-alice-202609051530` — while the state file keeps the stable logical names the
    other stages ask for.

    -Reuse keeps whatever ids the state file already holds, for re-running a later stage
    against the counters an earlier one built up.

    Keys land in the gitignored state file and are redacted out of every printed line and
    out of the evidence report. -ShowKeys prints them (local convenience; never in CI).

.EXAMPLE
    pwsh scripts/cycle/subscriptions.ps1
#>
[CmdletBinding()]
param(
    [string] $Environment = 'test',
    [string] $Subscription,
    [Parameter(HelpMessage = 'Print the subscription keys. Local use only.')]
    [switch] $ShowKeys,
    [Parameter(HelpMessage = 'Keep the subscription ids already in the state file instead of minting fresh ones.')]
    [switch] $Reuse
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/_common.ps1"

$state = Get-CycleState -Environment $Environment -Required
if (-not $Subscription) { $Subscription = $state.subscription }
$apiVersion = '2024-06-01-preview'

$azSubId = (Invoke-Az -Subscription $Subscription -Arguments @('account', 'show')).id
$apimName = $state.outputs.apimName
$rg = $state.resourceGroup
$apimId = "/subscriptions/$azSubId/resourceGroups/$rg/providers/Microsoft.ApiManagement/service/$apimName"

# name -> tier product. The names are also the display names, so a human opening the APIM
# blade during a cycle sees who is who.
$developers = [ordered]@{
    'dev-alice' = 'standard'
    'dev-bob'   = 'standard'
    'dev-carol' = 'power'
}

Write-CycleHeading "Provisioning developer keys on $apimName"

if (-not $state.ContainsKey('apimSubscriptions') -or $null -eq $state.apimSubscriptions) {
    $state.apimSubscriptions = @{}
}

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmm')

foreach ($name in $developers.Keys) {
    $product = $developers[$name]

    $existing = $state.apimSubscriptions[$name]
    if ($Reuse -and $null -ne $existing -and $existing.ContainsKey('subscriptionId')) {
        $subscriptionId = [string]$existing.subscriptionId
        Write-CycleInfo "reusing $name -> $subscriptionId"
    }
    else {
        $subscriptionId = "$name-$stamp"
    }

    $body = @{
        properties = @{
            scope       = "$apimId/products/$product"
            displayName = "FoundryGate cycle demo — $name ($product)"
            state       = 'active'
            # allowTracing is deliberately left default: tracing writes request bodies to
            # the APIM trace, which is exactly the prompt content this gateway should not
            # be storing.
        }
    } | ConvertTo-Json -Depth 6 -Compress

    $bodyFile = Join-Path ([System.IO.Path]::GetTempPath()) "fg-sub-$subscriptionId.json"
    Set-Content -Path $bodyFile -Value $body -Encoding utf8
    try {
        Invoke-Az -Subscription $Subscription -Arguments @(
            'rest', '--method', 'put',
            '--url', "https://management.azure.com$apimId/subscriptions/$($subscriptionId)?api-version=$apiVersion",
            '--body', "@$bodyFile"
        ) | Out-Null
    }
    finally {
        Remove-Item -Path $bodyFile -Force -ErrorAction SilentlyContinue
    }

    $secrets = Invoke-Az -Subscription $Subscription -Arguments @(
        'rest', '--method', 'post',
        '--url', "https://management.azure.com$apimId/subscriptions/$subscriptionId/listSecrets?api-version=$apiVersion"
    )

    $state.apimSubscriptions[$name] = @{
        product        = $product
        subscriptionId = $subscriptionId
        primaryKey     = $secrets.primaryKey
        scope          = "$apimId/products/$product"
    }
    $shown = if ($ShowKeys) { $secrets.primaryKey } else { "$($secrets.primaryKey.Substring(0,4))… (redacted)" }
    Write-Host "  $name -> $subscriptionId, product '$product', key $shown" -ForegroundColor Green
}

Save-CycleState -State $state

# ---- Wait for the keys to become usable ------------------------------------------
# A subscription that the Management API has already created is NOT immediately accepted by
# the gateway: the key has to propagate to the gateway nodes first. Observed live — smoke.ps1
# started seconds after this script returned and the first three checks using the newest key
# came back "401 Access denied due to invalid subscription key" while the same key worked a
# minute later.
#
# The probe asks for a model no tier allows, so it costs nothing and consumes no quota: 403
# means the key authenticated and reached the policy chain, which is exactly what the next
# stage needs. 401 means it is still propagating.
Write-CycleHeading 'Waiting for the keys to propagate to the gateway'
$openaiUrl = $state.outputs.openaiApiUrl
$probeBody = @{ model = 'fg-propagation-probe'; max_tokens = 1; messages = @(@{ role = 'user'; content = 'x' }) } | ConvertTo-Json -Compress
$deadline = (Get-Date).AddSeconds(180)

foreach ($name in $developers.Keys) {
    $key = $state.apimSubscriptions[$name].primaryKey
    $ready = $false
    while (-not $ready -and (Get-Date) -lt $deadline) {
        $probe = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers @{ 'api-key' = $key } -Body $probeBody
        if ($probe.StatusCode -ne 401) { $ready = $true; break }
        Start-Sleep -Seconds 5
    }
    if ($ready) {
        Write-Host "  $name ready" -ForegroundColor Green
    }
    else {
        Write-Host "  $name still 401 after 180s — the next stage will fail on it." -ForegroundColor Red
    }
}

Add-CycleCheck -State $state -Id 'SUB-1' -Name 'Developer keys issued against tier products' -Status 'PASS' `
    -Detail ("dev-alice=standard, dev-bob=standard, dev-carol=power on {0}" -f $apimName)

Write-CycleInfo "Keys stored in $(Get-CycleStatePath -Environment $Environment) (gitignored)."

# Explicit, so cycle.ps1's $LASTEXITCODE check reflects this stage rather than the last az call.
exit 0
