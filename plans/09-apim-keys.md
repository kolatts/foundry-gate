# APIM subscription key provisioning, rotation, and revocation

> GitHub: #9  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic wires FoundryGate to Azure API Management so that approved developers get a real APIM subscription key they can use to call the AI Foundry gateway. Key operations — provision, rotate (self-service and admin), and revoke — are all mediated through the FoundryGate API using the Azure Resource Manager SDK with Managed Identity, so no APIM credentials are stored in the application. The `ApiKey` table tracks key metadata (APIM subscription ID, status, created/rotated timestamps) but never stores the raw key value.

## Approach

### Implement APIM key provisioning using Azure SDK and Managed Identity (#36)
Create an `IApimKeyService` backed by `ApiManagementSubscriptionResource` from `Azure.ResourceManager.ApiManagement`. On `POST /keys/provision` (developer-only, one active key per user limit enforced), the service calls `CreateOrUpdateAsync` on the APIM management plane to create a subscription scoped to the user's APIM user ID, then stores the resulting APIM `subscriptionId` and masked key hint in the `ApiKey` table. The actual key value is returned once in the response body and never persisted. Use `DefaultAzureCredential` (resolves to Managed Identity in production, developer credentials locally). Read APIM resource details (`ResourceGroupName`, `ServiceName`, `ProductId`) from `IConfiguration`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/KeysController.cs`
- `src/FoundryGate.Api/Services/IApimKeyService.cs`
- `src/FoundryGate.Api/Services/ApimKeyService.cs`
- `src/FoundryGate.Api/appsettings.json` (Apim section)
- `src/FoundryGate.Api/FoundryGate.Api.csproj` (Azure.ResourceManager.ApiManagement package)

### Implement key rotation (self-service and admin) and revocation (#37)
Add `POST /keys/{id}/rotate` (self-service: user can only rotate their own active key; generates a new APIM key via `RegeneratePrimaryKeyAsync`, updates `RotatedAt` in the `ApiKey` table, writes audit log, returns new key value once). Add `POST /admin/keys/{id}/rotate` (admin can rotate any user's key). Add `POST /keys/{id}/revoke` (self-service or admin) which calls the APIM management plane to deactivate the subscription, sets `ApiKey.Status = Revoked` and `RevokedAt`, and writes an audit log entry. Revoked keys cannot be rotated or re-provisioned until explicitly re-activated by an admin.

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
