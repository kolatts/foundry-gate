<#
.SYNOPSIS
    Spin the gateway back down. Default keeps the Foundry accounts; -Mode Full removes everything.

.DESCRIPTION
    Stage 6 of the cycle.

    MODE KeepFoundry (the default, and the one to use)
      Deletes every resource in the resource group EXCEPT the
      Microsoft.CognitiveServices/accounts. That removes APIM — the only meaningful idle
      cost in the stack — along with monitoring, while leaving the Foundry accounts and
      their model deployments in place, so the next up.ps1 re-runs the template with
      createModelDeployments=false over surviving accounts.

      This is the default BECAUSE of E-007: Anthropic deployments are create-once per
      account, an account that has churned Claude deployments starts refusing new ones, and
      a fresh account only gets one attempt. Keeping the accounts is what makes "spin up and
      down frequently" survivable — every teardown that destroys them spends another Claude
      create attempt on the next spin-up.

    MODE Full
      Deletes the whole resource group, then purges the soft-deleted APIM service and the
      soft-deleted Cognitive Services accounts so a clean-slate redeploy is not blocked by
      name conflicts.

      *** This spends the account's Claude create attempts. *** Use it when you genuinely
      want a clean slate and accept that the next day-0 may not get a working Claude
      deployment. Everything else should use KeepFoundry.

    Idempotent in both modes: a resource group that is already gone is a no-op, not an error.

.EXAMPLE
    pwsh scripts/cycle/down.ps1

.EXAMPLE
    pwsh scripts/cycle/down.ps1 -Mode Full
#>
[CmdletBinding()]
param(
    [string] $Environment = 'test',
    [string] $Subscription,
    [ValidateSet('KeepFoundry', 'Full')]
    [string] $Mode = 'KeepFoundry',
    [Parameter(HelpMessage = 'Return as soon as the deletes are accepted rather than waiting for them to finish.')]
    [switch] $NoWait,
    [Parameter(HelpMessage = 'Required to tear down a resource group that contains control-plane resources. There is no good reason to pass this.')]
    [switch] $AllowControlPlane
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/_common.ps1"

$state = Get-CycleState -Environment $Environment
if (-not $Subscription) {
    if (-not $state.ContainsKey('subscription')) { throw 'No -Subscription given and no state file to read one from.' }
    $Subscription = $state.subscription
}
$rgName = if ($state.ContainsKey('resourceGroup')) { $state.resourceGroup } else { "rg-foundrygate-$Environment" }

$rg = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @('group', 'show', '--name', $rgName)
if ($null -eq $rg) {
    Write-Host "Resource group $rgName does not exist — nothing to tear down." -ForegroundColor Green
    $state.teardownMode = $Mode
    $state.downCompletedUtc = Get-CycleTimestamp
    Save-CycleState -State $state
    exit 0
}

$resources = @(Invoke-Az -Subscription $Subscription -Arguments @('resource', 'list', '--resource-group', $rgName) | Where-Object { $null -ne $_ })

# REFUSE to tear down a control-plane environment. -Environment is a free string defaulting to
# `test`, and KeepFoundry — the DEFAULT mode, with no confirmation prompt — deletes every
# resource in rg-foundrygate-$Environment that is not Foundry or telemetry. That is correct for
# the gateway-only test environment this tool exists for, and catastrophic one typo later:
# `down.ps1 -Environment dev` would take the SQL server, the Container App and the Static Web
# App with it, because dev.bicepparam sets deployControlPlane = true. Nothing about the name
# "KeepFoundry" warns you about that, so the script checks instead of trusting the name.
$controlPlaneTypes = @(
    'Microsoft.Sql/servers'
    'Microsoft.App/containerApps'
    'Microsoft.Web/staticSites'
    'Microsoft.KeyVault/vaults'
    'Microsoft.ContainerRegistry/registries'
)
$controlPlane = @($resources | Where-Object { $controlPlaneTypes -contains $_.type })
if ($controlPlane.Count -gt 0 -and -not $AllowControlPlane) {
    foreach ($c in $controlPlane) { Write-Host "  $($c.type)/$($c.name)" -ForegroundColor Red }
    throw "$rgName holds $($controlPlane.Count) control-plane resource(s), listed above. scripts/cycle/ is a gateway-only test harness and will not delete a control-plane environment. If you genuinely mean to, pass -AllowControlPlane."
}

if ($Mode -eq 'KeepFoundry') {
    Write-CycleHeading "Tearing down $rgName (KeepFoundry — Foundry accounts and the telemetry stores survive)"

    # What survives, and why each one:
    #   Cognitive Services accounts   Anthropic deployments are create-once per account
    #                                 (E-007); destroying them spends the next spin-up's only
    #                                 Claude create attempt. This is the headline reason.
    #   Log Analytics + App Insights  they hold the billing-grade token logs, and Log
    #                                 Analytics ingestion lags the traffic by longer than a
    #                                 cycle takes — deleting them at teardown destroys the
    #                                 measurement evidence minutes before it arrives, which
    #                                 is exactly what happened on 2026-09-05. Keeping them
    #                                 lets `measure.ps1` be re-run against the same state
    #                                 file after the gateway is gone.
    # Neither bills at rest: Foundry is per-token, the telemetry stores are per ingested GB
    # and a cycle ingests megabytes. APIM, the one real idle cost, always goes.
    $keepTypes = @(
        'Microsoft.CognitiveServices/accounts'
        'Microsoft.OperationalInsights/workspaces'
        'Microsoft.Insights/components'
    )
    $keep = @($resources | Where-Object { $keepTypes -contains $_.type })
    $drop = @($resources | Where-Object { $keepTypes -notcontains $_.type })

    foreach ($k in $keep) { Write-Host "  keep   $($k.type)/$($k.name)" -ForegroundColor Green }
    if ($drop.Count -eq 0) {
        Write-Host '  Nothing to delete — only Foundry accounts remain.' -ForegroundColor Green
    }

    # APIM first and explicitly: it is the expensive one, it takes the longest to delete,
    # and `az resource delete` on an APIM service does not always wait for the service to
    # actually go away. Deleting it first means the cost meter stops at the top of the run.
    $apims = @($drop | Where-Object { $_.type -eq 'Microsoft.ApiManagement/service' })
    foreach ($a in $apims) {
        Write-Host "  delete Microsoft.ApiManagement/service/$($a.name)" -ForegroundColor Yellow
        $args = @('apim', 'delete', '--name', $a.name, '--resource-group', $rgName, '--yes')
        if ($NoWait) { $args += '--no-wait' }
        Invoke-Az -Subscription $Subscription -AllowFailure -Arguments $args | Out-Null

        # AND PURGE IT. Deleting an APIM service only SOFT-deletes it, and the name stays
        # reserved: the very next up.ps1 fails with
        #   ServiceAlreadyExistsInSoftDeletedState: Api service <name> was soft-deleted.
        # which would defeat the entire point of a teardown you can run frequently.
        # Purging APIM has nothing to do with the Anthropic create-once problem — that is
        # about Cognitive Services accounts, which this mode keeps — so there is no reason
        # not to, and every reason to.
        Write-Host "  purge soft-deleted APIM $($a.name)" -ForegroundColor Yellow
        Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
            'apim', 'deletedservice', 'purge', '--service-name', $a.name, '--location', $a.location
        ) | Out-Null
    }

    foreach ($r in @($drop | Where-Object { $_.type -ne 'Microsoft.ApiManagement/service' })) {
        Write-Host "  delete $($r.type)/$($r.name)" -ForegroundColor Yellow
        # Failures are tolerated and reported: some resources are child resources that the
        # parent's deletion already removed, and racing them is not an error.
        $deleted = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @('resource', 'delete', '--ids', $r.id)
        if ($null -eq $deleted -and (Get-LastAzError)) {
            Write-CycleInfo "  (already gone or removed with its parent: $($r.name))"
        }
    }
}
else {
    Write-CycleHeading "Tearing down $rgName (Full — deletes the Foundry accounts too)"
    Write-Host '  WARNING: this spends this subscription/account pair''s Anthropic create attempts (E-007).' -ForegroundColor Red
    Write-Host '  The next day-0 deploy may not get a working Claude deployment. Use -Mode KeepFoundry unless a clean slate is genuinely required.' -ForegroundColor Red

    $cogAccounts = @($resources | Where-Object { $_.type -eq 'Microsoft.CognitiveServices/accounts' } | ForEach-Object { @{ name = $_.name; location = $_.location } })
    $apimNames = @($resources | Where-Object { $_.type -eq 'Microsoft.ApiManagement/service' } | ForEach-Object { @{ name = $_.name; location = $_.location } })

    Invoke-Az -Subscription $Subscription -Arguments @('group', 'delete', '--name', $rgName, '--yes') | Out-Null
    Write-CycleInfo "Resource group $rgName deleted."

    # Both APIM and Cognitive Services soft-delete. Left unpurged, the next deploy fails on
    # a name conflict against a resource that is not visible in the portal.
    foreach ($a in $apimNames) {
        Write-Host "  purge soft-deleted APIM $($a.name)" -ForegroundColor Yellow
        Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
            'apim', 'deletedservice', 'purge', '--service-name', $a.name, '--location', $a.location
        ) | Out-Null
    }
    foreach ($c in $cogAccounts) {
        Write-Host "  purge soft-deleted Cognitive Services account $($c.name)" -ForegroundColor Yellow
        Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
            'cognitiveservices', 'account', 'purge', '--name', $c.name, '--resource-group', $rgName, '--location', $c.location
        ) | Out-Null
    }
}

$state.teardownMode = $Mode
$state.downCompletedUtc = Get-CycleTimestamp
# The gateway is gone, so the keys are dead. Drop them rather than leaving live-looking
# secrets in a file whose whole point is that the next stage can read them.
if ($state.ContainsKey('apimSubscriptions')) { $state.Remove('apimSubscriptions') }
Save-CycleState -State $state

Write-CycleHeading 'Teardown complete'
& "$PSScriptRoot/status.ps1" -Environment $Environment -Subscription $Subscription

# Explicit, so cycle.ps1's $LASTEXITCODE check reflects this stage rather than the last az call.
exit 0
