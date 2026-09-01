// Role assignments wiring the control-plane identities to the resources they operate on.
// Everything is scoped to the individual resource (never the resource group), and every
// assignment name is guid(scope, principal, role) so re-runs are idempotent.
//
// API identity (FoundryGate.Api):
//   Key Vault Secrets User           @KeyVault() reference resolution at startup
//   Key Vault Crypto User            wrap/unwrap APIM subscription keys with the KEK (#95)
//   AcrPull                          pull the API image
//   API Management Service Contributor (APIM instance)
//                                    create / rotate / delete developer subscriptions and
//                                    move them between tier products; edit fg-model-map-*
//                                    named values (#86)
//   Cognitive Services Contributor   (each Foundry account) model deployment lifecycle (#61)
//   Log Analytics Reader             (workspace) usage queries for the admin dashboard
//
// Functions identity (FoundryGate.Functions):
//   Key Vault Secrets User
//   Log Analytics Reader             (workspace) reconciliation reads
//                                    ApiManagementGatewayLlmLog (#84)
//   Storage Blob Data Owner          Functions host state + Flex deployment container
//   Storage Queue Data Contributor   queue triggers/outputs (CONVENTIONS.md typed queues)
//   Storage Table Data Contributor   table bindings
//
// NOT here, by design: SQL access (contained users via T-SQL — see modules/sql.bicep), and
// the Marketplace/SaaS permissions a runtime Claude deployment create needs (#13 direction
// update) — tracked separately, since they are subscription-scope and outside this RG.
param apiPrincipalId string
param functionsPrincipalId string
param keyVaultName string
param registryName string
param storageAccountName string
param apimName string
param foundryAccountNames array
param workspaceName string

// Built-in role definition ids.
var roles = {
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultCryptoUser: '12338af0-0e69-4776-bea7-57ae8d297424'
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  apimServiceContributor: '312a565d-c81f-4fd8-895a-4e21e48d571c'
  cognitiveServicesContributor: '25fbc0a9-bd7c-42a3-aa1a-3b75d497ee68'
  logAnalyticsReader: '73c42c96-874c-492f-b3fb-9d5a4d9d0a5b'
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aebd'
}

func roleId(id string) string => subscriptionResourceId('Microsoft.Authorization/roleDefinitions', id)

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: keyVaultName
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = {
  name: registryName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' existing = {
  name: apimName
}

resource foundryAccounts 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = [
  for name in foundryAccountNames: {
    name: name
  }
]

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

// ---- API identity ----------------------------------------------------------------
resource apiKeyVaultSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, apiPrincipalId, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: roleId(roles.keyVaultSecretsUser)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiKeyVaultCrypto 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, apiPrincipalId, roles.keyVaultCryptoUser)
  scope: keyVault
  properties: {
    roleDefinitionId: roleId(roles.keyVaultCryptoUser)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, apiPrincipalId, roles.acrPull)
  scope: registry
  properties: {
    roleDefinitionId: roleId(roles.acrPull)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiApimContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(apim.id, apiPrincipalId, roles.apimServiceContributor)
  scope: apim
  properties: {
    roleDefinitionId: roleId(roles.apimServiceContributor)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiFoundryContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (name, i) in foundryAccountNames: {
    name: guid(foundryAccounts[i].id, apiPrincipalId, roles.cognitiveServicesContributor)
    scope: foundryAccounts[i]
    properties: {
      roleDefinitionId: roleId(roles.cognitiveServicesContributor)
      principalId: apiPrincipalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource apiLogAnalyticsReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workspace.id, apiPrincipalId, roles.logAnalyticsReader)
  scope: workspace
  properties: {
    roleDefinitionId: roleId(roles.logAnalyticsReader)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ---- Functions identity ----------------------------------------------------------
resource functionsKeyVaultSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionsPrincipalId, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: roleId(roles.keyVaultSecretsUser)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsLogAnalyticsReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workspace.id, functionsPrincipalId, roles.logAnalyticsReader)
  scope: workspace
  properties: {
    roleDefinitionId: roleId(roles.logAnalyticsReader)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsStorageBlobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionsPrincipalId, roles.storageBlobDataOwner)
  scope: storage
  properties: {
    roleDefinitionId: roleId(roles.storageBlobDataOwner)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsStorageQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionsPrincipalId, roles.storageQueueDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: roleId(roles.storageQueueDataContributor)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsStorageTableContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionsPrincipalId, roles.storageTableDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: roleId(roles.storageTableDataContributor)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}
