# APIM subscription key provisioning, rotation, and revocation

> GitHub: #9  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic wires Foundry Gate to Azure API Management so that approved developers get a real APIM subscription key they can use to call the AI Foundry gateway. Key operations — provision, rotate (self-service and admin), reveal, and revoke — are all mediated through the Foundry Gate API using the Azure Resource Manager SDK with the API's managed identity, so no APIM credentials are stored in the application. Key metadata lives on `User` (`ApimSubscriptionId` — the ARM resource id, not secret; `ApimSubscriptionKey` — the primary key **encrypted** by `IKeyProtector`, #95; `ApimSubscriptionKeyHint` — last four characters for the masked view; `ApimKeyIssuedDate`). The plaintext key is never persisted and never logged.

Direction (Sep 2026, #81/#82): a developer's subscription is created under their **quota-tier product** (`GatewayTiers.Standard/Power/Unlimited`), and a tier change re-scopes the subscription to another product (`IApimKeyService.MoveToProductAsync`). There is no suspended state anywhere in the lifecycle (#116): quota exhaustion is a real-time gateway `403`, and every people-lifecycle exit (admin deactivation, Entra departure, admin key revocation) **deletes** the subscription. Re-activation re-runs provisioning and reuses an orphaned subscription if one exists.

## Approach

### Encrypt `User.ApimSubscriptionKey` at rest (#95)
`Services/Security/IKeyProtector` with two implementations selected by `KeyProtection:Provider`: `KeyVaultKeyProtector` wraps the key bytes directly with RSA-OAEP-256 under the RSA key at `Gateway:KeyEncryptionKeyUri` (the `fg-apim-key-encryption` key infra creates, #43/#111; the API identity is Key Vault Crypto User) and stores `kv1:{versionedKeyId}:{base64}`; `DataProtectionKeyProtector` (ASP.NET Core Data Protection, `dp1:{token}`) is permitted in `local` only. Selection is fail-fast at startup (`KeyProtectorFactory` + `AddResolveOnStartup`): `KeyVault` without a key URI, or `DataProtection` outside `local`, refuses to boot. The envelope records the exact Key Vault key *version* that wrapped each row, so a Key Vault key rotation needs no re-encryption sweep — old rows unwrap with their version, new writes use the new one. `ApimSubscriptionId` is an address, not a credential, and stays plain.

Files:
- `src/FoundryGate.Api/Services/Security/{IKeyProtector,KeyEnvelope,KeyVaultKeyProtector,DataProtectionKeyProtector,KeyProtectorFactory,SecurityServiceCollectionExtensions}.cs`
- `src/FoundryGate.Api/Configuration/AppSettings.cs` (`GatewayOptions`, `KeyProtectionOptions`)
- `src/FoundryGate.Data/Entities/User.cs` + `src/FoundryGate.Database/dbo/Tables/Users.sql` (`ApimSubscriptionKey` widened to 1000; `ApimSubscriptionKeyHint`, `ApimKeyIssuedDate` added)

### Implement APIM key provisioning using Azure SDK and Managed Identity (#36)
`Services/Keys/IApimManagementClient` is the thin seam over `Azure.ResourceManager.ApiManagement` (`ArmApimManagementClient`; addressed by `Gateway:SubscriptionId/ResourceGroup/ApimName` — the `Gateway__*` env vars infra sets, #108). `IApimKeyService.ProvisionAsync(user, tierProductId)` creates the subscription `foundrygate-{UserId}` (`Domain/Keys/ApimSubscriptionNames`, shared with the Functions reconciliation job) scoped to `/products/{tier}`, display name carrying the email, `active`; stores the encrypted primary key, resource id, hint and issue date; audits `key.provisioned`; saves; returns the plaintext once (`ApiKeyRevealResponse`). A second provision is `409`. If APIM already holds a subscription with that name (an orphan from a save that failed after APIM succeeded), it is reused: re-scoped if needed and **both keys regenerated**, so the orphan's keys are dead. APIM is called before the row is touched, so an ARM failure leaves the database untouched. Quota resolution and the lifecycle orchestration around this call belong to plan 21 (#64/#65).

Files:
- `src/FoundryGate.Api/Services/Keys/{IApimManagementClient,ArmApimManagementClient,UnconfiguredApimManagementClient,IApimKeyService,ApimKeyService,KeysServiceCollectionExtensions}.cs`
- `src/FoundryGate.Api/Controllers/KeysController.cs`
- `src/FoundryGate.Domain/Keys/ApimSubscriptionNames.cs`
- `Directory.Packages.props` / `FoundryGate.Api.csproj` (`Azure.ResourceManager.ApiManagement`, `Azure.Security.KeyVault.Keys`)

### Implement key rotation (self-service and admin), reveal, and revocation (#37)
`POST /keys/me/rotate` and `POST /keys/{userId}/rotate` (admin): regenerate **both** the primary and the secondary key (#117 — the secondary is never issued, but rotating it bounds its lifetime to the primary's), re-encrypt the new primary, update `User.ApimSubscriptionKey`/`Hint`/`IssuedDate`, audit `key.rotated`, return the new value once. `GET /keys/me` returns the masked key from the stored hint (no decryption). `POST /keys/me/reveal` decrypts and returns the full key once, audited `key.revealed`, never cached. `DELETE /keys/{userId}` (admin) is **key-only revocation** (#116): delete the APIM subscription, clear the four key fields, audit `key.revoked`; `User.IsActive` is untouched and the user can be re-provisioned. Idempotent (`204` even with no key). Full deactivation is `POST /users/{id}/deactivate` (plan 21), which calls `RevokeAsync` as its first step. A subscription that vanished from APIM behind FoundryGate's back turns rotate/move into a `409` with "revoke and re-provision" guidance.

Files:
- `src/FoundryGate.Api/Controllers/KeysController.cs`
- `src/FoundryGate.Api/Services/Keys/ApimKeyService.cs`

## Verification
- [x] `dotnet build FoundryGate.sln -c Release` passes with zero warnings
- [x] Provisioning creates an APIM subscription under the tier product and returns the key value once (`ApimKeyServiceTests`, `KeysEndpointTests`)
- [x] A second provision attempt while a key is active returns `409`; an unknown tier `400`; a deactivated user `409`
- [x] An orphan subscription is reused (re-scoped, both keys regenerated) instead of erroring
- [x] Rotation regenerates both keys, updates `ApimKeyIssuedDate`, and the old plaintext no longer matches
- [x] Revocation deletes the subscription, clears every key field, leaves `IsActive = true`, and is idempotent (`204`)
- [x] `GET /keys/me` never exposes more than the last four characters; reveal returns the full key and is audited
- [x] Audit log captures provision, rotation, reveal, revocation and tier-change events, attributed to the caller, with no key material in `Details`
- [x] Plaintext never reaches the logger (`Plaintext_key_material_never_reaches_the_logger_or_an_audit_row`)
- [x] Key protector round-trips under both providers; `DataProtection` is refused outside `local`; `KeyVault` without a key URI fails startup; envelope fits the 1000-char column for RSA-4096
- [ ] Live: provision/rotate/reveal/revoke against a real APIM + Key Vault — manual checklist in #132
