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
    `az rest`. Idempotent: PUT is create-or-update and the keys are read back with
    listSecrets either way, so re-running against an existing gateway returns the same keys
    rather than rotating them.

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
    [switch] $ShowKeys
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

foreach ($name in $developers.Keys) {
    $product = $developers[$name]
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

    $bodyFile = Join-Path ([System.IO.Path]::GetTempPath()) "fg-sub-$name.json"
    Set-Content -Path $bodyFile -Value $body -Encoding utf8
    try {
        Invoke-Az -Subscription $Subscription -Arguments @(
            'rest', '--method', 'put',
            '--url', "https://management.azure.com$apimId/subscriptions/$($name)?api-version=$apiVersion",
            '--body', "@$bodyFile"
        ) | Out-Null
    }
    finally {
        Remove-Item -Path $bodyFile -Force -ErrorAction SilentlyContinue
    }

    $secrets = Invoke-Az -Subscription $Subscription -Arguments @(
        'rest', '--method', 'post',
        '--url', "https://management.azure.com$apimId/subscriptions/$name/listSecrets?api-version=$apiVersion"
    )

    $state.apimSubscriptions[$name] = @{
        product    = $product
        primaryKey = $secrets.primaryKey
        scope      = "$apimId/products/$product"
    }
    $shown = if ($ShowKeys) { $secrets.primaryKey } else { "$($secrets.primaryKey.Substring(0,4))… (redacted)" }
    Write-Host "  $name -> product '$product', key $shown" -ForegroundColor Green
}

Save-CycleState -State $state
Add-CycleCheck -State $state -Id 'SUB-1' -Name 'Developer keys issued against tier products' -Status 'PASS' `
    -Detail ("dev-alice=standard, dev-bob=standard, dev-carol=power on {0}" -f $apimName)

Write-CycleInfo "Keys stored in $(Get-CycleStatePath -Environment $Environment) (gitignored)."

# Explicit, so cycle.ps1's $LASTEXITCODE check reflects this stage rather than the last az call.
exit 0
