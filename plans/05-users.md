# Users endpoints and Entra auto-provisioning

> GitHub: #5  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic delivers the full users surface of the API: a developer-facing `GET /users/me` that auto-provisions a user record on first login using the Entra ID claims in the bearer token, and a suite of admin endpoints for listing users, viewing a user's detail and quota state, and toggling their active status. Auto-provisioning is the key behaviour here — it means operators never need to manually create user records; a valid Entra login is sufficient to join the system.

## Approach

### Implement GET /users/me with first-login auto-provisioning flow (#28)
Read the `oid` (object ID) and `preferred_username` claims from the validated JWT using `HttpContext.User`. Query the `Users` table by `EntraObjectId`. If no record exists, create one with `Status = Active`, `CreatedAt = UtcNow`, and default quota settings derived from the active SystemConfiguration defaults, then write a `UserProvisioned` audit log entry. Return a `UserResponse` DTO including the user's current `QuotaAllocation` for the running period. Wrap the provisioning logic in a scoped `IUserProvisioningService` to keep the controller thin and the logic testable. The endpoint requires the `RequireDeveloper` policy.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/UsersController.cs`
- `src/FoundryGate.Api/Services/IUserProvisioningService.cs`
- `src/FoundryGate.Api/Services/UserProvisioningService.cs`

### Implement admin user management endpoints: list, detail, quota, activate/deactivate (#29)
Add admin-only endpoints under the same `UsersController`: `GET /users` (paginated list with optional search by name/email, using `PagedResult<UserResponse>`), `GET /users/{id}` (full detail including group memberships and current quota allocation), `GET /users/{id}/quota` (quota breakdown showing which level resolved the allocation), `POST /users/{id}/activate` and `POST /users/{id}/deactivate` (toggle `UserStatus`, write audit log). All admin endpoints require the `RequireAdmin` policy. Use `IQueryable` with `AsNoTracking` for list queries and explicit `Include` for detail queries.

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
