# Quota resolution logic and allocation endpoints

> GitHub: #7  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic implements the five-level quota resolution hierarchy that is the core business logic of FoundryGate. When a user makes an API call, their effective token quota for the current period must be determined by walking five levels in priority order: (1) user-specific override, (2) group policy (highest group wins), (3) tenant-wide policy, (4) system default from SystemConfiguration, (5) a hard-coded fallback. The resolved value is materialised into a `QuotaAllocation` row for the current period. The epic also delivers the read endpoints that expose allocation state and a manual reset endpoint for admins.

## Approach

### Implement five-level quota resolution logic and write to QuotaAllocation (#32)
Create a `IQuotaResolutionService` that accepts a `userId` and billing period, then walks the five-level hierarchy using the data already in the database. The service reads the user's direct `QuotaPolicy` override, then queries all `GroupMembership` rows for the user and their associated `QuotaPolicy` rows (picking the highest limit), then falls back through tenant and system levels. The resolved `TokenLimit` and the `QuotaLevel` enum value that provided it are both written to (or updated on) the `QuotaAllocation` row for `(userId, year, month)`. Call this service from `GET /users/me` auto-provisioning and from the monthly reset background service. Use a database transaction to prevent double-writes under concurrent requests.

Files expected to be created or modified:
- `src/FoundryGate.Api/Services/IQuotaResolutionService.cs`
- `src/FoundryGate.Api/Services/QuotaResolutionService.cs`

### Implement QuotaAllocation read endpoints and manual reset (#33)
Add `GET /users/{id}/quota/allocation` (return the current period's `QuotaAllocation` including resolved level, token limit, tokens used, and remaining) and `GET /users/{id}/quota/allocation/history` (list previous periods). Add `POST /users/{id}/quota/allocation/reset` (admin only) that zeros `TokensUsed` on the current allocation and writes an audit log entry. All three endpoints live on `UsersController` (or a nested `QuotaController`) and require at minimum the `RequireDeveloper` policy (admins can access any user, developers only their own).

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/QuotaController.cs`
- `src/FoundryGate.Api/Services/IQuotaAllocationService.cs`
- `src/FoundryGate.Api/Services/QuotaAllocationService.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] A user with a group policy gets the group limit, not the system default
- [ ] A user with a direct override gets that limit regardless of group
- [ ] Concurrent provisioning calls do not create duplicate `QuotaAllocation` rows
- [ ] Manual reset zeroes `TokensUsed` and writes audit log
- [ ] History endpoint returns previous periods in descending order
