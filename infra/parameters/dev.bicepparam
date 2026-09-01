// FoundryGate dev: full stack (gateway + control plane), cost-minimised.
// Runs as `qa` from the app's point of view (CONVENTIONS.md environments are local/qa/prod).
//
// Environment variables this file REQUIRES (build-params fails loudly without them —
// deliberately, so a forgotten variable can never silently change what gets deployed):
//   FG_API_IMAGE   image the Container App runs. Bootstrap run (registry empty):
//                  mcr.microsoft.com/k8se/quickstart:latest. Every later run: the tag
//                  currently running, e.g. from
//                  az containerapp show -n ca-foundrygate-api-dev -g rg-foundrygate-dev \
//                    --query properties.template.containers[0].image -o tsv
// Optional:
//   FG_ENTRA_API_CLIENT_ID   FoundryGate.Api app registration client id (#109). The
//                            zero-GUID fallback lets infra deploy before it exists; token
//                            validation rejects everything until it is real.
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
// CI OIDC principal must be a member for the dacpac deploy to connect (#109). Object ids
// are identifiers, not secrets. Dev shares the tenant's existing SQL admin group.
param sqlAdminGroupObjectId = '2ed4d6b7-575c-4046-aeb0-eb51bc254ef5'
param sqlAdminGroupName = 'SG_IMAGILE_SQL_ADMINS'

// Serverless GP_S_Gen5 x1 (the main.bicep default): auto-pauses after 60 idle minutes.
// That pause is real only because nothing polls the database — the API's readiness probe
// deliberately hits the hermetic /health, not /health/ready (modules/container-app.bicep).
param sqlBackupStorageRedundancy = 'Local'

param entraApiClientId = readEnvironmentVariable('FG_ENTRA_API_CLIENT_ID', '00000000-0000-0000-0000-000000000000')
param apiContainerImage = readEnvironmentVariable('FG_API_IMAGE')

param containerAppMinReplicas = 1
param containerAppMaxReplicas = 2
param staticWebAppSku = 'Free'
param keyVaultPurgeProtection = false
param keyVaultSoftDeleteRetentionInDays = 7
