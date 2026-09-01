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
- [x] `dotnet build` passes — zero warnings, whole solution (`TreatWarningsAsErrors`);
      `FoundryGate.Tests.Predeployment` 119/119 (82 before #42 + 37 new); `dotnet format
      --verify-no-changes` clean.
- [ ] Every mutation listed above produces a row in `AuditLog` — **deferred to each endpoint
      wave, by design.** #42 lands `IAuditService`, `GET /audit`, and the scaffolding every wave
      shares; the mutating endpoints themselves don't exist yet (#28–#41, #61, #65–#66). Each of
      those PRs wires its own `LogAsync` call and asserts the row in its endpoint tests — the
      call-site list in this plan's Approach section is the checklist they work from.
- [x] `GET /api/v1/audit` returns filtered results correctly — `AuditEndpointTests`: 401
      anonymous, 403 authenticated non-admin, 200 admin; paging (`page`/`pageSize`, `TotalCount`,
      `TotalPages`), newest-first ordering, and each filter (`actorUserId`, `action`, `targetType`,
      `targetId`, inclusive `fromDate`/`toDate`, plus a non-UTC-offset date compared by instant).
      Route is `/api/v1/audit` per spec §4.6, not this plan's original `/admin/audit`.
- [x] ~~Audit writes do not break the primary operation if they throw (fire-and-log pattern)~~ →
      **design decision reversed, deliberately.** `LogAsync` adds the row to the SAME `AppDbContext`
      and the caller's `SaveChangesAsync` commits mutation + audit atomically — no separate save,
      no swallowed failure. A fire-and-log audit can leave a mutation with no audit row, or an
      audit row for a mutation that rolled back; for an audit trail, failing the request is the
      correct outcome. `AuditServiceTests.LogAsync_adds_to_the_context_but_does_not_save…` pins it.
- [x] Actor ID is always the authenticated user's `oid` claim, not a display name — amended for
      the int-PK model (#92): `AuditLog.ActorUserId` is the `User` FK resolved from the caller's
      `oid` by `ICurrentUserAccessor` (both oid claim types; `CurrentUserAccessorTests`), and the
      row is attached via the `ActorUser` navigation so a caller whose `User` was just `Add`ed but
      not yet saved (first-login auto-provisioning, #28) is attributed correctly in the same save
      (`AuditWriterTests` / `AuditServiceTests` "auto_provision_pattern"). A caller with no `User`
      row at all cannot write a human-attributed audit row — `UnauthorizedAccessException` → 403,
      the same status and message (`call GET /users/me first`) `GetRequiredUserAsync` gives, so the
      two "no row" paths never disagree; system jobs use `IAuditWriter.AddSystem(…)`.
      `ActorDisplayName` on the response is a read-time join, never stored.
- [x] `AuditLog.OccurredDate` indexed (`[Index]` + `IX_AuditLogs_OccurredDate` in
      `dbo/Tables/AuditLogs.sql`; `SchemaParityTests` verifies the pair) — every page of
      `GET /audit` orders by it and the date-range filter seeks on it.

### Deviations from this plan's original text (#42)
- **One writer in Data, a wrapper in Api.** The plan put `IAuditService` in Api, but `FoundryGate
  .Functions` (monthly reset, usage sync — #38/#39) references Data and Domain only, so the
  actor-agnostic row builder is `FoundryGate.Data.Audit.IAuditWriter`/`AuditWriter` (`Add(User
  actor, …)`, `Add(int actorUserId, …)`, `AddSystem(…)`; `TimeProvider` timestamp; camelCase JSON
  with cycles ignored), registered in `AddFoundryGateData`. Api's `IAuditService` is the thin
  current-user-attributing wrapper (`LogAsync(action, targetType, targetId, details, ct)`) plus the
  admin read query — rather than the plan's `(AuditAction enum, string actorId, …)`. Actions stay
  string constants (`AuditActions`, per #91's reasoning); `details` is any object so call sites
  pass `new { before, after }` instead of hand-building JSON; both return the added `AuditLog`.
- **File layout.** `Services/Audit/{IAuditService,AuditService,AuditServiceCollectionExtensions}.cs`
  and `Services/Identity/…` — the `Services/<Area>/` convention from CONVENTIONS.md, not the flat
  `Services/IAuditService.cs` this plan listed. Registration is one line in
  `Services/ApiServiceCollectionExtensions.AddFoundryGateApiServices()`, not in Program.cs.
- **Scaffolding beyond audit**, because #42 is the first `/api/v1` controller and seven waves
  follow it: `ApiControllerBase`, `ICurrentUserAccessor`, `ToPagedAsync`, `AuditTargetTypes`,
  the remaining `AuditActions` later waves need, `GatewayTiers`, and the `ApiTestFactory` /
  `TestAuthHandler` integration-test harness. Documented in CONVENTIONS.md "API
  service/controller conventions".
