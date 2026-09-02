// FoundryGate — full stack (subscription scope).
// Provisions: resource group, monitoring, Foundry accounts + model deployments per
// region, APIM (StandardV2), the AI gateway layer (backends, pools, APIs, products,
// policies) and — behind `deployControlPlane` — the FoundryGate control plane (API on
// Container Apps, Blazor UI on Static Web Apps, Functions Flex Consumption, Azure SQL,
// Key Vault, registry, role assignments; see modules/control-plane.bicep).
// The gateway data plane is deliberately deployable on its own (deployControlPlane=false,
// the default) so a fork can stand it up first and prove traffic flows before any app
// code exists; parameters/test.bicepparam is exactly that shape.
targetScope = 'subscription'

@description('Deployment environment name, used in resource names (e.g. test, dev, prod). Lowercase alphanumeric — it lands in storage/registry names.')
param environmentName string

@description('Primary Azure region for shared resources (APIM, monitoring).')
param location string = 'eastus2'

@description('Globally-unique suffix for DNS-named resources (APIM, Foundry accounts).')
param nameSuffix string

@description('APIM publisher email (required by APIM).')
param publisherEmail string

@description('APIM publisher display name.')
param publisherName string = 'FoundryGate'

@description('APIM v2 tier (BasicV2 is the cheapest viable; see modules/apim.bicep).')
param apimSkuName string = 'StandardV2'

@description('Regions to place Foundry accounts in. Two+ regions multiply TPM/RPM headroom via the APIM backend pool.')
param foundryRegions array = ['eastus2', 'swedencentral']

// NOTE on the model-placement contract: these params cover DAY-0 provisioning only
// ("everywhere" or "primary region"). Ongoing model lifecycle — adding models,
// per-model region subsets, capacity changes — is deliberately the control plane's
// job (#60/#64): Anthropic deployments are create-once under ARM (see
// modules/foundry.bicep), so ARM re-runs must not manage them.
// Every Claude model reachable through the alias map must exist in EVERY pool member —
// the Anthropic pool fails a request over to another region on a 429, and a region
// missing the deployment would turn a throttle into a 404. Hence all Claude models are
// pooled and only OpenAI (single-backend) models are primary-only. Model names/versions
// verified against `az cognitiveservices model list` (eastus2 + swedencentral,
// 2026-09-01); capacities sit well inside the subscription's GlobalStandard quota
// (sonnet-4-5 200, haiku-4-5 80, opus-4-5 40 units).
@description('Model deployments created in EVERY Foundry account (pooled models). format matches Microsoft.CognitiveServices deployment model.format.')
param pooledModelDeployments array = [
  {
    name: 'claude-sonnet-4-5'
    format: 'Anthropic'
    model: 'claude-sonnet-4-5'
    version: '20250929'
    sku: 'GlobalStandard'
    capacity: 10
  }
  {
    name: 'claude-haiku-4-5'
    format: 'Anthropic'
    model: 'claude-haiku-4-5'
    version: '20251001'
    sku: 'GlobalStandard'
    capacity: 5
  }
  {
    name: 'claude-opus-4-5'
    format: 'Anthropic'
    model: 'claude-opus-4-5'
    version: '20251101'
    sku: 'GlobalStandard'
    capacity: 5
  }
]

@description('Model deployments created only in the FIRST Foundry account (single-region models).')
param primaryOnlyModelDeployments array = [
  {
    name: 'gpt-4-1-mini'
    format: 'OpenAI'
    model: 'gpt-4.1-mini'
    version: '2025-04-14'
    sku: 'GlobalStandard'
    capacity: 10
  }
]

@description('Required by Azure for Anthropic (Claude) deployments: { industry, organizationName, countryCode }.')
param anthropicProviderData object

// Quota tiers, not per-user quotas: APIM's `token-quota` accepts LITERALS ONLY (policy
// expressions are rejected — "Expression return type 'System.Int32' is not allowed",
// validated live 2026-09-01), so a single policy cannot read a per-developer budget.
// Each tier becomes an APIM product carrying its own rendered llm-token-limit policy,
// and the control plane sets a developer's quota by issuing their APIM subscription
// against the matching tier product (#82).
@description('Quota tiers -> APIM products. Each: { name, displayName, description?, monthlyTokenQuota (0 = no native monthly quota), tpm }.')
param quotaTiers array = [
  {
    name: 'standard'
    displayName: 'Standard'
    description: 'Everyday agent usage. 5M tokens/month, 20K tokens/minute.'
    monthlyTokenQuota: 5000000
    tpm: 20000
  }
  {
    name: 'power'
    displayName: 'Power'
    description: 'Heavy agentic workloads. 20M tokens/month, 40K tokens/minute.'
    monthlyTokenQuota: 20000000
    tpm: 40000
  }
  {
    name: 'unlimited'
    displayName: 'Unlimited'
    description: 'No gateway-enforced monthly budget; burst smoothing only. Monthly oversight is the control plane\'s job.'
    monthlyTokenQuota: 0
    tpm: 100000
  }
]

// The alias map is also the allowlist (#86): aliases are the model names developers put
// in ANTHROPIC_DEFAULT_*_MODEL / Codex `model`, and anything not listed for their tier
// gets 403 model_not_permitted. Deployments rotate underneath by editing these values —
// the gateway module emits them as named values the control plane can PUT without a
// policy redeploy. `pool` is the APIM backend to route at ('anthropic' multi-region pool
// or 'openai'); `provider` is the front door the alias belongs to, so a Claude alias sent
// to the OpenAI front door is refused with the right base path instead of being routed
// into an opaque 404. Deployment names must match the *_ModelDeployments params above.
@description('Per-tier model alias maps: { <tier>: { <alias>: { deployment, pool, provider } } }. A tier with no entry permits no models.')
param productModelAliases object = {
  standard: {
    sonnet: { deployment: 'claude-sonnet-4-5', pool: 'anthropic', provider: 'anthropic' }
    haiku: { deployment: 'claude-haiku-4-5', pool: 'anthropic', provider: 'anthropic' }
    gpt: { deployment: 'gpt-4-1-mini', pool: 'openai', provider: 'openai' }
  }
  power: {
    sonnet: { deployment: 'claude-sonnet-4-5', pool: 'anthropic', provider: 'anthropic' }
    haiku: { deployment: 'claude-haiku-4-5', pool: 'anthropic', provider: 'anthropic' }
    gpt: { deployment: 'gpt-4-1-mini', pool: 'openai', provider: 'openai' }
  }
  unlimited: {
    sonnet: { deployment: 'claude-sonnet-4-5', pool: 'anthropic', provider: 'anthropic' }
    haiku: { deployment: 'claude-haiku-4-5', pool: 'anthropic', provider: 'anthropic' }
    opus: { deployment: 'claude-opus-4-5', pool: 'anthropic', provider: 'anthropic' }
    gpt: { deployment: 'gpt-4-1-mini', pool: 'openai', provider: 'openai' }
  }
}

@description('Create model deployments (first run). Set false on re-runs — Anthropic deployments are create-once under ARM; see modules/foundry.bicep.')
param createModelDeployments bool = true

// ---- Control plane (#43/#44) -----------------------------------------------------
// Off by default so the gateway-only deployment keeps working unchanged; dev/prod param
// files turn it on. When on, sqlAdminGroupObjectId/sqlAdminGroupName and
// entraApiClientId are effectively required (Bicep has no conditional-required, so an
// empty group id fails at deploy time inside modules/sql.bicep rather than here).
@description('Deploy the control plane (SQL, Container Apps API, Static Web App, Functions, Key Vault, registry, RBAC) alongside the gateway.')
param deployControlPlane bool = false

@allowed(['qa', 'prod'])
@description('ASPNETCORE_ENVIRONMENT for the control-plane hosts (lowercase per CONVENTIONS.md). Distinct from environmentName, which only names resources.')
param appEnvironment string = 'qa'

@description('Object id of the Entra security group that administers Azure SQL (Entra-only auth). The CI/OIDC principal that runs the dacpac deploy must be a member — a manual step, see docs reference/infrastructure.')
param sqlAdminGroupObjectId string = ''

@description('Display name of that group (becomes the SQL server admin login name).')
param sqlAdminGroupName string = ''

@description('Azure SQL database SKU: { name, tier, family?, capacity? }. GP_S_* names are serverless (auto-pause derived from the name); default is serverless for dev, prod.bicepparam uses provisioned General Purpose.')
param sqlDatabaseSku object = {
  name: 'GP_S_Gen5'
  tier: 'GeneralPurpose'
  family: 'Gen5'
  capacity: 1
}

@allowed(['Local', 'Zone', 'Geo', 'GeoZone'])
@description('Azure SQL backup storage redundancy: Local for dev, Geo for prod.')
param sqlBackupStorageRedundancy string = 'Local'

@description('Zone-redundant Azure SQL database (prod). Adds ~60% to the compute meter (eastus2 retail 2026-09-02: $0.152217/vCore-hr + $0.09133/vCore-hr ZR surcharge) and needs a region with availability zones and a SKU that supports ZR; false for dev.')
param sqlZoneRedundant bool = false

@description('Entra tenant the API validates bearer tokens against (AzureAd__TenantId).')
param entraTenantId string = tenant().tenantId

@description('Client id of the FoundryGate.Api app registration (AzureAd__ClientId).')
param entraApiClientId string = ''

@description('Token audience (AzureAd__Audience). Empty = api://{entraApiClientId}.')
param entraApiAudience string = ''

@description('API image, e.g. crfoundrygatedeve7k2.azurecr.io/foundrygate-api:<sha>. For the bootstrap deploy (registry created by this same run, nothing pushed yet) pass mcr.microsoft.com/k8se/quickstart:latest explicitly — the Container App module then switches to port 80 and /health-only probes. Every later infra run must pass the running tag; the param files read it from FG_API_IMAGE with no default so a forgotten variable fails build-params instead of silently resetting the app.')
param apiContainerImage string = ''

@minValue(1)
@description('Container App minimum replicas. 1 keeps the admin API warm (it is the Blazor UI\'s only backend).')
param containerAppMinReplicas int = 1

@minValue(1)
@description('Container App maximum replicas (HTTP concurrency scale rule, 50 concurrent requests per replica).')
param containerAppMaxReplicas int = 3

@description('vCPU per API replica, as a decimal string. Only the Consumption profile pairs are valid: 0.25/0.5Gi, 0.5/1.0Gi, 0.75/1.5Gi, 1.0/2.0Gi, ... The default matches the published cost model.')
param containerAppCpu string = '0.25'

@description('Memory per API replica — must be the pair of containerAppCpu (see above).')
param containerAppMemory string = '0.5Gi'

@description('Zone-redundant Container Apps environment. ARM rejects it without a VNet-integrated environment (infrastructureSubnetId), which infra/ does not declare anywhere yet, and the property is immutable — so turning this on is part of the private-networking change (spec §11) and RECREATES the environment, changing the Container App ingress FQDN. Tracked in #196; the parameter exists so that work does not also have to re-thread it.')
param containerAppsZoneRedundant bool = false

@allowed(['Standard_LRS', 'Standard_ZRS', 'Standard_GRS'])
@description('Functions runtime storage SKU: Standard_LRS for dev, Standard_ZRS for prod.')
param functionsStorageSku string = 'Standard_LRS'

@allowed(['Basic', 'Standard', 'Premium'])
@description('Container registry SKU: Basic for dev, Standard for prod (10x the included storage and higher throughput; Premium only buys geo-replication and private link).')
param containerRegistrySku string = 'Basic'

@allowed(['Free', 'Standard'])
@description('Static Web App tier: Free for dev, Standard for prod (custom domain, SLA).')
param staticWebAppSku string = 'Free'

@description('Static Web Apps region (limited set: eastus2, centralus, westus2, westeurope, eastasia).')
param staticWebAppLocation string = 'eastus2'

@description('Functions .NET isolated runtime version.')
param functionsRuntimeVersion string = '10.0'

@description('Key Vault purge protection — irreversible once on; true for prod.')
param keyVaultPurgeProtection bool = false

@minValue(7)
@maxValue(90)
@description('Key Vault soft-delete retention in days: 7 for dev (fast purge after teardown), 90 for prod.')
param keyVaultSoftDeleteRetentionInDays int = 7

@description('Create the Key Vault RSA key the API wraps APIM subscription keys with (#95).')
param createKeyEncryptionKey bool = true

@description('Define the custom RBAC role the ui-preview CI identity is assigned for Static Web Apps PR previews (#155). No built-in role grants any Microsoft.Web/staticSites action, so without this the only working option is Contributor. Needs Microsoft.Authorization/roleDefinitions/write at subscription scope; a fork whose principal lacks it must set this false and hand-craft the role (the assignment itself is an owner action either way, #109).')
param deploySwaPreviewRole bool = true

// Standard tags on every resource. Scale model: one FoundryGate stack per environment
// per subscription; additional REGIONS scale inside a stack (foundryRegions → pool
// members, all tagged fg-role=foundry); additional SUBSCRIPTIONS scale by deploying
// this same template per subscription (Claude Global Standard quota is pooled
// per-subscription per-model, so extra subscriptions — not extra same-subscription
// deployments — are what multiply Claude headroom). Tags make cross-subscription cost
// and inventory queries uniform: filter on workload + environment + fg-role.
// Short region names for resource naming; unknown regions fall back to the full name.
var regionShortNames = {
  eastus: 'eus'
  eastus2: 'eus2'
  westus: 'wus'
  westus2: 'wus2'
  westus3: 'wus3'
  swedencentral: 'swc'
  westeurope: 'weu'
  northeurope: 'neu'
  uksouth: 'uks'
  japaneast: 'jpe'
  australiaeast: 'aue'
}

var standardTags = {
  workload: 'foundrygate'
  environment: environmentName
  'managed-by': 'bicep'
  repo: 'kolatts/foundry-gate'
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-foundrygate-${environmentName}'
  location: location
  tags: standardTags
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'foundrygate-monitoring'
  scope: rg
  params: {
    environmentName: environmentName
    location: location
    tags: union(standardTags, { 'fg-role': 'monitoring' })
  }
}

// Serialized: concurrent Anthropic deployment creation across regions has been observed
// to fail with InternalServerError (Marketplace attestation appears subscription-serial).
@batchSize(1)
module foundry 'modules/foundry.bicep' = [
  for (region, i) in foundryRegions: {
    name: 'foundrygate-foundry-${region}'
    scope: rg
    params: {
      accountName: 'fg${environmentName}-${nameSuffix}-${regionShortNames[?region] ?? region}'
      location: region
      modelDeployments: i == 0 ? concat(pooledModelDeployments, primaryOnlyModelDeployments) : pooledModelDeployments
      anthropicProviderData: anthropicProviderData
      createModelDeployments: createModelDeployments
      tags: union(standardTags, {
        'fg-role': 'foundry'
        'fg-region-role': i == 0 ? 'primary' : 'pool-member'
      })
    }
  }
]

module apim 'modules/apim.bicep' = {
  name: 'foundrygate-apim'
  scope: rg
  params: {
    apimName: 'apim-foundrygate-${environmentName}-${nameSuffix}'
    location: location
    publisherEmail: publisherEmail
    publisherName: publisherName
    skuName: apimSkuName
    appInsightsId: monitoring.outputs.appInsightsId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    workspaceId: monitoring.outputs.workspaceId
    tags: union(standardTags, { 'fg-role': 'gateway' })
  }
}

// Gateway MI -> Foundry data-plane access (replaces account keys entirely).
module foundryRbac 'modules/foundry-rbac.bicep' = [
  for (region, i) in foundryRegions: {
    name: 'foundrygate-rbac-${region}'
    scope: rg
    params: {
      accountName: foundry[i].outputs.accountName
      apimPrincipalId: apim.outputs.principalId
    }
  }
]

module gateway 'modules/ai-gateway.bicep' = {
  name: 'foundrygate-ai-gateway'
  scope: rg
  params: {
    apimName: apim.outputs.apimName
    // Pool routing (#83) is derived from the foundryRegions order: the first region is
    // priority 1 (normal traffic, co-located with APIM), every later region is
    // priority 2 standing headroom that only takes traffic when priority 1 throttles or
    // its circuit breaker trips. Weight is only meaningful within a priority group.
    foundryAccounts: [
      for (region, i) in foundryRegions: {
        name: foundry[i].outputs.accountName
        endpoint: foundry[i].outputs.endpoint
        priority: i == 0 ? 1 : 2
        weight: 1
      }
    ]
    quotaTiers: quotaTiers
    productModelAliases: productModelAliases
  }
  dependsOn: [foundryRbac]
}

// The app that manages the gateway. Attaches to the gateway/monitoring resources above by
// name (role assignments on APIM, each Foundry account and the workspace), so it runs
// after the gateway layer is in place.
module controlPlane 'modules/control-plane.bicep' = if (deployControlPlane) {
  name: 'foundrygate-control-plane'
  scope: rg
  params: {
    environmentName: environmentName
    location: location
    nameSuffix: nameSuffix
    tags: standardTags
    appEnvironment: appEnvironment
    workspaceId: monitoring.outputs.workspaceId
    workspaceName: monitoring.outputs.workspaceName
    workspaceCustomerId: monitoring.outputs.workspaceCustomerId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    apimName: apim.outputs.apimName
    apimGatewayUrl: apim.outputs.gatewayUrl
    foundryAccountNames: [for (region, i) in foundryRegions: foundry[i].outputs.accountName]
    // Same object the gateway module turns into policy — one source, two consumers (#153).
    productModelAliases: productModelAliases
    sqlAdminGroupObjectId: sqlAdminGroupObjectId
    sqlAdminGroupName: sqlAdminGroupName
    sqlDatabaseSku: sqlDatabaseSku
    sqlBackupStorageRedundancy: sqlBackupStorageRedundancy
    sqlZoneRedundant: sqlZoneRedundant
    entraTenantId: entraTenantId
    entraApiClientId: entraApiClientId
    entraApiAudience: entraApiAudience
    apiContainerImage: apiContainerImage
    containerAppMinReplicas: containerAppMinReplicas
    containerAppMaxReplicas: containerAppMaxReplicas
    containerAppCpu: containerAppCpu
    containerAppMemory: containerAppMemory
    containerAppsZoneRedundant: containerAppsZoneRedundant
    functionsStorageSku: functionsStorageSku
    containerRegistrySku: containerRegistrySku
    staticWebAppSku: staticWebAppSku
    staticWebAppLocation: staticWebAppLocation
    functionsRuntimeVersion: functionsRuntimeVersion
    keyVaultPurgeProtection: keyVaultPurgeProtection
    keyVaultSoftDeleteRetentionInDays: keyVaultSoftDeleteRetentionInDays
    createKeyEncryptionKey: createKeyEncryptionKey
  }
  dependsOn: [gateway]
}

// The role the `ui-preview` GitHub Environment identity gets assigned so PR previews can read
// one Static Web App's deployment token and manage its staging environments — and nothing
// else (#155). Subscription scope because that is where role DEFINITIONS live; its single
// assignableScope is the Static Web App above, so the role cannot be assigned anywhere else
// even by someone who wanted to. The ASSIGNMENT is an owner action (#109) — the app
// registration is not in Bicep.
module swaPreviewRole 'modules/swa-preview-role.bicep' = if (deployControlPlane && deploySwaPreviewRole) {
  name: 'foundrygate-swa-preview-role'
  params: {
    staticWebAppId: controlPlane.?outputs.staticWebAppId ?? ''
    environmentName: environmentName
  }
}

// ---- Outputs: the contract the deploy workflows and the CLI consume -------------
// Gateway: addresses, the tier products that developer subscriptions scope to, the
// workspace holding billing-grade token logs, and the identities/names for further role
// assignments.
output resourceGroupName string = rg.name
output apimGatewayUrl string = apim.outputs.gatewayUrl
output apimName string = apim.outputs.apimName
output apimPrincipalId string = apim.outputs.principalId
output anthropicApiUrl string = gateway.outputs.anthropicApiUrl
output openaiApiUrl string = gateway.outputs.openaiApiUrl
output productIds array = gateway.outputs.productIds
output defaultProductId string = gateway.outputs.defaultProductId
output logAnalyticsWorkspaceId string = monitoring.outputs.workspaceId
output logAnalyticsWorkspaceName string = monitoring.outputs.workspaceName
@description('Workspace GUID (customerId) — the "workspace id" the Log Analytics query API expects; logAnalyticsWorkspaceId above is the ARM resource id.')
output logAnalyticsWorkspaceCustomerId string = monitoring.outputs.workspaceCustomerId
output appInsightsConnectionString string = monitoring.outputs.appInsightsConnectionString
output foundryAccountNames array = [for (region, i) in foundryRegions: foundry[i].outputs.accountName]

// Control plane: empty strings when deployControlPlane=false (safe-dereference on the
// conditional module, so these cannot drift from the module's own condition). Names follow
// the convention documented in modules/control-plane.bicep; the deploy workflows read these
// outputs (az deployment sub show --query properties.outputs) rather than re-deriving names.
output controlPlaneDeployed bool = deployControlPlane
output containerAppIsBootstrapImage bool = controlPlane.?outputs.containerAppIsBootstrapImage ?? false
output sqlServerName string = controlPlane.?outputs.sqlServerName ?? ''
output sqlServerFqdn string = controlPlane.?outputs.sqlServerFqdn ?? ''
output sqlDatabaseName string = controlPlane.?outputs.sqlDatabaseName ?? ''
output sqlEntraConnectionString string = controlPlane.?outputs.sqlEntraConnectionString ?? ''
output sqlAdminGroupName string = controlPlane.?outputs.sqlAdminGroupName ?? ''
output keyVaultName string = controlPlane.?outputs.keyVaultName ?? ''
output keyVaultUri string = controlPlane.?outputs.keyVaultUri ?? ''
output keyEncryptionKeyUri string = controlPlane.?outputs.keyEncryptionKeyUri ?? ''
output containerRegistryName string = controlPlane.?outputs.containerRegistryName ?? ''
output containerRegistryLoginServer string = controlPlane.?outputs.containerRegistryLoginServer ?? ''
output containerAppsEnvironmentName string = controlPlane.?outputs.containerAppsEnvironmentName ?? ''
output containerAppName string = controlPlane.?outputs.containerAppName ?? ''
output containerAppFqdn string = controlPlane.?outputs.containerAppFqdn ?? ''
output functionAppName string = controlPlane.?outputs.functionAppName ?? ''
output functionAppHostname string = controlPlane.?outputs.functionAppHostname ?? ''
output functionsStorageAccountName string = controlPlane.?outputs.functionsStorageAccountName ?? ''
output staticWebAppName string = controlPlane.?outputs.staticWebAppName ?? ''
output staticWebAppId string = controlPlane.?outputs.staticWebAppId ?? ''
output staticWebAppHostname string = controlPlane.?outputs.staticWebAppHostname ?? ''
@description('Role definition id to assign to the ui-preview identity, and the one scope it may be assigned at (#155/#109). Empty when the role was not deployed.')
output swaPreviewRoleDefinitionId string = swaPreviewRole.?outputs.roleDefinitionId ?? ''
output swaPreviewRoleName string = swaPreviewRole.?outputs.roleName ?? ''
output swaPreviewRoleAssignableScope string = swaPreviewRole.?outputs.assignableScope ?? ''
output apiIdentityName string = controlPlane.?outputs.apiIdentityName ?? ''
output apiIdentityClientId string = controlPlane.?outputs.apiIdentityClientId ?? ''
output apiIdentityPrincipalId string = controlPlane.?outputs.apiIdentityPrincipalId ?? ''
output functionsIdentityName string = controlPlane.?outputs.functionsIdentityName ?? ''
output functionsIdentityClientId string = controlPlane.?outputs.functionsIdentityClientId ?? ''
output functionsIdentityPrincipalId string = controlPlane.?outputs.functionsIdentityPrincipalId ?? ''
@description('The alias map as the control plane receives it — one row per (tier, alias), mirroring the Gateway__ModelAliases__{i}__* settings on both hosts (#153). Empty when the control plane is not deployed.')
output modelAliasRows array = controlPlane.?outputs.modelAliasRows ?? []
