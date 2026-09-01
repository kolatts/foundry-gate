// FoundryGate prod: full stack (gateway + control plane), production-grade SKUs.
//
// Environment variables this file REQUIRES (build-params fails loudly without them —
// deliberately: with Entra-only SQL, group membership IS the access model, and a
// forgotten image variable must never silently swap the production API for a
// placeholder page):
//   FG_SQL_ADMIN_GROUP_OBJECT_ID   object id of the DEDICATED prod SQL admin group (#109).
//   FG_SQL_ADMIN_GROUP_NAME        its display name.
//   FG_API_IMAGE                   image the Container App runs. Bootstrap run only:
//                                  mcr.microsoft.com/k8se/quickstart:latest; every later
//                                  run the tag currently running (see dev.bicepparam).
// Optional:
//   FG_ENTRA_API_CLIENT_ID         FoundryGate.Api app registration client id (#109).
using '../main.bicep'

param environmentName = 'prod'
param appEnvironment = 'prod'
param location = 'eastus2'
param nameSuffix = 'e7k2'
param publisherEmail = 'kolatts@gmail.com'
param publisherName = 'FoundryGate'
param anthropicProviderData = {
  industry: 'Software'
  organizationName: 'Imagile'
  countryCode: 'US'
}

param apimSkuName = 'StandardV2'

// Flip to true ONLY for the very first deployment. Anthropic deployments are
// create-once under ARM — re-running with true re-PUTs them into a Failed state
// (see modules/foundry.bicep). Model lifecycle after day 0 belongs to the control
// plane, not ARM.
param createModelDeployments = false

// ---- Control plane ---------------------------------------------------------------
param deployControlPlane = true

// Deliberately NOT the dev group: every dev admin and the dev CI principal would own the
// production database. Fails at build-params until the dedicated group exists (#109).
param sqlAdminGroupObjectId = readEnvironmentVariable('FG_SQL_ADMIN_GROUP_OBJECT_ID')
param sqlAdminGroupName = readEnvironmentVariable('FG_SQL_ADMIN_GROUP_NAME')

// Provisioned General Purpose, 2 vCores, geo-redundant backups: no auto-pause latency
// for the admin plane and cross-region restore for the system of record. Serverless
// vs provisioned is derived from the SKU name (GP_S_* = serverless).
param sqlDatabaseSku = {
  name: 'GP_Gen5_2'
  tier: 'GeneralPurpose'
  family: 'Gen5'
  capacity: 2
}
param sqlBackupStorageRedundancy = 'Geo'

param entraApiClientId = readEnvironmentVariable('FG_ENTRA_API_CLIENT_ID', '00000000-0000-0000-0000-000000000000')
param apiContainerImage = readEnvironmentVariable('FG_API_IMAGE')

param containerAppMinReplicas = 1
param containerAppMaxReplicas = 3
// Standard: custom domain + SLA for the admin UI.
param staticWebAppSku = 'Standard'
// Irreversible once on — which is the point for prod.
param keyVaultPurgeProtection = true
param keyVaultSoftDeleteRetentionInDays = 90

// Deliberately v1-scoped: zone redundancy (SQL/Container Apps env), storage SKU, ACR SKU
// and Container App CPU/memory are leaf-module params not yet plumbed through
// modules/control-plane.bicep — the defaults (no ZR, Standard_LRS, Basic ACR,
// 0.25 vCPU/0.5 GiB) are what prod gets today. Tracked in #134.
