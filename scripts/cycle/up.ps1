<#
.SYNOPSIS
    Spin the FoundryGate gateway data plane up in Azure and record its addresses.

.DESCRIPTION
    Stage 1 of the spin-up -> test -> spin-down cycle (see .claude/skills/gateway-cycle).

    Validates the APIM policy documents offline, builds the Bicep, deploys
    infra/main.bicep at subscription scope with the environment's .bicepparam plus two
    kinds of override, and writes every output the later stages need into the state file.

    THE TWO OVERRIDES

      quotaTiers               The `standard` tier is shrunk to numbers a demo can
                               actually reach: `codex exec` burns ~9K tokens per run on
                               its own system prompt (fable-refactor-log, T11), so a 40K
                               monthly budget is exhausted in ~5 runs and an 8K/minute cap
                               is hit inside one or two. `power` and `unlimited` keep the
                               param file's values so the "carol is unaffected" half of
                               the quota test means something.

      createModelDeployments   Anthropic deployments are CREATE-ONCE under ARM (E-007):
                               re-PUTing one drives it to Failed and, after enough churn,
                               poisons Claude creation for the whole account. So this is
                               auto-detected — true only when the resource group holds no
                               Cognitive Services account yet — and every subsequent run
                               passes false. -CreateModelDeployments forces true (day-0 on
                               a fresh account); -SkipClaude deploys no Anthropic models at
                               all, which is the safe choice when a previous cycle in this
                               subscription already burned a Claude create attempt.

    Idempotent: run it against an already-deployed environment and it re-runs the template
    with createModelDeployments=false, which is the supported re-run shape.

.EXAMPLE
    pwsh scripts/cycle/up.ps1 -Subscription "Imagile Paid"

.EXAMPLE
    # Second and later cycles after a KeepFoundry teardown — Foundry accounts survive,
    # so this re-creates APIM/monitoring over them without touching model deployments.
    pwsh scripts/cycle/up.ps1 -Subscription "Imagile Paid"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Subscription,
    [string] $Environment = 'test',
    [string] $Location = 'eastus2',

    [Parameter(HelpMessage = 'Monthly token budget for the standard tier. Small on purpose: the 403 path must be reachable.')]
    [int] $MonthlyTokenQuota = 40000,

    <#
      Tokens-per-minute cap for the standard tier. Small on purpose so the 429 path is
      reachable — but NOT smaller than one `codex exec`, which spends ~9-10K tokens on its
      system prompt before it does anything. Verified live 2026-09-05 at 8000: Codex 429s,
      retries, keeps the bucket empty, never completes a single exec, and therefore never
      spends any of its MONTHLY budget — the harness deadlocks at the TPM wall and the quota
      wall becomes unreachable. 12000 leaves enough headroom for one exec per minute, so a
      standard-tier key hits 429 within a session and 403 after three or four of them.
    #>
    [Parameter(HelpMessage = 'Tokens-per-minute cap for the standard tier. Must exceed one codex exec (~10K) or the harness deadlocks at the 429 wall.')]
    [int] $Tpm = 12000,

    [Parameter(HelpMessage = 'Force createModelDeployments=true. Only valid on a genuinely fresh Foundry account — see E-007.')]
    [switch] $CreateModelDeployments,

    [Parameter(HelpMessage = 'Deploy OpenAI model deployments only. Use when a Claude create attempt has already been spent in this subscription.')]
    [switch] $SkipClaude,

    [Parameter(HelpMessage = 'Run az deployment sub what-if before deploying (adds ~2 min).')]
    [switch] $WhatIf,

    <#
      Deploy NOTHING. Read the addresses, tier limits and model deployments off the
      environment that is already there and write them into the state file, so every later
      stage runs against it unchanged.

      This is how the cycle runs against dev and prod, which CI owns: `Deploy All` re-deploys
      dev on every merge to main, the template needs FG_API_IMAGE and FG_ENTRA_API_CLIENT_ID
      that only the workflow supplies, and a local deploy would fight both. It is implied
      (and cannot be turned off) for those environments.
    #>
    [Parameter(HelpMessage = 'Attach to an already-deployed environment instead of deploying. Implied for dev/prod.')]
    [switch] $AttachOnly,

    [string] $DeploymentName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/_common.ps1"

$repoRoot = Get-CycleRepoRoot
$started = Get-Date
$resourceGroup = "rg-foundrygate-$Environment"
if (-not $DeploymentName) { $DeploymentName = "foundrygate-$Environment" }

$managed = Test-CycleManagedEnvironment -Environment $Environment
if ($managed -and -not $AttachOnly) {
    Write-Host "  '$Environment' is deployed by CI (deploy-all.yml). Attaching to it instead of deploying — -AttachOnly is implied." -ForegroundColor Yellow
    $AttachOnly = $true
}

if (-not $AttachOnly) {
    $paramFile = Join-Path $repoRoot 'infra' 'parameters' "$Environment.bicepparam"
    if (-not (Test-Path $paramFile)) { throw "No parameter file at $paramFile" }
}

$state = Get-CycleState -Environment $Environment
$state.environment = $Environment
$state.subscription = $Subscription
$state.resourceGroup = $resourceGroup
$state.location = $Location
$state.deploymentName = $DeploymentName
if (-not $state.ContainsKey('checks')) { $state.checks = @() }
$state.upStartedUtc = Get-CycleTimestamp -When $started
Save-CycleState -State $state

<#
 Reads every model deployment's REAL provisioning state off every Foundry account, and
 reports whether any Anthropic one is live. Shared by both paths: the template can report
 success while an Anthropic deployment sits in Failed (ARM accepts the create and the
 provider fails it asynchronously, E-007d), and an attached environment has the same
 question to answer before the Claude-dependent checks decide PASS or SKIP.
#>
function Read-ModelDeploymentStates {
    param([Parameter(Mandatory)][string[]] $AccountNames)
    $states = @{}
    $anyClaude = $false
    foreach ($accountName in $AccountNames) {
        $deployments = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
            'cognitiveservices', 'account', 'deployment', 'list',
            '--name', $accountName, '--resource-group', $resourceGroup
        )
        $rows = @()
        foreach ($d in @($deployments)) {
            $row = @{
                name              = $d.name
                provisioningState = $d.properties.provisioningState
                format            = $d.properties.model.format
            }
            $rows += $row
            $ok = $row.provisioningState -eq 'Succeeded'
            Write-Host ("  {0}/{1}: {2} ({3})" -f $accountName, $row.name, $row.provisioningState, $row.format) `
                -ForegroundColor ($ok ? 'Green' : 'Red')
            if ($ok -and $row.format -eq 'Anthropic') { $anyClaude = $true }
        }
        $states[$accountName] = $rows
    }
    return @{ states = $states; claudeAvailable = $anyClaude }
}

# ---- ATTACH ----------------------------------------------------------------------
# Everything below this block deploys. None of it runs when attaching: the environment is
# already there, CI owns it, and the only job here is to learn its addresses and its real
# limits accurately enough that the enforcement checks assert against the right walls.
if ($AttachOnly) {
    Write-CycleHeading "Attaching to the deployed environment '$Environment' (nothing will be deployed)"

    $rg = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @('group', 'show', '--name', $resourceGroup)
    if ($null -eq $rg) { throw "Resource group $resourceGroup does not exist in '$Subscription'. There is nothing to attach to." }

    $deployment = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
        'deployment', 'sub', 'show', '--name', $DeploymentName
    )
    if ($null -eq $deployment) {
        throw "No subscription-scope deployment named '$DeploymentName'. Attaching reads the addresses out of the deployment's OUTPUTS, so this is fatal — check the environment name."
    }
    if ($deployment.properties.provisioningState -ne 'Succeeded') {
        # Not fatal: the outputs of the last successful deploy are still what is running. But
        # it is exactly the thing that explains a confusing result later, so say it loudly.
        Write-Host "  WARNING: deployment $DeploymentName is $($deployment.properties.provisioningState). Attaching to the outputs it last recorded." -ForegroundColor Yellow
    }

    $outputs = @{}
    foreach ($key in $deployment.properties.outputs.Keys) {
        $outputs[$key] = $deployment.properties.outputs[$key].value
    }
    $state.outputs = $outputs
    $state.attached = $true
    $state.deploymentProvisioningState = [string]$deployment.properties.provisioningState

    $apimName = [string]$outputs.apimName
    $azSubId = (Invoke-Az -Subscription $Subscription -Arguments @('account', 'show')).id
    $apimId = "/subscriptions/$azSubId/resourceGroups/$resourceGroup/providers/Microsoft.ApiManagement/service/$apimName"

    $apimState = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
        'apim', 'show', '--name', $apimName, '--resource-group', $resourceGroup, '--query', 'provisioningState'
    )
    if ($apimState -ne 'Succeeded') { throw "APIM $apimName is '$apimState', not Succeeded. Wait for CI's deploy to finish before running the cycle against it." }

    # ---- The tiers, from the policies the gateway is actually running ----------------
    Write-CycleHeading 'Reading the tier limits off the live product policies'
    $tiers = @()
    foreach ($productId in @($outputs.productIds)) {
        $tier = Get-CycleTierFromPolicy -Subscription $Subscription -ApimResourceId $apimId -ProductId $productId
        $burn = Get-CycleQuotaBurnMinutes -Tier $tier
        $burnText = [double]::IsPositiveInfinity($burn) ? 'no monthly quota' : "$burn min at full rate to exhaust"
        Write-Host ("  {0,-12} {1,10} tokens/month  {2,8} TPM   ({3})" -f $tier.name, $tier.monthlyTokenQuota, $tier.tpm, $burnText)
        $tiers += $tier
    }
    $state.quotaTiers = $tiers

    # ---- #244 regression guard -------------------------------------------------------
    # Without logAnalyticsDestinationType='Dedicated' the gateway's LLM rows go to the legacy
    # AzureDiagnostics catch-all and ApiManagementGatewayLlmLog stays empty forever while
    # every resource reports healthy. Reading it here turns a four-hour "ingestion lag" hunt
    # in measure.ps1 into one line at attach time.
    Write-CycleHeading 'Diagnostic setting destination type (#244)'
    $listed = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
        'monitor', 'diagnostic-settings', 'list', '--resource', $apimId
    )
    # Three shapes to survive, and getting any of them wrong throws under Set-StrictMode:
    #   $null            -AllowFailure swallowed an az failure. `@($null)` is a ONE-element
    #                    array containing $null, so an unguarded Where-Object on it throws
    #                    ("property cannot be found") and aborts the whole attach — and if it
    #                    didn't, the count below would report "1 diagnostic setting(s)" for an
    #                    environment that returned none.
    #   a bare array     what az returns today.
    #   { value: [...] } the ARM envelope az has also returned across versions.
    $settings = if ($null -eq $listed) { @() }
    elseif ($listed -is [System.Collections.IDictionary] -and $listed.ContainsKey('value')) { @($listed.value) }
    else { @($listed) }
    $dedicated = @($settings | Where-Object { $_.logAnalyticsDestinationType -eq 'Dedicated' })
    $llmCategories = @($settings | ForEach-Object { @($_.logs) } | Where-Object { $_.category -eq 'GatewayLlmLogs' -and $_.enabled })
    Add-CycleCheck -State $state -Id 'UP-3' -Name 'APIM diagnostic setting sends LLM logs to the dedicated table (#244)' `
        -Status (($dedicated.Count -gt 0 -and $llmCategories.Count -gt 0) ? 'PASS' : 'FAIL') `
        -Detail ("{0} diagnostic setting(s), {1} with logAnalyticsDestinationType=Dedicated, {2} with GatewayLlmLogs enabled" -f `
            $settings.Count, $dedicated.Count, $llmCategories.Count)

    Write-CycleHeading 'Model deployment states'
    $models = Read-ModelDeploymentStates -AccountNames @($outputs.foundryAccountNames)
    $state.modelDeployments = $models.states
    $state.claudeAvailable = $models.claudeAvailable

    $state.createModelDeploymentsUsed = $false
    $state.skipClaude = $true
    $state.upCompletedUtc = Get-CycleTimestamp
    $state.upElapsedSeconds = [math]::Round(((Get-Date) - $started).TotalSeconds)
    $state.deployElapsedSeconds = 0
    Save-CycleState -State $state

    Add-CycleCheck -State $state -Id 'UP-1' -Name 'Gateway deployed' -Status 'PASS' `
        -Detail "Attached to the existing '$Environment' deployment $DeploymentName ($($outputs.apimGatewayUrl)); nothing was deployed."
    $claudeDetail = if ($models.claudeAvailable) {
        'At least one Anthropic deployment in Succeeded.'
    }
    else {
        "No Anthropic deployment on '$Environment'. This run made NO create attempt — attaching never deploys a model (#231, E-007). Claude-dependent checks report SKIP."
    }
    Add-CycleCheck -State $state -Id 'UP-2' -Name 'Anthropic (Claude) deployment provisioned' `
        -Status ($models.claudeAvailable ? 'PASS' : 'SKIP') -Detail $claudeDetail

    Write-CycleHeading "Attached to '$Environment'"
    Write-Host "  Gateway URL : $($outputs.apimGatewayUrl)"
    Write-Host "  Anthropic   : $($outputs.anthropicApiUrl)"
    Write-Host "  OpenAI      : $($outputs.openaiApiUrl)"
    Write-Host "  APIM        : $apimName"
    Write-Host "  Products    : $(@($outputs.productIds) -join ', ')"
    Write-Host "  Workspace   : $($outputs.logAnalyticsWorkspaceName) ($($outputs.logAnalyticsWorkspaceCustomerId))"
    Write-Host "  State file  : $(Get-CycleStatePath -Environment $Environment)"
    exit 0
}

# ---- 1. Offline policy validation ------------------------------------------------
# Cheap, and it is the only thing that catches a malformed policy before a 10-minute
# deployment fails on it.
Write-CycleHeading 'Validating APIM policy documents'
& (Join-Path $repoRoot 'scripts' 'validate-policies.ps1')
if ($LASTEXITCODE -ne 0) { throw 'scripts/validate-policies.ps1 failed — fix the policies before deploying.' }

Write-CycleHeading 'Building Bicep'
& az bicep build --file (Join-Path $repoRoot 'infra' 'main.bicep') --stdout --only-show-errors | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'az bicep build failed.' }
Write-CycleInfo 'infra/main.bicep compiles.'

# ---- 2. Day-0 detection ----------------------------------------------------------
Write-CycleHeading 'Determining createModelDeployments'
$existingAccounts = @()
$rg = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @('group', 'show', '--name', $resourceGroup)
if ($null -ne $rg) {
    $accounts = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
        'cognitiveservices', 'account', 'list', '--resource-group', $resourceGroup
    )
    if ($null -ne $accounts) { $existingAccounts = @($accounts | ForEach-Object { $_.name }) }
}

if ($CreateModelDeployments) {
    $create = $true
    Write-CycleInfo 'Forced true by -CreateModelDeployments.'
}
elseif ($existingAccounts.Count -gt 0) {
    $create = $false
    Write-CycleInfo "Foundry account(s) already present ($($existingAccounts -join ', ')) — re-run shape, createModelDeployments=false."
}
else {
    $create = $true
    Write-CycleInfo 'No Foundry account in the resource group — this is day 0, createModelDeployments=true.'
}

if ($create -and -not $SkipClaude) {
    Write-Host '  NOTE: this run will make its ONE Anthropic create attempt (E-007). It is never retried.' -ForegroundColor Yellow
}

# ---- 2b. Make sure the APIM name is actually free --------------------------------
# Two different ways a previous teardown blocks this deploy, and they need different waits:
#
#   still deleting  the service is mid-transition and ARM answers `ServiceLocked: The API
#                   Service ... is transitioning at this time`. Only time fixes it.
#   soft-deleted    the delete finished but the NAME stays reserved, and ARM answers
#                   `ServiceAlreadyExistsInSoftDeletedState`. Only a purge fixes it.
#
# A teardown immediately followed by a spin-up — which is the normal shape of "run the cycle
# again" — hits the first, then the second. So: wait out any transition, then purge.
$apimName = "apim-foundrygate-$Environment-$((Select-String -Path $paramFile -Pattern "nameSuffix\s*=\s*'([^']+)'").Matches[0].Groups[1].Value)"

$transitionDeadline = (Get-Date).AddMinutes(15)
while ((Get-Date) -lt $transitionDeadline) {
    $existing = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
        'apim', 'show', '--name', $apimName, '--resource-group', $resourceGroup, '--query', 'provisioningState'
    )
    # Absent (the usual case) or already Succeeded: nothing to wait for.
    if ($null -eq $existing -or $existing -eq 'Succeeded') { break }
    Write-CycleInfo "APIM $apimName is $existing — waiting for it to settle before deploying."
    Start-Sleep -Seconds 30
}

# Log Analytics soft-deletes too, and a same-name redeploy does NOT reliably recover it: the
# workspace create appears to succeed while the APIM diagnostic setting that depends on it
# fails with `ResourceNotFound: The resource .../workspaces/log-foundrygate-test doesn't
# exist`. Recovering it explicitly first is deterministic. (Since KeepFoundry now keeps the
# workspace, this is the recovery path for an environment torn down with -Mode Full or by an
# older script — the same role the APIM purge plays above.)
$workspaceName = "log-foundrygate-$Environment"
$deletedWorkspaces = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
    'monitor', 'log-analytics', 'workspace', 'list-deleted-workspaces', '--query', "[?name=='$workspaceName']"
)
if ($null -ne $deletedWorkspaces -and @($deletedWorkspaces).Count -gt 0) {
    Write-CycleHeading "Recovering soft-deleted Log Analytics workspace $workspaceName"
    Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
        # --workspace-name, not --name: this command spells it differently from every other
        # `az monitor log-analytics workspace` verb.
        'monitor', 'log-analytics', 'workspace', 'recover',
        '--resource-group', $resourceGroup, '--workspace-name', $workspaceName
    ) | Out-Null
    Write-CycleInfo 'Recovered.'
}

$softDeleted = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
    'apim', 'deletedservice', 'list', '--query', "[?name=='$apimName']"
)
if ($null -ne $softDeleted -and @($softDeleted).Count -gt 0) {
    Write-CycleHeading "Purging soft-deleted APIM $apimName (it would block this deploy)"
    Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
        'apim', 'deletedservice', 'purge', '--service-name', $apimName, '--location', $Location
    ) | Out-Null
    Write-CycleInfo 'Purged.'
}

# ---- 3. Parameter overrides ------------------------------------------------------
# quotaTiers: standard shrunk to demo size, power/unlimited left generous so the
# "other tiers keep working while alice is blocked" assertions are meaningful.
# Descriptions stay 7-bit ASCII: they travel through cmd.exe's argument parser on the way
# to az, which mangles anything outside the console code page.
$quotaTiers = @(
    @{ name = 'standard'; displayName = 'Standard'; description = "Cycle demo tier - $MonthlyTokenQuota tokens/month, $Tpm TPM, small so 429/403 are reachable."; monthlyTokenQuota = $MonthlyTokenQuota; tpm = $Tpm }
    # The harness tier. TPM is deliberately generous and the MONTHLY budget deliberately
    # small, which is the only shape in which an agent harness can reach the monthly wall:
    # a tight TPM cap starves codex before it finishes an exec, so it never spends a monthly
    # budget at all (#237). Standard, above, is the opposite shape and is what proves the
    # TPM wall. Between them the two tiers cover both meters with real traffic.
    @{ name = 'power'; displayName = 'Power'; description = 'Cycle demo tier - generous TPM, small monthly budget, so a real harness can reach the 403.'; monthlyTokenQuota = 60000; tpm = 100000 }
    @{ name = 'unlimited'; displayName = 'Unlimited'; description = 'Cycle demo tier - no native monthly quota, TPM smoothing only.'; monthlyTokenQuota = 0; tpm = 100000 }
)
$quotaTiersJson = Format-AzJsonArg -Value $quotaTiers -AsArray

$deployArgs = @(
    'deployment', 'sub', 'create'
    '--name', $DeploymentName
    '--location', $Location
    '--template-file', (Join-Path $repoRoot 'infra' 'main.bicep')
    '--parameters', $paramFile
    '--parameters', "createModelDeployments=$($create.ToString().ToLowerInvariant())"
    '--parameters', "quotaTiers=$quotaTiersJson"
)
if ($SkipClaude) {
    # An empty pooled list means the template creates no Anthropic deployment anywhere.
    # The alias map still lists sonnet/haiku, so those aliases resolve and then 404 at the
    # backend — smoke.ps1 marks the Anthropic checks SKIPPED rather than FAIL on that.
    $deployArgs += @('--parameters', "pooledModelDeployments=$(Format-AzJsonArg -Value @() -AsArray)")
}

if ($WhatIf) {
    Write-CycleHeading 'what-if'
    # Swap the verb rather than slicing $deployArgs by index. The previous form took
    # $deployArgs[3..] and re-appended --name/--location, which both duplicated those two
    # arguments and silently broke the moment anyone reordered $deployArgs above.
    $whatIfArgs = @('deployment', 'sub', 'what-if') + $deployArgs[3..($deployArgs.Count - 1)]
    Invoke-Az -Subscription $Subscription -Raw -Arguments $whatIfArgs | Write-Host
}

# ---- 4. Deploy -------------------------------------------------------------------
Write-CycleHeading "Deploying $DeploymentName (APIM v2 provisioning usually dominates: 5-10 min)"
Write-CycleInfo "standard tier = $MonthlyTokenQuota tokens/month, $Tpm TPM"
$deployStarted = Get-Date
$result = $null

# Retry the DEPLOYMENT on the two APIM-name races, because neither is reliably visible
# before the attempt. A just-deleted service reports ResourceNotFound to `az apim show`
# while ARM still considers the name to be transitioning, so a pre-flight poll cannot see
# what is about to reject the deploy:
#   ServiceLocked                          "is transitioning at this time" — wait, retry.
#   ServiceAlreadyExistsInSoftDeletedState the delete finished and reserved the name —
#                                          purge, then retry.
# Everything else fails on the first attempt: a retry loop around real deployment errors
# would just burn ten minutes of APIM provisioning per attempt to reach the same failure.
for ($attempt = 1; $attempt -le 5; $attempt++) {
    $result = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments $deployArgs
    if ($null -ne $result) { break }

    $azError = Get-LastAzError
    if ($azError -match 'ServiceAlreadyExistsInSoftDeletedState') {
        Write-CycleInfo "Attempt ${attempt}: the APIM name is held by a soft-deleted service. Purging and retrying."
        Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
            'apim', 'deletedservice', 'purge', '--service-name', $apimName, '--location', $Location
        ) | Out-Null
        continue
    }
    if ($azError -match 'ServiceLocked') {
        Write-CycleInfo "Attempt ${attempt}: APIM is still transitioning. Waiting 90s and retrying."
        Start-Sleep -Seconds 90
        continue
    }
    break
}

if ($null -eq $result) {
    # The deployment itself failed. Surface the operation-level errors — the top-level
    # message is almost always a useless "at least one resource deployment operation failed".
    Write-Host "Deployment failed. az reported:" -ForegroundColor Red
    Write-Host (Get-LastAzError) -ForegroundColor Red
    Write-Host 'Failed operations:' -ForegroundColor Red
    Invoke-Az -Subscription $Subscription -Raw -AllowFailure -Arguments @(
        'deployment', 'operation', 'sub', 'list', '--name', $DeploymentName,
        '--query', "[?properties.provisioningState=='Failed'].{resource:properties.targetResource.resourceName,type:properties.targetResource.resourceType,code:properties.statusMessage.error.code,message:properties.statusMessage.error.message}"
    ) | Write-Host
    throw "Deployment $DeploymentName failed. See the failed operations above."
}
$deployElapsed = (Get-Date) - $deployStarted
Write-CycleInfo ("Deployment succeeded in {0:mm\:ss}." -f $deployElapsed)

# ---- 5. Capture outputs ----------------------------------------------------------
$outputs = @{}
foreach ($key in $result.properties.outputs.Keys) {
    $outputs[$key] = $result.properties.outputs[$key].value
}

$state.outputs = $outputs
$state.quotaTiers = $quotaTiers
$state.createModelDeploymentsUsed = $create
$state.skipClaude = [bool]$SkipClaude
$state.upCompletedUtc = Get-CycleTimestamp
$state.upElapsedSeconds = [math]::Round(((Get-Date) - $started).TotalSeconds)
$state.deployElapsedSeconds = [math]::Round($deployElapsed.TotalSeconds)

# ---- 6. OpenAI create-if-missing on the primary account --------------------------
# Narrow, deliberate exception to "ARM owns day-0 deployments". A re-run passes
# createModelDeployments=false to protect the ANTHROPIC deployments (E-007), but that flag
# is all-or-nothing, so an account that came up without its OpenAI deployment — an
# interrupted day-0, a hand-deleted deployment — can never get one back from the template.
# OpenAI deployments provision synchronously and reliably in the same accounts where
# Anthropic ones are fragile (E-007e), so creating a MISSING one out of band is safe.
# This never touches an existing deployment and never runs for format=Anthropic.
$primaryAccount = @($outputs.foundryAccountNames)[0]
$existingDeployments = @()
$listed = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
    'cognitiveservices', 'account', 'deployment', 'list', '--name', $primaryAccount, '--resource-group', $resourceGroup
)
if ($null -ne $listed) { $existingDeployments = @($listed | ForEach-Object { $_.name }) }

foreach ($m in @(@{ name = 'gpt-4-1-mini'; model = 'gpt-4.1-mini'; version = '2025-04-14'; sku = 'GlobalStandard'; capacity = 10 })) {
    if ($existingDeployments -contains $m.name) { continue }
    Write-CycleHeading "Creating missing OpenAI deployment $($m.name) on $primaryAccount"
    Invoke-Az -Subscription $Subscription -Arguments @(
        'cognitiveservices', 'account', 'deployment', 'create',
        '--name', $primaryAccount, '--resource-group', $resourceGroup,
        '--deployment-name', $m.name,
        '--model-name', $m.model, '--model-version', $m.version, '--model-format', 'OpenAI',
        '--sku-name', $m.sku, '--sku-capacity', [string]$m.capacity
    ) | Out-Null
    Write-CycleInfo "Created $($m.name)."
}

# ---- 7. Model deployment reality check -------------------------------------------
# The template can report success while an Anthropic deployment sits in Failed: ARM
# accepts the create and the provider fails it asynchronously (E-007d). Read the real
# provisioning state off every account so later stages know whether Claude exists.
Write-CycleHeading 'Model deployment states'
$models = Read-ModelDeploymentStates -AccountNames @($outputs.foundryAccountNames)
$claudeOk = $models.claudeAvailable
$state.modelDeployments = $models.states
$state.claudeAvailable = $claudeOk

if (-not $claudeOk) {
    # Deliberately NOT retried and NOT deleted/recreated (E-007c: churn poisons the
    # account for every later attempt). Recorded, and the cycle continues on OpenAI.
    Write-Host '  No Anthropic deployment reached Succeeded. Recording and continuing on the OpenAI path.' -ForegroundColor Yellow
    Write-Host '  DO NOT retry or delete/recreate the Claude deployment (E-007).' -ForegroundColor Yellow
    $failedOps = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @(
        'deployment', 'operation', 'sub', 'list', '--name', $DeploymentName,
        '--query', "[?properties.provisioningState=='Failed'].properties.statusMessage"
    )
    if ($null -ne $failedOps) { $state.claudeFailureDetail = ($failedOps | ConvertTo-Json -Depth 8 -Compress) }
}

Save-CycleState -State $state

Add-CycleCheck -State $state -Id 'UP-1' -Name 'Gateway deployed' -Status 'PASS' `
    -Detail ("{0} in {1:mm\:ss}, createModelDeployments={2}" -f $outputs.apimGatewayUrl, $deployElapsed, $create)
Add-CycleCheck -State $state -Id 'UP-2' -Name 'Anthropic (Claude) deployment provisioned' `
    -Status ($claudeOk ? 'PASS' : 'FAIL') `
    -Detail ($claudeOk ? 'At least one Anthropic deployment in Succeeded.' : 'No Anthropic deployment reached Succeeded — E-007. Not retried by design.')

Write-CycleHeading 'Gateway is up'
Write-Host "  Gateway URL : $($outputs.apimGatewayUrl)"
Write-Host "  Anthropic   : $($outputs.anthropicApiUrl)"
Write-Host "  OpenAI      : $($outputs.openaiApiUrl)"
Write-Host "  APIM        : $($outputs.apimName)"
Write-Host "  Products    : $(@($outputs.productIds) -join ', ')"
Write-Host "  Workspace   : $($outputs.logAnalyticsWorkspaceName) ($($outputs.logAnalyticsWorkspaceCustomerId))"
Write-Host "  State file  : $(Get-CycleStatePath -Environment $Environment)"

# Explicit, because cycle.ps1 reads $LASTEXITCODE to decide whether this stage passed. Without
# it, $LASTEXITCODE still holds whatever the last `az` call inside this script left behind.
exit 0
