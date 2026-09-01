// Functions runtime storage (CONVENTIONS.md §Storage accounts: Functions runtime and
// high-volume non-relational data only; SQL is the system of record). Shared-key access is
// disabled — the Functions host connects with its user-assigned identity
// (AzureWebJobsStorage__accountName + __credential=managedidentity) and the Flex
// Consumption deployment container is read with the same identity, so no storage key ever
// appears in app settings. Blob/Queue/Table data roles are granted in
// modules/control-plane-rbac.bicep.
param storageAccountName string
param location string
param tags object = {}

@allowed(['Standard_LRS', 'Standard_ZRS', 'Standard_GRS'])
param skuName string = 'Standard_LRS'

@description('Blob container the Flex Consumption Function App deploys its package into.')
param deploymentContainerName string = 'function-deployments'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: union(tags, { 'fg-component': 'storage' })
  kind: 'StorageV2'
  sku: { name: skuName }
  properties: {
    accessTier: 'Hot'
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: deploymentContainerName
  properties: {
    publicAccess: 'None'
  }
}

output storageAccountName string = storage.name
output storageAccountId string = storage.id
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output queueEndpoint string = storage.properties.primaryEndpoints.queue
output tableEndpoint string = storage.properties.primaryEndpoints.table
output deploymentContainerName string = deploymentContainer.name
