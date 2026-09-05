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

    [Parameter(HelpMessage = 'Tokens-per-minute cap for the standard tier. Small on purpose: the 429 path must be reachable.')]
    [int] $Tpm = 8000,

    [Parameter(HelpMessage = 'Force createModelDeployments=true. Only valid on a genuinely fresh Foundry account — see E-007.')]
    [switch] $CreateModelDeployments,

    [Parameter(HelpMessage = 'Deploy OpenAI model deployments only. Use when a Claude create attempt has already been spent in this subscription.')]
    [switch] $SkipClaude,

    [Parameter(HelpMessage = 'Run az deployment sub what-if before deploying (adds ~2 min).')]
    [switch] $WhatIf,

    [string] $DeploymentName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/_common.ps1"

$repoRoot = Get-CycleRepoRoot
$started = Get-Date
$resourceGroup = "rg-foundrygate-$Environment"
$paramFile = Join-Path $repoRoot 'infra' 'parameters' "$Environment.bicepparam"
if (-not (Test-Path $paramFile)) { throw "No parameter file at $paramFile" }
if (-not $DeploymentName) { $DeploymentName = "foundrygate-$Environment" }

$state = Get-CycleState -Environment $Environment
$state.environment = $Environment
$state.subscription = $Subscription
$state.resourceGroup = $resourceGroup
$state.location = $Location
$state.deploymentName = $DeploymentName
if (-not $state.ContainsKey('checks')) { $state.checks = @() }
$state.upStartedUtc = $started.ToUniversalTime().ToString('o')
Save-CycleState -State $state

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

# ---- 3. Parameter overrides ------------------------------------------------------
# quotaTiers: standard shrunk to demo size, power/unlimited left generous so the
# "other tiers keep working while alice is blocked" assertions are meaningful.
# Descriptions stay 7-bit ASCII: they travel through cmd.exe's argument parser on the way
# to az, which mangles anything outside the console code page.
$quotaTiers = @(
    @{ name = 'standard'; displayName = 'Standard'; description = "Cycle demo tier - $MonthlyTokenQuota tokens/month, $Tpm TPM, small so 429/403 are reachable."; monthlyTokenQuota = $MonthlyTokenQuota; tpm = $Tpm }
    @{ name = 'power'; displayName = 'Power'; description = 'Cycle demo tier - larger budget, stays 200 while standard is blocked.'; monthlyTokenQuota = 1000000; tpm = 40000 }
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
    $whatIfArgs = @('deployment', 'sub', 'what-if') + $deployArgs[3..($deployArgs.Count - 1)] + @('--name', $DeploymentName, '--location', $Location)
    Invoke-Az -Subscription $Subscription -Raw -Arguments $whatIfArgs | Write-Host
}

# ---- 4. Deploy -------------------------------------------------------------------
Write-CycleHeading "Deploying $DeploymentName (APIM v2 provisioning usually dominates: 5-10 min)"
Write-CycleInfo "standard tier = $MonthlyTokenQuota tokens/month, $Tpm TPM"
$deployStarted = Get-Date
$result = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments $deployArgs

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
$state.upCompletedUtc = (Get-Date).ToUniversalTime().ToString('o')
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
$modelStates = @{}
$claudeOk = $false
foreach ($accountName in @($outputs.foundryAccountNames)) {
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
        $color = if ($ok) { 'Green' } else { 'Red' }
        Write-Host ("  {0}/{1}: {2} ({3})" -f $accountName, $row.name, $row.provisioningState, $row.format) -ForegroundColor $color
        if ($ok -and $row.format -eq 'Anthropic') { $claudeOk = $true }
    }
    $modelStates[$accountName] = $rows
}
$state.modelDeployments = $modelStates
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
