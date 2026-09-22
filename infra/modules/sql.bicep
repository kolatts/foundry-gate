// Azure SQL logical server + the single FoundryGate database (CONVENTIONS.md: one
// database, one DbContext, no sharding).
//
// AUTH: Entra-only. `azureADOnlyAuthentication: true` with an Entra security GROUP as the
// server administrator, and no SQL login/password anywhere (CONVENTIONS.md mandates
// `Authentication=Active Directory Default`; this supersedes the "SQL admin
// login/password" wording in #43/#44). Membership of that group is what grants admin
// access, so two things cannot be expressed in Bicep and are operator/pipeline steps:
//   1. Put the deploying/CI principal (the OIDC app registration) in the admin group so
//      the dacpac deploy (`_deploy-database.yml`) can connect (#109).
//   2. Create contained users for the API and Functions managed identities inside the
//      database (`CREATE USER [id-foundrygate-api-<env>] FROM EXTERNAL PROVIDER` +
//      db_datareader/db_datawriter) — a post-dacpac step in the db deploy (#106).
//
// FIREWALL: only the "Allow Azure services" rule is declared here (0.0.0.0-0.0.0.0), which
// is what lets Container Apps / Functions connect without a VNet. Runner/developer IP rules
// are created at deploy time by the CLI `ip setup` command (#96) and are NOT declared here
// on purpose: undeclared child resources are left alone by an incremental deployment, so a
// re-run never wipes a rule the pipeline just added.
//
// TIER: PROVISIONED ONLY. Serverless (GP_S_*) is deliberately not supported here, because its
// whole saving is conditional on something a template cannot promise: that nothing touches the
// database for the entire pause delay. Dev ran GP_S_Gen5 with a 60-minute delay from 2026-09-05
// and never paused once — UsageSyncFunction's `0 */15 * * * *` timer (#84) reconnects four times
// an hour, so the delay never elapsed. It billed a full vCore around the clock: ~$10.50/day,
// ~$315/month, for a database holding 33 MB (#277). A provisioned SKU bills the same whether it
// is touched or not, which makes the bill a property of this file rather than a property of a
// timer schedule somebody may change later without thinking about SQL at all.
//
// Basic (5 DTU, 2 GB, ~$4.90/month) is what dev uses; prod uses provisioned General Purpose.
// InfraSqlTierTests pins this: a GP_S_* name in main.bicep or any parameter file fails the build.
param sqlServerName string
param sqlDatabaseName string

// NOT necessarily the deployment's primary region. Azure closes individual regions to NEW
// SQL logical servers without warning and without it showing up in any quota or SKU query —
// the only way to find out is to try, and the error is `ProvisioningDisabled` /
// `RegionDoesNotAllowProvisioning`, not a quota error. Both `eastus2` and `eastus` were
// closed to this subscription on 2026-09-05, which is what pushed dev's SQL to `centralus`
// (#241). Existing servers in a closed region keep working; only creation is blocked.
//
// IMMUTABLE ONCE DEPLOYED. `Microsoft.Sql/servers.location` cannot be changed in place, so
// this is a one-shot decision per environment: changing it later makes the incremental
// deployment FAIL rather than move anything, and the recovery is exporting the database and
// re-creating it on a new server. Choose it deliberately before an environment's first
// deploy — after that it is a migration, not a parameter.
param location string
param tags object = {}

@description('Object id of the Entra security group that administers the server (Entra-only auth; no SQL login exists).')
param entraAdminGroupObjectId string

@description('Display name of that group — becomes the server admin login name.')
param entraAdminGroupName string

@description('Provisioned database SKU: { name, tier, family?, capacity? } — Basic for dev, GP_Gen5_2 for prod. Serverless GP_S_* names are rejected by InfraSqlTierTests; see the TIER note above.')
param databaseSku object

@allowed(['Local', 'Zone', 'Geo', 'GeoZone'])
@description('Backup storage redundancy. Local for dev, Geo for prod.')
param backupStorageRedundancy string = 'Local'

@description('Max database size in bytes. Must fit the SKU — Basic caps at 2 GB (2147483648) and ARM fails the deployment rather than clamping a larger value.')
param maxSizeBytes int = 34359738368

param zoneRedundant bool = false


resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: union(tags, { 'fg-component': 'sql' })
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: entraAdminGroupName
      sid: entraAdminGroupObjectId
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

// "Allow Azure services and resources to access this server" — the magic 0.0.0.0 rule.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: union(tags, { 'fg-component': 'sql' })
  sku: databaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: maxSizeBytes
    zoneRedundant: zoneRedundant
    requestedBackupStorageRedundancy: backupStorageRedundancy
  }
}

output sqlServerName string = sqlServer.name
output sqlServerId string = sqlServer.id
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = database.name
@description('Entra-auth connection string (no secret in it). Same shape _deploy-database.yml computes for the dacpac deploy.')
output entraConnectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
