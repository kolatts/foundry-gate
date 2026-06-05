# Audit logging on all admin and key lifecycle actions

> GitHub: #12  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic ensures that every mutating admin action and every APIM key lifecycle event writes a structured `AuditLog` row. Audit logging is implemented as a thin service (`IAuditService`) that is injected into controllers and domain services. Rather than cross-cutting middleware, explicit call sites are used so the audit record carries meaningful context (what changed, who did it, the target entity ID). The epic also wires the `GET /audit` admin endpoint so operators can query the log with filters.

## Approach

### Wire IAuditService writes to all mutating endpoints and key lifecycle actions (#42)
Define `IAuditService` with a single `LogAsync(AuditAction action, string actorId, string targetEntityType, string targetEntityId, string? details = null)` method. Implement it as `AuditService` which creates and saves an `AuditLog` entity via `Foundry GateDbContext`. Inject `IAuditService` into every controller and service that performs a mutating action and add `await _audit.LogAsync(...)` calls at each mutation point. The full list of audit points: user provisioning, user activate/deactivate, group create/update/delete, group member add/remove, quota policy set, quota reset, quota request submit/approve/reject, key provision/rotate/revoke, system config update, Entra sync runs. Register `IAuditService` as scoped. Add `GET /admin/audit` with filters for `actorId`, `action`, `targetEntityType`, date range, and pagination.

Files expected to be created or modified:
- `src/FoundryGate.Api/Services/IAuditService.cs`
- `src/FoundryGate.Api/Services/AuditService.cs`
- `src/FoundryGate.Api/Controllers/AuditController.cs`
- `src/FoundryGate.Api/Controllers/UsersController.cs`
- `src/FoundryGate.Api/Controllers/GroupsController.cs`
- `src/FoundryGate.Api/Controllers/QuotaRequestsController.cs`
- `src/FoundryGate.Api/Controllers/KeysController.cs`
- `src/FoundryGate.Api/Controllers/InternalController.cs`
- `src/FoundryGate.Api/Program.cs` (register AuditService)

## Verification
- [ ] `dotnet build` passes
- [ ] Every mutation listed above produces a row in `AuditLog`
- [ ] `GET /admin/audit` returns filtered results correctly
- [ ] Audit writes do not break the primary operation if they throw (fire-and-log pattern)
- [ ] Actor ID is always the authenticated user's `oid` claim, not a display name
