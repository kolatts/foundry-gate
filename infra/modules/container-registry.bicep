// Container registry for the API image. Pull is by the API's user-assigned identity
// (AcrPull, modules/control-plane-rbac.bicep); the admin user stays disabled so no
// registry password exists. One registry per environment (it lives in the environment's
// resource group, so destroying the environment takes its images with it).
param registryName string
param location string
param tags object = {}

@allowed(['Basic', 'Standard', 'Premium'])
param sku string = 'Basic'

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: registryName
  location: location
  tags: union(tags, { 'fg-component': 'registry' })
  sku: { name: sku }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

output registryName string = registry.name
output registryId string = registry.id
output loginServer string = registry.properties.loginServer
