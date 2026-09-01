// Grants APIM's system-assigned managed identity data-plane access to one Foundry
// account (Cognitive Services User), so gateway->backend auth needs no account keys.
param accountName string
param apimPrincipalId string

// Built-in role: Cognitive Services User
var cognitiveServicesUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'a97b65f3-24c7-4388-baec-2e87135dc908'
)

resource account 'Microsoft.CognitiveServices/accounts@2026-07-01' existing = {
  name: accountName
}

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, apimPrincipalId, cognitiveServicesUserRoleId)
  scope: account
  properties: {
    roleDefinitionId: cognitiveServicesUserRoleId
    principalId: apimPrincipalId
    principalType: 'ServicePrincipal'
  }
}
