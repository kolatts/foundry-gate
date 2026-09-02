// FoundryGate control plane (resource-group scope): the app that manages the gateway —
// API on Container Apps, Blazor UI on Static Web Apps, background jobs on Functions Flex
// Consumption, Azure SQL (Entra-only), Key Vault, the container registry, and the role
// assignments that let the two managed identities operate the gateway data plane.
//
// This is an ORCHESTRATION module: it owns the naming convention and the wiring between
// leaf modules and nothing else. It exists (rather than main.bicep calling the leaf modules
// directly) because the control plane is optional — main.bicep deploys it behind
// `deployControlPlane`, and one conditional module means one nullable reference per output
// there instead of nullable cross-references between eight conditional modules here.
// Everything inside is unconditional and ordered by real dependencies:
//
//   identities ─┬─> key vault ─┐
//               ├─> registry  ─┤
//               ├─> storage   ─┼─> rbac ─┬─> container app (pulls image via AcrPull)
//               ├─> sql       ─┤         └─> function app  (reads deployment container)
//               └─> static web app (hostname feeds the API's CORS origin)
//
// NAMING (all inside rg-foundrygate-{env}; {suffix} = nameSuffix, needed only where the
// name is a global DNS label). The CLI `ip setup` (#96) and the deploy workflows resolve
// resources from these patterns, so changing one is a contract change:
//   sql-foundrygate-{env}-{suffix}     Azure SQL logical server  (FQDN: <name>.database.windows.net)
//   sqldb-foundrygate-{env}            database
//   kv-fg-{env}-{suffix}               Key Vault  (24-char limit forces the short form)
//   crfoundrygate{env}{suffix}         container registry (alphanumeric only)
//   cae-foundrygate-{env}              Container Apps environment
//   ca-foundrygate-api-{env}           API container app
//   id-foundrygate-api-{env}           API user-assigned identity
//   id-foundrygate-func-{env}          Functions user-assigned identity
//   func-foundrygate-{env}-{suffix}    Function App
//   asp-foundrygate-func-{env}         Flex Consumption plan
//   stfg{env}{suffix}                  Functions storage account (3-24 lowercase alphanumeric)
//   stapp-foundrygate-{env}            Static Web App
param environmentName string
param location string
param nameSuffix string
param tags object = {}

@allowed(['qa', 'prod'])
@description('ASPNETCORE_ENVIRONMENT for the hosts — lowercase qa|prod per CONVENTIONS.md (local is docker-only). Distinct from environmentName, which only names resources.')
param appEnvironment string

// ---- Shared gateway/monitoring resources this module attaches to ----------------
@description('ARM resource id of the Log Analytics workspace (diagnostic settings, RBAC scope).')
param workspaceId string
param workspaceName string
@description('Workspace GUID (customerId) — what the Log Analytics query API calls the workspace id.')
param workspaceCustomerId string
param appInsightsConnectionString string
param apimName string
param apimGatewayUrl string
param foundryAccountNames array

// ---- SQL ------------------------------------------------------------------------
param sqlAdminGroupObjectId string
param sqlAdminGroupName string
param sqlDatabaseSku object
param sqlBackupStorageRedundancy string
@description('Spread the SQL database across availability zones (prod). Adds roughly the compute line again; the region must offer AZs and the SKU must support ZR.')
param sqlZoneRedundant bool = false

// ---- Entra (API app registration) -----------------------------------------------
param entraTenantId string
param entraApiClientId string
@description('Token audience; defaults to api://{entraApiClientId} when empty.')
param entraApiAudience string = ''

// ---- Hosting --------------------------------------------------------------------
param apiContainerImage string
param containerAppMinReplicas int
param containerAppMaxReplicas int
@description('vCPU per API replica, as a decimal string. Consumption profile pairs only: 0.25/0.5Gi, 0.5/1.0Gi, 1.0/2.0Gi, ...')
param containerAppCpu string = '0.25'
@description('Memory per API replica. Must be the pair of containerAppCpu above.')
param containerAppMemory string = '0.5Gi'
@description('Zone-redundant Container Apps environment. Requires VNet integration, so false everywhere until private networking lands (#196).')
param containerAppsZoneRedundant bool = false
@description('Functions storage account SKU. Standard_ZRS for prod; Standard_LRS is the cheap default.')
param functionsStorageSku string = 'Standard_LRS'
@description('Container registry SKU. Standard for prod (more included storage and throughput); Basic is the cheap default.')
param containerRegistrySku string = 'Basic'
param staticWebAppSku string
param staticWebAppLocation string
param functionsRuntimeVersion string
param keyVaultPurgeProtection bool
param keyVaultSoftDeleteRetentionInDays int
param createKeyEncryptionKey bool

var controlPlaneTags = union(tags, { 'fg-role': 'control-plane' })
var audience = empty(entraApiAudience) ? 'api://${entraApiClientId}' : entraApiAudience
var apiPort = 8080

var names = {
  sqlServer: 'sql-foundrygate-${environmentName}-${nameSuffix}'
  sqlDatabase: 'sqldb-foundrygate-${environmentName}'
  keyVault: 'kv-fg-${environmentName}-${nameSuffix}'
  registry: 'crfoundrygate${environmentName}${nameSuffix}'
  containerAppsEnvironment: 'cae-foundrygate-${environmentName}'
  containerApp: 'ca-foundrygate-api-${environmentName}'
  functionApp: 'func-foundrygate-${environmentName}-${nameSuffix}'
  functionsPlan: 'asp-foundrygate-func-${environmentName}'
  storage: take('stfg${environmentName}${nameSuffix}', 24)
  staticWebApp: 'stapp-foundrygate-${environmentName}'
}

module identities 'managed-identities.bicep' = {
  name: 'foundrygate-cp-identities'
  params: {
    environmentName: environmentName
    location: location
    tags: controlPlaneTags
  }
}

module keyVault 'key-vault.bicep' = {
  name: 'foundrygate-cp-keyvault'
  params: {
    keyVaultName: names.keyVault
    location: location
    tags: controlPlaneTags
    enablePurgeProtection: keyVaultPurgeProtection
    softDeleteRetentionInDays: keyVaultSoftDeleteRetentionInDays
    createKeyEncryptionKey: createKeyEncryptionKey
  }
}

module registry 'container-registry.bicep' = {
  name: 'foundrygate-cp-registry'
  params: {
    registryName: names.registry
    location: location
    tags: controlPlaneTags
    sku: containerRegistrySku
  }
}

module storage 'storage-account.bicep' = {
  name: 'foundrygate-cp-storage'
  params: {
    storageAccountName: names.storage
    location: location
    tags: controlPlaneTags
    skuName: functionsStorageSku
  }
}

module sql 'sql.bicep' = {
  name: 'foundrygate-cp-sql'
  params: {
    sqlServerName: names.sqlServer
    sqlDatabaseName: names.sqlDatabase
    location: location
    tags: controlPlaneTags
    entraAdminGroupObjectId: sqlAdminGroupObjectId
    entraAdminGroupName: sqlAdminGroupName
    databaseSku: sqlDatabaseSku
    backupStorageRedundancy: sqlBackupStorageRedundancy
    zoneRedundant: sqlZoneRedundant
  }
}

module staticWebApp 'static-web-app.bicep' = {
  name: 'foundrygate-cp-staticwebapp'
  params: {
    staticWebAppName: names.staticWebApp
    location: staticWebAppLocation
    tags: controlPlaneTags
    sku: staticWebAppSku
  }
}

// Role assignments BEFORE the hosts: the Container App pulls with AcrPull at creation and
// the Function App reads its deployment container with Blob Data Owner at creation.
module rbac 'control-plane-rbac.bicep' = {
  name: 'foundrygate-cp-rbac'
  params: {
    apiPrincipalId: identities.outputs.apiIdentityPrincipalId
    functionsPrincipalId: identities.outputs.functionsIdentityPrincipalId
    keyVaultName: keyVault.outputs.keyVaultName
    registryName: registry.outputs.registryName
    storageAccountName: storage.outputs.storageAccountName
    apimName: apimName
    foundryAccountNames: foundryAccountNames
    workspaceName: workspaceName
  }
}

// Configuration the API and Functions share. Keys are the ASP.NET Core configuration paths
// (double underscore = section separator) of FoundryGate.Api/Configuration/AppSettings.cs.
// The connection string is Entra-auth and therefore not a secret; nothing here is.
var sharedAppConfig = [
  { name: 'Azure__KeyVaultUrl', value: keyVault.outputs.keyVaultUri }
  { name: 'ConnectionStrings__FoundryGate', value: sql.outputs.entraConnectionString }
  { name: 'OpenTelemetry__Enabled', value: 'true' }
  { name: 'OpenTelemetry__ConnectionString', value: appInsightsConnectionString }
  // Gateway addressing, so the control plane never needs resource ids typed into
  // SystemConfiguration by hand. Consumed by the APIM key service (#36/#37), the Foundry
  // deployment service (#61) and the reconciliation function (#84).
  { name: 'Gateway__SubscriptionId', value: subscription().subscriptionId }
  { name: 'Gateway__ResourceGroup', value: resourceGroup().name }
  { name: 'Gateway__ApimName', value: apimName }
  { name: 'Gateway__ApimGatewayUrl', value: apimGatewayUrl }
  // Two different "workspace ids" on purpose: the GUID is what the query API wants
  // (LogsQueryClient.QueryWorkspaceAsync, /v1/workspaces/{id}/query); the ARM id is for
  // QueryResourceAsync and anything management-plane. #108 binds both.
  { name: 'Gateway__LogAnalyticsWorkspaceId', value: workspaceCustomerId }
  { name: 'Gateway__LogAnalyticsWorkspaceResourceId', value: workspaceId }
  { name: 'Gateway__KeyEncryptionKeyUri', value: keyVault.outputs.keyEncryptionKeyUri }
]

var foundryAccountConfig = [
  for (name, i) in foundryAccountNames: { name: 'Gateway__FoundryAccountNames__${i}', value: name }
]

module containerApp 'container-app.bicep' = {
  name: 'foundrygate-cp-containerapp'
  params: {
    containerAppsEnvironmentName: names.containerAppsEnvironment
    containerAppName: names.containerApp
    location: location
    tags: controlPlaneTags
    workspaceId: workspaceId
    identityId: identities.outputs.apiIdentityId
    registryLoginServer: registry.outputs.loginServer
    containerImage: apiContainerImage
    targetPort: apiPort
    minReplicas: containerAppMinReplicas
    maxReplicas: containerAppMaxReplicas
    cpu: containerAppCpu
    memory: containerAppMemory
    zoneRedundant: containerAppsZoneRedundant
    environmentVariables: concat(
      [
        { name: 'ASPNETCORE_ENVIRONMENT', value: appEnvironment }
        { name: 'ASPNETCORE_URLS', value: 'http://+:${apiPort}' }
        { name: 'AZURE_CLIENT_ID', value: identities.outputs.apiIdentityClientId }
        // https://login.microsoftonline.com/ in the public cloud; sovereign-cloud forks get theirs.
        { name: 'AzureAd__Instance', value: environment().authentication.loginEndpoint }
        { name: 'AzureAd__TenantId', value: entraTenantId }
        { name: 'AzureAd__ClientId', value: entraApiClientId }
        { name: 'AzureAd__Audience', value: audience }
        { name: 'Cors__AllowedOrigins__0', value: 'https://${staticWebApp.outputs.defaultHostname}' }
      ],
      sharedAppConfig,
      foundryAccountConfig
    )
  }
  dependsOn: [rbac]
}

module functionApp 'function-app.bicep' = {
  name: 'foundrygate-cp-functionapp'
  params: {
    functionAppName: names.functionApp
    planName: names.functionsPlan
    location: location
    tags: controlPlaneTags
    identityId: identities.outputs.functionsIdentityId
    identityClientId: identities.outputs.functionsIdentityClientId
    storageAccountName: storage.outputs.storageAccountName
    deploymentContainerName: storage.outputs.deploymentContainerName
    appInsightsConnectionString: appInsightsConnectionString
    runtimeVersion: functionsRuntimeVersion
    appSettings: concat(
      [
        // The Functions host reads AZURE_FUNCTIONS_ENVIRONMENT; the isolated worker's
        // generic host reads DOTNET_ENVIRONMENT. Both get the same lowercase value.
        { name: 'AZURE_FUNCTIONS_ENVIRONMENT', value: appEnvironment }
        { name: 'DOTNET_ENVIRONMENT', value: appEnvironment }
        { name: 'AZURE_CLIENT_ID', value: identities.outputs.functionsIdentityClientId }
      ],
      sharedAppConfig,
      foundryAccountConfig
    )
  }
  dependsOn: [rbac]
}

output sqlServerName string = sql.outputs.sqlServerName
output sqlServerFqdn string = sql.outputs.sqlServerFqdn
output sqlDatabaseName string = sql.outputs.sqlDatabaseName
output sqlEntraConnectionString string = sql.outputs.entraConnectionString
output sqlAdminGroupName string = sqlAdminGroupName
output keyVaultName string = keyVault.outputs.keyVaultName
output keyVaultUri string = keyVault.outputs.keyVaultUri
output keyEncryptionKeyUri string = keyVault.outputs.keyEncryptionKeyUri
output containerRegistryName string = registry.outputs.registryName
output containerRegistryLoginServer string = registry.outputs.loginServer
output containerAppsEnvironmentName string = containerApp.outputs.containerAppsEnvironmentName
output containerAppName string = containerApp.outputs.containerAppName
output containerAppFqdn string = containerApp.outputs.containerAppFqdn
output containerAppIsBootstrapImage bool = containerApp.outputs.isBootstrapImage
output functionAppName string = functionApp.outputs.functionAppName
output functionAppHostname string = functionApp.outputs.functionAppHostname
output functionsStorageAccountName string = storage.outputs.storageAccountName
output staticWebAppName string = staticWebApp.outputs.staticWebAppName
@description('ARM resource id — the only assignableScope of the SWA preview publisher role (modules/swa-preview-role.bicep, #155).')
output staticWebAppId string = staticWebApp.outputs.staticWebAppId
output staticWebAppHostname string = staticWebApp.outputs.defaultHostname
output apiIdentityName string = identities.outputs.apiIdentityName
output apiIdentityClientId string = identities.outputs.apiIdentityClientId
output apiIdentityPrincipalId string = identities.outputs.apiIdentityPrincipalId
output functionsIdentityName string = identities.outputs.functionsIdentityName
output functionsIdentityClientId string = identities.outputs.functionsIdentityClientId
output functionsIdentityPrincipalId string = identities.outputs.functionsIdentityPrincipalId
