// One Azure AI Foundry (AIServices) account + its model deployments.
// Deployments are chained sequentially — Azure serializes deployment writes per account.
param accountName string
param location string
param modelDeployments array

@description('Required by Azure for Anthropic (Claude) deployments: { industry, organizationName, countryCode }. Ignored for other model formats.')
param anthropicProviderData object = {}

@description('Create the ANTHROPIC (Claude) deployments. Day-0 only: they are create-once, and re-PUTing an existing one (even unchanged) fails with Conflict/InternalServerError and drives it to Failed. OpenAI-format deployments ignore this flag — see the loop below.')
param createAnthropicModelDeployments bool = true

param tags object = {}

@description('Disable account-key auth so the ONLY data-plane path is Entra ID (the gateway managed identity). Keys would let anyone bypass every per-developer meter.')
param disableLocalAuth bool = true

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
    disableLocalAuth: disableLocalAuth
  }
}

// The create-once rule is Anthropic's, not ARM's (#259). An OpenAI-format deployment is a genuine
// upsert: re-PUTing it with the same model, sku and capacity leaves provisioningState = Succeeded and
// createdAt untouched — verified against a live account on 2026-09-05 by PUTing the same body three
// times. So ARM can keep the OpenAI deployments present on every run, and the flag guards only the
// Claude ones, whose re-PUT is what E-007 says drives a deployment to Failed.
//
// Before this, `createModelDeployments = false` on every day-N run meant nothing owned creating a
// missing OpenAI deployment: not ARM, and not the control plane, which can create one but never
// reconciles. dev spent its first day answering 404 on every model for exactly that reason (#259).
@batchSize(1)
resource deployments 'Microsoft.CognitiveServices/accounts/deployments@2026-07-01' = [
  for d in modelDeployments: if (d.format != 'Anthropic' || createAnthropicModelDeployments) {
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

