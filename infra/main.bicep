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
@description('Model deployments created in EVERY Foundry account (pooled models). format matches Microsoft.CognitiveServices deployment model.format.')
param pooledModelDeployments array = [
  {
    name: 'claude-haiku-4-5'
    format: 'Anthropic'
    model: 'claude-haiku-4-5'
    version: '20251001'
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

@description('Default per-developer tokens-per-minute cap enforced by APIM llm-token-limit.')
param defaultDeveloperTpm int = 20000

@description('Default per-developer monthly token quota enforced natively by APIM (0 disables the native quota and leaves monthly enforcement to the control plane).')
param defaultDeveloperMonthlyTokenQuota int = 0

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
    foundryAccounts: [
      for (region, i) in foundryRegions: {
        name: foundry[i].outputs.accountName
        endpoint: foundry[i].outputs.endpoint
      }
    ]
    defaultDeveloperTpm: defaultDeveloperTpm
    defaultDeveloperMonthlyTokenQuota: defaultDeveloperMonthlyTokenQuota
  }
  dependsOn: [foundryRbac]
}

// Everything a control plane needs to attach: gateway addresses, the product that
// developer subscriptions scope to, the workspace holding billing-grade token logs,
// and the identities/names for further role assignments.
output apimGatewayUrl string = apim.outputs.gatewayUrl
output apimName string = apim.outputs.apimName
output apimPrincipalId string = apim.outputs.principalId
output anthropicApiUrl string = gateway.outputs.anthropicApiUrl
output openaiApiUrl string = gateway.outputs.openaiApiUrl
output productId string = gateway.outputs.productId
output logAnalyticsWorkspaceId string = monitoring.outputs.workspaceId
output resourceGroupName string = rg.name
output foundryAccountNames array = [for (region, i) in foundryRegions: foundry[i].outputs.accountName]
