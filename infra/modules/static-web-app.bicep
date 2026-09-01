// Static Web App hosting the Blazor WASM admin UI. Free tier for dev; Standard for prod
// when a custom domain / SLA is wanted. Deployed by the SWA deploy action with the site's
// deployment token (`provider: 'Custom'` — no GitHub repository binding on the resource,
// which would otherwise try to write its own workflow file into the repo).
//
// The default hostname is what the API allows in CORS (Cors__AllowedOrigins__0), so this
// module runs before the Container App in modules/control-plane.bicep.
param staticWebAppName string

@description('Static Web Apps is only offered in a handful of regions (eastus2, centralus, westus2, westeurope, eastasia); pass one of those, not necessarily the stack location.')
param location string
param tags object = {}

@allowed(['Free', 'Standard'])
param sku string = 'Free'

resource staticWebApp 'Microsoft.Web/staticSites@2024-04-01' = {
  name: staticWebAppName
  location: location
  tags: union(tags, { 'fg-component': 'ui' })
  sku: { name: sku, tier: sku }
  properties: {
    provider: 'Custom'
    stagingEnvironmentPolicy: 'Enabled'
    allowConfigFileUpdates: true
  }
}

output staticWebAppName string = staticWebApp.name
output staticWebAppId string = staticWebApp.id
output defaultHostname string = staticWebApp.properties.defaultHostname
