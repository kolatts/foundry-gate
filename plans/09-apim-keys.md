# APIM subscription key provisioning, rotation, and revocation

> GitHub: #9  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic wires Foundry Gate to Azure API Management so that approved developers get a real APIM subscription key they can use to call the AI Foundry gateway. Key operations — provision, rotate (self-service and admin), and revoke — are all mediated through the Foundry Gate API using the Azure Resource Manager SDK with Managed Identity, so no APIM credentials are stored in the application. The `ApiKey` table tracks key metadata (APIM subscription ID, status, created/rotated timestamps) but never stores the raw key value.

## Approach

### Implement APIM key provisioning using Azure SDK and Managed Identity (#36)
Create an `IApimKeyService` backed by `ApiManagementSubscriptionResource` from `Azure.ResourceManager.ApiManagement`. On `POST /keys/provision` (developer-only, one active key per user limit enforced), the service calls `CreateOrUpdateAsync` on the APIM management plane to create a subscription named `foundrygate-{userId}` scoped to the configured product, then stores the APIM `subscriptionId` and a masked key hint in `User.ApimSubscriptionId` / `User.ApimSubscriptionKey`. The actual key value is returned once in the response body and never persisted in plaintext. After provisioning, call `IQuotaResolutionService` to write the initial `QuotaAllocation` and push the user's resolved limit into the APIM cache key `quota-{subscriptionId}`. The subscription starts in `active` state. Use `DefaultAzureCredential`. Read APIM resource details from `IConfiguration`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/KeysController.cs`
- `src/FoundryGate.Api/Services/IApimKeyService.cs`
- `src/FoundryGate.Api/Services/ApimKeyService.cs`
- `src/FoundryGate.Api/appsettings.json` (Apim section)
- `src/FoundryGate.Api/FoundryGate.Api.csproj` (Azure.ResourceManager.ApiManagement package)

### Implement key rotation (self-service and admin) and revocation (#37)
`POST /keys/me/rotate` and `POST /keys/{userId}/rotate` (admin): call `RegeneratePrimaryKeyAsync` on the APIM subscription, re-encrypt the new key via Key Vault, update `User.ApimSubscriptionKey`, write audit log, return new key value once. `DELETE /keys/{userId}` (admin) and the user deactivation path: call APIM to **delete** the subscription entirely (not suspend — deletion means the key stops working immediately and cannot be re-enabled). Set `User.ApimSubscriptionId` and `User.ApimSubscriptionKey` to null, `User.IsActive = false`, `QuotaAllocation.IsHardStopped = true`. Suspension (for quota exhaustion) is distinct from deletion (for account deactivation): suspended subscriptions can be re-enabled; deleted ones require a new `POST /keys/{userId}/provision`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/KeysController.cs`
- `src/FoundryGate.Api/Services/ApimKeyService.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] Provisioning creates an APIM subscription and returns the key value
- [ ] A second provision attempt while a key is active returns `409`
- [ ] Rotation generates a new key and updates `RotatedAt`
- [ ] Revoked key status is reflected in `GET /keys` response
- [ ] Audit log captures provision, rotation, and revocation events
