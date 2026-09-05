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
// Geo: cross-region restore for the system of record. Note the asymmetry this creates with
// the zone-redundant database below — RA-GRS backup storage is LRS *within* the primary
// region, so a zone loss takes the primary backups with it while the database itself keeps
// serving. 'GeoZone' (already in main.bicep's @allowed list) is the pairing that makes both
// halves zone-tolerant; it is not the default here only because it costs more and the
// restore path of last resort is the geo-secondary either way. Revisit with #105.
param sqlBackupStorageRedundancy = 'Geo'

// DECIDE THIS BEFORE PRODUCTION'S FIRST DEPLOY (#241). Left unset deliberately, so it
// inherits `location` — but that is a placeholder, not a decision:
//
//   param sqlLocation = '<region confirmed open>'
//
// Azure closes individual regions to NEW Azure SQL logical servers without notice, and it
// is invisible to every quota and SKU query — the only way to know is to attempt a create.
// eastus2 AND eastus were both closed to this subscription on 2026-09-05, which is why dev
// runs SQL in centralus. Probe the intended region before the first prod deploy rather than
// finding out mid-deployment.
//
// Two constraints if you set it, because `Microsoft.Sql/servers.location` is IMMUTABLE and
// this is therefore one-shot:
//   * `sqlZoneRedundant = true` below needs a region that actually has availability zones.
//   * `sqlBackupStorageRedundancy = 'Geo'` above geo-pairs from THIS region, not `location`.

// Zone-redundant database: survives the loss of one availability zone in the SQL region
// (eastus2 unless `sqlLocation` above says otherwise) without a
// restore. Adds ~60% to the SQL compute line, not 2x — eastus2 retail (2026-09-02) is
// $0.152217/vCore-hr plus a $0.09133/vCore-hr zone-redundancy surcharge
// (docs reference/cost-and-capacity).
param sqlZoneRedundant = true

param entraApiClientId = readEnvironmentVariable('FG_ENTRA_API_CLIENT_ID', '00000000-0000-0000-0000-000000000000')
param apiContainerImage = readEnvironmentVariable('FG_API_IMAGE')

param containerAppMinReplicas = 1
param containerAppMaxReplicas = 3
// Double the dev replica size (the next valid Consumption pair up from 0.25/0.5Gi). The API
// is the Blazor UI's only backend and the gateway's management plane; 0.25 vCPU is a dev
// budget, not a production one.
param containerAppCpu = '0.5'
param containerAppMemory = '1.0Gi'
// Zone redundancy for the Container Apps environment is deliberately still false: ARM only
// accepts zoneRedundant:true on a VNet-integrated environment (infrastructureSubnetId), and
// infra/ declares no VNet at all yet. It is also IMMUTABLE, so this is not a flip-in-place:
// turning it on is part of the private-networking change (spec §11), it recreates
// cae-foundrygate-prod, and the Container App's ingress FQDN changes with it. The parameter
// exists so that work inherits the wiring, not because the switch is cheap. #196.
param containerAppsZoneRedundant = false
// Zone-redundant Functions storage: the deployment package and the host's own state.
param functionsStorageSku = 'Standard_ZRS'
// Standard ACR: 100 GB included storage and higher throughput than Basic's 10 GB, which one
// image per deploy fills quickly. Premium only adds geo-replication and private link.
param containerRegistrySku = 'Standard'
// Standard: custom domain + SLA for the admin UI.
param staticWebAppSku = 'Standard'
// Irreversible once on — which is the point for prod.
param keyVaultPurgeProtection = true
param keyVaultSoftDeleteRetentionInDays = 90
