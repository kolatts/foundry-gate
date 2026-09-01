// Azure Functions on the Flex Consumption plan (scale-to-zero, no always-on cost) for the
// background jobs: usage reconciliation from ApiManagementGatewayLlmLog (#84), monthly
// reset (#38), Entra sync (#40/#41).
//
// Identity everywhere: the host reaches its storage account with the user-assigned
// identity (AzureWebJobsStorage__credential=managedidentity), the deployment package is
// read from the storage container with the same identity, and Key Vault references resolve
// through it (keyVaultReferenceIdentity). Storage shared-key access is OFF
// (modules/storage-account.bicep), so nothing here could fall back to a key.
//
// Flex Consumption differences from the classic Consumption plan, for whoever edits this:
// no WEBSITE_RUN_FROM_PACKAGE / FUNCTIONS_WORKER_RUNTIME / linuxFxVersion — runtime and
// deployment source live in functionAppConfig; `Azure/functions-action` publishes to the
// deployment container.
param functionAppName string
param planName string
param location string
param tags object = {}

@description('Resource id of the Functions user-assigned identity.')
param identityId string

@description('Client id of that identity — AZURE_CLIENT_ID for AppTokenCredential and AzureWebJobsStorage__clientId for the host.')
param identityClientId string

@description('Storage account (shared-key access disabled) backing the Functions host and holding the deployment container.')
param storageAccountName string

@description('Blob container the package is deployed into.')
param deploymentContainerName string

@description('Application Insights connection string (shared monitoring stack).')
param appInsightsConnectionString string

@description('.NET isolated worker runtime version.')
param runtimeVersion string = '10.0'

@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 100

@allowed([2048, 4096])
param instanceMemoryMB int = 2048

@description('Additional app settings: [{ name, value }]. Merged after the host settings below.')
param appSettings array = []

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: union(tags, { 'fg-component': 'functions' })
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: union(tags, { 'fg-component': 'functions' })
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identityId
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identityId
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: runtimeVersion
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: concat(
        [
          { name: 'AzureWebJobsStorage__accountName', value: storage.name }
          { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
          { name: 'AzureWebJobsStorage__clientId', value: identityClientId }
          { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        ],
        appSettings
      )
    }
  }
}

output functionAppName string = functionApp.name
output functionAppId string = functionApp.id
output functionAppHostname string = functionApp.properties.defaultHostName
output planName string = plan.name
