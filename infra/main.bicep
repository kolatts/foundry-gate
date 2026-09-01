// FoundryGate — gateway data plane (subscription scope).
// Provisions: resource group, monitoring, Foundry accounts + model deployments per
// region, APIM (StandardV2), and the AI gateway layer (backends, pools, APIs,
// product, policies). The FoundryGate control-plane app (API/UI/SQL) is provisioned
// separately; this file is deliberately deployable on its own so a fork can stand up
// the data plane first and prove traffic flows before any app code exists.
targetScope = 'subscription'

@description('Deployment environment name, used in resource names (e.g. test, prod).')
param environmentName string

@description('Primary Azure region for shared resources (APIM, monitoring).')
param location string = 'eastus2'

@description('Globally-unique suffix for DNS-named resources (APIM, Foundry accounts).')
param nameSuffix string

@description('APIM publisher email (required by APIM).')
param publisherEmail string

@description('APIM publisher display name.')
param publisherName string = 'FoundryGate'

@description('APIM v2 tier (BasicV2 is the cheapest viable; see modules/apim.bicep).')
param apimSkuName string = 'StandardV2'

@description('Regions to place Foundry accounts in. Two+ regions multiply TPM/RPM headroom via the APIM backend pool.')
param foundryRegions array = ['eastus2', 'swedencentral']

// NOTE on the model-placement contract: these params cover DAY-0 provisioning only
// ("everywhere" or "primary region"). Ongoing model lifecycle — adding models,
// per-model region subsets, capacity changes — is deliberately the control plane's
// job (#60/#64): Anthropic deployments are create-once under ARM (see
// modules/foundry.bicep), so ARM re-runs must not manage them.
// Every Claude model reachable through the alias map must exist in EVERY pool member —
// the Anthropic pool fails a request over to another region on a 429, and a region
// missing the deployment would turn a throttle into a 404. Hence all Claude models are
// pooled and only OpenAI (single-backend) models are primary-only. Model names/versions
// verified against `az cognitiveservices model list` (eastus2 + swedencentral,
// 2026-09-01); capacities sit well inside the subscription's GlobalStandard quota
// (sonnet-4-5 200, haiku-4-5 80, opus-4-5 40 units).
@description('Model deployments created in EVERY Foundry account (pooled models). format matches Microsoft.CognitiveServices deployment model.format.')
param pooledModelDeployments array = [
  {
    name: 'claude-sonnet-4-5'
    format: 'Anthropic'
    model: 'claude-sonnet-4-5'
    version: '20250929'
    sku: 'GlobalStandard'
    capacity: 10
  }
  {
    name: 'claude-haiku-4-5'
    format: 'Anthropic'
    model: 'claude-haiku-4-5'
    version: '20251001'
    sku: 'GlobalStandard'
    capacity: 5
  }
  {
    name: 'claude-opus-4-5'
    format: 'Anthropic'
    model: 'claude-opus-4-5'
    version: '20251101'
    sku: 'GlobalStandard'
    capacity: 5
  }
]

@description('Model deployments created only in the FIRST Foundry account (single-region models).')
param primaryOnlyModelDeployments array = [
  {
    name: 'gpt-4-1-mini'
    format: 'OpenAI'
    model: 'gpt-4.1-mini'
    version: '2025-04-14'
    sku: 'GlobalStandard'
    capacity: 10
  }
]

@description('Required by Azure for Anthropic (Claude) deployments: { industry, organizationName, countryCode }.')
param anthropicProviderData object

// Quota tiers, not per-user quotas: APIM's `token-quota` accepts LITERALS ONLY (policy
// expressions are rejected — "Expression return type 'System.Int32' is not allowed",
// validated live 2026-09-01), so a single policy cannot read a per-developer budget.
// Each tier becomes an APIM product carrying its own rendered llm-token-limit policy,
// and the control plane sets a developer's quota by issuing their APIM subscription
// against the matching tier product (#82).
@description('Quota tiers -> APIM products. Each: { name, displayName, description?, monthlyTokenQuota (0 = no native monthly quota), tpm }.')
param quotaTiers array = [
  {
    name: 'standard'
    displayName: 'Standard'
    description: 'Everyday agent usage. 5M tokens/month, 20K tokens/minute.'
    monthlyTokenQuota: 5000000
    tpm: 20000
  }
  {
    name: 'power'
    displayName: 'Power'
    description: 'Heavy agentic workloads. 20M tokens/month, 40K tokens/minute.'
    monthlyTokenQuota: 20000000
    tpm: 40000
  }
  {
    name: 'unlimited'
    displayName: 'Unlimited'
    description: 'No gateway-enforced monthly budget; burst smoothing only. Monthly oversight is the control plane\'s job.'
    monthlyTokenQuota: 0
    tpm: 100000
  }
]

// The alias map is also the allowlist (#86): aliases are the model names developers put
// in ANTHROPIC_DEFAULT_*_MODEL / Codex `model`, and anything not listed for their tier
// gets 403 model_not_permitted. Deployments rotate underneath by editing these values —
// the gateway module emits them as named values the control plane can PUT without a
// policy redeploy. `pool` is 'anthropic' (multi-region pool) or 'openai'. Deployment
// names must match the *_ModelDeployments params above.
@description('Per-tier model alias maps: { <tier>: { <alias>: { deployment, pool } } }. A tier with no entry permits no models.')
param productModelAliases object = {
  standard: {
    sonnet: { deployment: 'claude-sonnet-4-5', pool: 'anthropic' }
    haiku: { deployment: 'claude-haiku-4-5', pool: 'anthropic' }
    gpt: { deployment: 'gpt-4-1-mini', pool: 'openai' }
  }
  power: {
    sonnet: { deployment: 'claude-sonnet-4-5', pool: 'anthropic' }
    haiku: { deployment: 'claude-haiku-4-5', pool: 'anthropic' }
    gpt: { deployment: 'gpt-4-1-mini', pool: 'openai' }
  }
  unlimited: {
    sonnet: { deployment: 'claude-sonnet-4-5', pool: 'anthropic' }
    haiku: { deployment: 'claude-haiku-4-5', pool: 'anthropic' }
    opus: { deployment: 'claude-opus-4-5', pool: 'anthropic' }
    gpt: { deployment: 'gpt-4-1-mini', pool: 'openai' }
  }
}

@description('Create model deployments (first run). Set false on re-runs — Anthropic deployments are create-once under ARM; see modules/foundry.bicep.')
param createModelDeployments bool = true

// Standard tags on every resource. Scale model: one FoundryGate stack per environment
// per subscription; additional REGIONS scale inside a stack (foundryRegions → pool
// members, all tagged fg-role=foundry); additional SUBSCRIPTIONS scale by deploying
// this same template per subscription (Claude Global Standard quota is pooled
// per-subscription per-model, so extra subscriptions — not extra same-subscription
// deployments — are what multiply Claude headroom). Tags make cross-subscription cost
// and inventory queries uniform: filter on workload + environment + fg-role.
// Short region names for resource naming; unknown regions fall back to the full name.
var regionShortNames = {
  eastus: 'eus'
  eastus2: 'eus2'
  westus: 'wus'
  westus2: 'wus2'
  westus3: 'wus3'
  swedencentral: 'swc'
  westeurope: 'weu'
  northeurope: 'neu'
  uksouth: 'uks'
  japaneast: 'jpe'
  australiaeast: 'aue'
}

var standardTags = {
  workload: 'foundrygate'
  environment: environmentName
  'managed-by': 'bicep'
  repo: 'kolatts/foundry-gate'
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-foundrygate-${environmentName}'
  location: location
  tags: standardTags
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'foundrygate-monitoring'
  scope: rg
  params: {
    environmentName: environmentName
    location: location
    tags: union(standardTags, { 'fg-role': 'monitoring' })
  }
}

// Serialized: concurrent Anthropic deployment creation across regions has been observed
// to fail with InternalServerError (Marketplace attestation appears subscription-serial).
@batchSize(1)
module foundry 'modules/foundry.bicep' = [
  for (region, i) in foundryRegions: {
    name: 'foundrygate-foundry-${region}'
    scope: rg
    params: {
      accountName: 'fg${environmentName}-${nameSuffix}-${regionShortNames[?region] ?? region}'
      location: region
      modelDeployments: i == 0 ? concat(pooledModelDeployments, primaryOnlyModelDeployments) : pooledModelDeployments
      anthropicProviderData: anthropicProviderData
      createModelDeployments: createModelDeployments
      tags: union(standardTags, {
        'fg-role': 'foundry'
        'fg-region-role': i == 0 ? 'primary' : 'pool-member'
      })
    }
  }
]

module apim 'modules/apim.bicep' = {
  name: 'foundrygate-apim'
  scope: rg
  params: {
    apimName: 'apim-foundrygate-${environmentName}-${nameSuffix}'
    location: location
    publisherEmail: publisherEmail
    publisherName: publisherName
    skuName: apimSkuName
    appInsightsId: monitoring.outputs.appInsightsId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    workspaceId: monitoring.outputs.workspaceId
    tags: union(standardTags, { 'fg-role': 'gateway' })
  }
}

// Gateway MI -> Foundry data-plane access (replaces account keys entirely).
module foundryRbac 'modules/foundry-rbac.bicep' = [
  for (region, i) in foundryRegions: {
    name: 'foundrygate-rbac-${region}'
    scope: rg
    params: {
      accountName: foundry[i].outputs.accountName
      apimPrincipalId: apim.outputs.principalId
    }
  }
]

module gateway 'modules/ai-gateway.bicep' = {
  name: 'foundrygate-ai-gateway'
  scope: rg
  params: {
    apimName: apim.outputs.apimName
    // Pool routing (#83) is derived from the foundryRegions order: the first region is
    // priority 1 (normal traffic, co-located with APIM), every later region is
    // priority 2 standing headroom that only takes traffic when priority 1 throttles or
    // its circuit breaker trips. Weight is only meaningful within a priority group.
    foundryAccounts: [
      for (region, i) in foundryRegions: {
        name: foundry[i].outputs.accountName
        endpoint: foundry[i].outputs.endpoint
        priority: i == 0 ? 1 : 2
        weight: 1
      }
    ]
    quotaTiers: quotaTiers
    productModelAliases: productModelAliases
  }
  dependsOn: [foundryRbac]
}

// Everything a control plane needs to attach: gateway addresses, the tier products that
// developer subscriptions scope to, the workspace holding billing-grade token logs,
// and the identities/names for further role assignments.
output apimGatewayUrl string = apim.outputs.gatewayUrl
output apimName string = apim.outputs.apimName
output apimPrincipalId string = apim.outputs.principalId
output anthropicApiUrl string = gateway.outputs.anthropicApiUrl
output openaiApiUrl string = gateway.outputs.openaiApiUrl
output productIds array = gateway.outputs.productIds
output defaultProductId string = gateway.outputs.defaultProductId
output logAnalyticsWorkspaceId string = monitoring.outputs.workspaceId
output resourceGroupName string = rg.name
output foundryAccountNames array = [for (region, i) in foundryRegions: foundry[i].outputs.accountName]
