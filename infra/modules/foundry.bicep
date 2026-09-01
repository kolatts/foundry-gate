// One Azure AI Foundry (AIServices) account + its model deployments.
// Deployments are chained sequentially — Azure serializes deployment writes per account.
param accountName string
param location string
param modelDeployments array

@description('Required by Azure for Anthropic (Claude) deployments: { industry, organizationName, countryCode }. Ignored for other model formats.')
param anthropicProviderData object = {}

@description('Create model deployments. Set false on re-runs: Anthropic deployments are create-once — re-PUTing an existing one (even unchanged) fails with Conflict/InternalServerError and drives it to Failed. Ongoing deployment lifecycle belongs to the control plane, not ARM.')
param createModelDeployments bool = true

param tags object = {}

resource account 'Microsoft.CognitiveServices/accounts@2026-07-01' = {
  name: accountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
  }
}

@batchSize(1)
resource deployments 'Microsoft.CognitiveServices/accounts/deployments@2026-07-01' = [
  for d in modelDeployments: if (createModelDeployments) {
    parent: account
    name: d.name
    sku: { name: d.sku, capacity: d.capacity }
    properties: union(
      {
        model: { format: d.format, name: d.model, version: d.version }
      },
      d.format == 'Anthropic' ? { modelProviderData: anthropicProviderData } : {}
    )
  }
]

output accountName string = account.name
output accountId string = account.id
output endpoint string = account.properties.endpoint

