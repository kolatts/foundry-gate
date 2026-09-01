// Key Vault for the control plane: RBAC authorization mode (no access policies), soft
// delete always on, purge protection param-driven (irreversible once enabled — on for
// prod, off for dev so a torn-down dev vault can be purged and its name reused).
//
// Deliberately creates NO secrets. A Bicep-managed secret is re-PUT with its template
// value on every deploy, which would overwrite whatever an operator set out of band; and
// the control plane has no genuinely required secret today — SQL is Entra-only, storage is
// identity-based, Graph access should be granted to the API's managed identity rather than
// a client secret. Secrets that do become necessary are set out of band and referenced
// from appsettings as @KeyVault(SecretName) (CONVENTIONS.md §Configuration & auth).
//
// The optional key `fg-apim-key-encryption` is the wrapping key for APIM subscription keys
// stored in SQL (#95, spec §11 "Key Vault key wrapping"); the API identity gets Key Vault
// Crypto User on the vault (modules/control-plane-rbac.bicep) so it can wrap/unwrap.
param keyVaultName string
param location string
param tags object = {}

@description('Purge protection. Irreversible once on; recommended for prod, off for dev so the vault can be purged after a teardown.')
param enablePurgeProtection bool = false

@minValue(7)
@maxValue(90)
@description('Soft-delete retention in days (7 for dev, 90 for prod).')
param softDeleteRetentionInDays int = 7

@description('Create the RSA wrapping key used to encrypt APIM subscription keys at rest (#95).')
param createKeyEncryptionKey bool = true

@description('Name of the wrapping key. Referenced by the API through the keyEncryptionKeyUri output.')
param keyEncryptionKeyName string = 'fg-apim-key-encryption'

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: keyVaultName
  location: location
  tags: union(tags, { 'fg-component': 'keyvault' })
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    // ARM rejects an explicit `false` here once the property has ever been set; null
    // leaves it unset, which is the only representation of "off".
    enablePurgeProtection: enablePurgeProtection ? true : null
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource keyEncryptionKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' = if (createKeyEncryptionKey) {
  parent: keyVault
  name: keyEncryptionKeyName
  properties: {
    kty: 'RSA'
    keySize: 3072
    keyOps: ['wrapKey', 'unwrapKey']
    attributes: { enabled: true }
  }
}

output keyVaultName string = keyVault.name
output keyVaultId string = keyVault.id
output keyVaultUri string = keyVault.properties.vaultUri
@description('Versionless key URI of the APIM key-wrapping key (empty when not created). Versionless so key rotation needs no redeploy.')
output keyEncryptionKeyUri string = createKeyEncryptionKey ? keyEncryptionKey!.properties.keyUri : ''
