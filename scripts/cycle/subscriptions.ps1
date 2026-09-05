<#
.SYNOPSIS
    Create (or read back) the demo developer keys — APIM subscriptions scoped to tier products.

.DESCRIPTION
    Stage 2 of the cycle. A FoundryGate "developer key" is an APIM subscription scoped to a
    quota TIER PRODUCT (D-013 / #82): the product carries the rendered llm-token-limit
    policy, and the quota counter is keyed on the subscription, so three subscriptions
    against two products is exactly the fixture the enforcement tests need:

      alice   standard   the victim — small TPM and monthly budget, gets 429 then 403
      bob     standard   the control for TPM isolation — same tier, own counter
      carol   power      the control for the monthly quota — different tier, stays 200

    `az apim` has no subscription verb, so this goes through the Management REST API via
    `az rest`.

    EVERY SUBSCRIPTION THIS SCRIPT CREATES IS NAMED `fgcycle-*`, and that prefix does real
    work on a SHARED environment. On dev the same APIM service also carries the real
    developer keys the control plane issues as `foundrygate-{UserId}` — so a test fixture has
    to be recognisable at a glance in the APIM blade, has to be excluded from anything that
    reads real usage, and above all has to be deletable without a human deciding key by key
    whether it is safe. `-Cleanup` deletes exactly the `fgcycle-*` subscriptions and nothing
    else, which is only a safe thing to write because the prefix is not shared with anything.

    EACH CYCLE GETS FRESH SUBSCRIPTION IDS, and that is not cosmetic. The monthly
    `token-quota` counter is keyed on `context.Subscription.Id` and there is no way to reset
    it — a subscription that has spent its budget stays spent until the calendar month rolls
    over. Reusing `fgcycle-alice` across cycles therefore means the SECOND cycle in a month
    can never demonstrate the quota wall (alice starts at 403) and can never demonstrate the
    TPM wall either (she never gets a 200 to burn). So the real APIM ids carry a cycle stamp
    — `fgcycle-alice-202609051530` — while the state file keeps the stable logical names the
    other stages ask for (`dev-alice`, `dev-bob`, `dev-carol`).

    -Reuse keeps whatever ids the state file already holds, for re-running a later stage
    against the counters an earlier one built up.

    Keys land in the gitignored state file and are redacted out of every printed line and
    out of the evidence report. -ShowKeys prints them (local convenience; never in CI).

.EXAMPLE
    pwsh scripts/cycle/subscriptions.ps1

.EXAMPLE
    # After a run against a shared environment: remove every fixture key this harness made.
    pwsh scripts/cycle/subscriptions.ps1 -Environment dev -Cleanup
#>
[CmdletBinding()]
param(
    [string] $Environment = 'test',
    [string] $Subscription,
    [Parameter(HelpMessage = 'Print the subscription keys. Local use only.')]
    [switch] $ShowKeys,
    [Parameter(HelpMessage = 'Keep the subscription ids already in the state file instead of minting fresh ones.')]
    [switch] $Reuse,
    [Parameter(HelpMessage = 'Delete every fgcycle-* APIM subscription on this environment and create none.')]
    [switch] $Cleanup,
    [Parameter(HelpMessage = 'Name prefix for the fixture subscriptions. Changing it breaks -Cleanup''s guarantee; there is no good reason to.')]
    [ValidatePattern('^[a-z][a-z0-9-]*$')]
    [string] $Prefix = 'fgcycle'
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

# Logical name -> tier product. The logical names are what every later stage asks the state
# file for; the APIM ids are built from $Prefix below.
$developers = [ordered]@{
    'dev-alice' = 'standard'
    'dev-bob'   = 'standard'
    'dev-carol' = 'power'
}
# 'dev-alice' -> 'alice'. The logical names predate the prefix and are load-bearing in
# smoke.ps1/codex-test.ps1; stripping the old prefix here keeps the APIM id from reading
# `fgcycle-dev-alice-...`, which on the dev environment is actively confusing.
function Get-ShortName { param([string] $Logical) return ($Logical -replace '^dev-', '') }

# ---- Cleanup ---------------------------------------------------------------------
# Deletes by PREFIX, not from the state file: a run that was interrupted, or one from another
# machine, left keys the state file here has never heard of, and those are exactly the ones
# that go on quietly holding a spent monthly counter on a shared gateway.
if ($Cleanup) {
    Write-CycleHeading "Removing $Prefix-* subscriptions from $apimName"
    $all = Invoke-Az -Subscription $Subscription -Arguments @(
        'rest', '--method', 'get',
        '--url', "https://management.azure.com$apimId/subscriptions?api-version=$apiVersion"
    )
    $ours = @(@($all.value) | Where-Object { $_.name -like "$Prefix-*" })
    if ($ours.Count -eq 0) {
        Write-Host "  Nothing to remove — no $Prefix-* subscriptions on $apimName." -ForegroundColor Green
    }
    foreach ($s in $ours) {
        Invoke-Az -Subscription $Subscription -Arguments @(
            'rest', '--method', 'delete',
            '--url', "https://management.azure.com$apimId/subscriptions/$($s.name)?api-version=$apiVersion"
        ) | Out-Null
        Write-Host "  deleted $($s.name)" -ForegroundColor Yellow
    }
    # The keys are dead; leaving them in the state file leaves live-looking secrets behind and
    # invites a later stage to run against subscriptions that no longer exist.
    if ($state.ContainsKey('apimSubscriptions')) { $state.Remove('apimSubscriptions') }
    $state.subscriptionsCleanedUpUtc = Get-CycleTimestamp
    Save-CycleState -State $state
    Write-Host "Removed $($ours.Count) fixture subscription(s)." -ForegroundColor Green
    exit 0
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
        $subscriptionId = "$Prefix-$(Get-ShortName $name)-$stamp"
    }

    $body = @{
        properties = @{
            scope       = "$apimId/products/$product"
            displayName = "FoundryGate cycle fixture — $(Get-ShortName $name) ($product)"
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
$notReady = @()
foreach ($name in $developers.Keys) {
    $key = $state.apimSubscriptions[$name].primaryKey
    $ready = $false
    # Per key, not shared across the loop: a single deadline computed outside would give the
    # first key the whole budget and leave the rest a few seconds each, then report them as
    # "still 401 after 180s" when nothing was wrong with them.
    $deadline = (Get-Date).AddSeconds(180)
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
        $notReady += $name
    }
}

# SUB-1 reflects what actually happened. It used to be an unconditional PASS, which put a
# green check in the evidence report for a stage that had just told the console every key
# was still 401 — the one shape of wrong a check must never have.
Add-CycleCheck -State $state -Id 'SUB-1' -Name 'Developer keys issued against tier products' `
    -Status ($notReady.Count -eq 0 ? 'PASS' : 'FAIL') `
    -Detail $(if ($notReady.Count -eq 0) {
        "dev-alice=standard, dev-bob=standard, dev-carol=power on $apimName; all keys answering"
    }
    else {
        # ${} required: a bare `$apimName:` is parsed as a scoped variable reference.
        "Still 401 after 180s on ${apimName}: $($notReady -join ', ')"
    })

Write-CycleInfo "Keys stored in $(Get-CycleStatePath -Environment $Environment) (gitignored)."

# Explicit, so cycle.ps1's $LASTEXITCODE check reflects this stage rather than the last az call.
exit 0
