// User-assigned managed identities for the control-plane hosts (API on Container Apps,
// background jobs on Functions). User-assigned rather than system-assigned so the role
// assignments can be granted BEFORE the hosts exist — the Container App needs AcrPull at
// creation time to pull its image, and the Flex Consumption Function App needs blob access
// to its deployment container at creation time. Program.cs reads AZURE_CLIENT_ID to pick
// the identity (CONVENTIONS.md: AppTokenCredential-style chain, not DefaultAzureCredential).
param environmentName string
param location string
param tags object = {}

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-foundrygate-api-${environmentName}'
  location: location
  tags: union(tags, { 'fg-component': 'api' })
}

resource functionsIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-foundrygate-func-${environmentName}'
  location: location
  tags: union(tags, { 'fg-component': 'functions' })
}

output apiIdentityId string = apiIdentity.id
output apiIdentityName string = apiIdentity.name
output apiIdentityClientId string = apiIdentity.properties.clientId
output apiIdentityPrincipalId string = apiIdentity.properties.principalId
output functionsIdentityId string = functionsIdentity.id
output functionsIdentityName string = functionsIdentity.name
output functionsIdentityClientId string = functionsIdentity.properties.clientId
output functionsIdentityPrincipalId string = functionsIdentity.properties.principalId
