// FoundryGate prod: full stack (gateway + control plane), production-grade SKUs.
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

// Entra security group that administers Azure SQL (Entra-only auth, no SQL login). The
// CI OIDC principal must be a member for the dacpac deploy to connect. A dedicated
// prod group is preferable to sharing the dev one — swap it here when it exists.
param sqlAdminGroupObjectId = '2ed4d6b7-575c-4046-aeb0-eb51bc254ef5'
param sqlAdminGroupName = 'SG_IMAGILE_SQL_ADMINS'

// Provisioned General Purpose, 2 vCores, geo-redundant backups: no auto-pause latency
// for the admin plane and cross-region restore for the system of record.
param sqlDatabaseSku = {
  name: 'GP_Gen5_2'
  tier: 'GeneralPurpose'
  family: 'Gen5'
  capacity: 2
}
param sqlServerless = false
param sqlBackupStorageRedundancy = 'Geo'

// See dev.bicepparam for why these two come from the environment.
param entraApiClientId = readEnvironmentVariable('FG_ENTRA_API_CLIENT_ID', '00000000-0000-0000-0000-000000000000')
param apiContainerImage = readEnvironmentVariable('FG_API_IMAGE', '')

param containerAppMinReplicas = 1
param containerAppMaxReplicas = 3
// Standard: custom domain + SLA for the admin UI.
param staticWebAppSku = 'Standard'
// Irreversible once on — which is the point for prod.
param keyVaultPurgeProtection = true
param keyVaultSoftDeleteRetentionInDays = 90
