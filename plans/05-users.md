# Users endpoints and Entra auto-provisioning

> GitHub: #5  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic delivers the full users surface of the API: a developer-facing `GET /users/me` that auto-provisions a user record on first login using the Entra ID claims in the bearer token, and a suite of admin endpoints for listing users, viewing a user's detail and quota state, and toggling their active status. Auto-provisioning is the key behaviour here — it means operators never need to manually create user records; a valid Entra login is sufficient to join the system.

## Approach

### Implement GET /users/me with first-login auto-provisioning flow (#28)
Read the `oid` claim from the validated JWT. Query `Users` by `EntraObjectId`. If found, update display fields and return. If not found, call `IUserLifecycleService.ProvisionAsync(trigger: FirstLogin, entraClaims)` which runs the full provision pipeline (see **plan #21** for the authoritative sequence): Graph lookup → INSERT User → resolve quota → APIM subscription create → write audit log. The service handles APIM failure compensation (rolls back the User INSERT if APIM fails). Return a `UserProfileDto` with the masked key hint and current allocation.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/UsersController.cs`
- `src/FoundryGate.Api/Services/IUserProvisioningService.cs`
- `src/FoundryGate.Api/Services/UserProvisioningService.cs`

### Implement admin user management endpoints: list, detail, quota, activate/deactivate (#29)
Add admin-only endpoints: `GET /users` (paginated, searchable), `GET /users/{id}` (full detail), `GET /users/{id}/quota` (quota breakdown with resolved level).

`POST /users/{id}/deactivate` → calls `IUserLifecycleService.DeprovisionAsync(trigger: AdminDeactivation, userId)`: **deletes** the APIM subscription, nulls out key fields on User, sets `IsActive = false`, hard-stops current QuotaAllocation, cancels any Pending increase requests (see **plan #21** for full sequence).

`POST /users/{id}/activate` → calls `IUserLifecycleService.ReactivateAsync(userId)`: sets `IsActive = true` then runs the full provision pipeline (quota resolution + APIM subscription create). If an orphan APIM subscription named `foundrygate-{userId}` already exists, reuse it rather than creating a duplicate.

All admin endpoints require `RequireAdmin` policy. Use `AsNoTracking` for list queries, explicit `Include` for detail.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/UsersController.cs`
- `src/FoundryGate.Api/Services/IUserAdminService.cs`
- `src/FoundryGate.Api/Services/UserAdminService.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] First call to `GET /users/me` with a new Entra token creates a User row
- [ ] Subsequent calls return the existing user without creating duplicates
- [ ] `GET /users` returns paginated results and respects search params
- [ ] Deactivating a user blocks their subsequent API access
- [ ] Audit log contains a row for each provisioning and status change event
