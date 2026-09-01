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
// AUTO-PAUSE: serverless SKUs (GP_S_*) get autoPauseDelay/minCapacity, derived from the
// SKU name so a provisioned SKU can never be sent serverless-only properties. Whether the
// database actually pauses depends on nothing touching it for that long — the API's
// readiness probe deliberately does not (modules/container-app.bicep); periodic Functions
// jobs (#84) should keep their cadence above the pause delay or accept an always-on vCore.
param sqlServerName string
param sqlDatabaseName string
param location string
param tags object = {}

@description('Object id of the Entra security group that administers the server (Entra-only auth; no SQL login exists).')
param entraAdminGroupObjectId string

@description('Display name of that group — becomes the server admin login name.')
param entraAdminGroupName string

@description('Database SKU: { name, tier, family?, capacity? }. GP_S_* names are serverless (auto-pause enabled); anything else is provisioned.')
param databaseSku object

@description('Serverless only: minutes of inactivity before auto-pause (-1 disables auto-pause).')
param autoPauseDelayMinutes int = 60

@description('Serverless only: minimum vCores while active, as a decimal string (0.5 is the floor for 1 max vCore).')
param serverlessMinCapacity string = '0.5'

@allowed(['Local', 'Zone', 'Geo', 'GeoZone'])
@description('Backup storage redundancy. Local for dev, Geo for prod.')
param backupStorageRedundancy string = 'Local'

@description('Max database size in bytes (default 32 GB).')
param maxSizeBytes int = 34359738368

param zoneRedundant bool = false

var serverless = startsWith(databaseSku.name, 'GP_S_')

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
    autoPauseDelay: serverless ? autoPauseDelayMinutes : null
    minCapacity: serverless ? json(serverlessMinCapacity) : null
  }
}

output sqlServerName string = sqlServer.name
output sqlServerId string = sqlServer.id
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = database.name
output serverless bool = serverless
@description('Entra-auth connection string (no secret in it). Same shape _deploy-database.yml computes for the dacpac deploy.')
output entraConnectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
