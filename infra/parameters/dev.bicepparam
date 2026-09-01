// FoundryGate dev: full stack (gateway + control plane), cost-minimised.
// Runs as `qa` from the app's point of view (CONVENTIONS.md environments are local/qa/prod).
using '../main.bicep'

param environmentName = 'dev'
param appEnvironment = 'qa'
param location = 'eastus2'
param nameSuffix = 'e7k2'
param publisherEmail = 'kolatts@gmail.com'
param publisherName = 'FoundryGate Dev'
param anthropicProviderData = {
  industry: 'Software'
  organizationName: 'Imagile'
  countryCode: 'US'
}

// Cheapest viable v2 tier; the LLM policies, pools and breakers all work on it.
param apimSkuName = 'BasicV2'

// Flip to true ONLY for the very first deployment. Anthropic deployments are
// create-once under ARM — re-running with true re-PUTs them into a Failed state
// (see modules/foundry.bicep). Model lifecycle after day 0 belongs to the control
// plane, not ARM.
param createModelDeployments = false

// ---- Control plane ---------------------------------------------------------------
param deployControlPlane = true

// Entra security group that administers Azure SQL (Entra-only auth, no SQL login). The
// CI OIDC principal must be a member for the dacpac deploy to connect — see
// docs reference/infrastructure. Object ids are not secrets.
param sqlAdminGroupObjectId = '2ed4d6b7-575c-4046-aeb0-eb51bc254ef5'
param sqlAdminGroupName = 'SG_IMAGILE_SQL_ADMINS'

// Serverless, auto-pauses after an hour idle (the sqlDatabaseSku default in main.bicep).
param sqlServerless = true
param sqlBackupStorageRedundancy = 'Local'

// FoundryGate.Api app registration. Read from the environment so the deploy workflow can
// supply it from a GitHub Environment variable; the zero GUID lets a bootstrap deploy of
// the infrastructure succeed before the registration exists (token validation will
// reject everything until it is real).
param entraApiClientId = readEnvironmentVariable('FG_ENTRA_API_CLIENT_ID', '00000000-0000-0000-0000-000000000000')

// Current API image. Empty on the bootstrap run (nothing pushed yet — placeholder image);
// the deploy workflows set FG_API_IMAGE to the tag currently running before every infra
// re-run so Bicep never resets the app to the placeholder.
param apiContainerImage = readEnvironmentVariable('FG_API_IMAGE', '')

param containerAppMinReplicas = 1
param containerAppMaxReplicas = 2
param staticWebAppSku = 'Free'
param keyVaultPurgeProtection = false
param keyVaultSoftDeleteRetentionInDays = 7
