# Quota resolution logic and allocation endpoints

> GitHub: #7  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic implements the five-level quota resolution hierarchy that is the core business logic of FoundryGate. The resolved token limit is written to `QuotaAllocation` and, crucially, is also pushed to APIM: when a user exhausts their quota, Foundry Gate suspends their APIM subscription via the Management API — APIM then returns 401 on all subsequent AI calls until the subscription is re-enabled at the monthly reset. `IsHardStopped` is a DB mirror of the APIM suspension state, not the enforcement mechanism itself. This means enforcement is always APIM-side, with no lag-risk of passing traffic that should be blocked.

## Approach

### Implement five-level quota resolution logic and write to QuotaAllocation (#32)
Create `IQuotaResolutionService` that accepts a `userId` and billing period, walks five levels (user unlimited → user override → group unlimited → max group quota → system default), and upserts a `QuotaAllocation` row. Also inject `IApimKeyService` so that when the resolved limit changes for an active user the service can push the new limit into APIM's token counter cache via `cache-store-value` (see APIM policy section below). Use a DB transaction to prevent concurrent double-writes. Expose `QuotaLevel` enum on `QuotaAllocation` so the UI can explain to the developer why they have the quota they have.

The APIM enforcement model: the `llm-token-limit` policy on the APIM product uses `counter-key="@(context.Subscription.Id)"` with the per-user limit stored in APIM's internal cache under the key `quota-{subscriptionId}`. When Foundry Gate resolves a new limit it calls `cache-store-value` via the APIM Management API to update that key. When `TokensUsed >= AllocatedTokens` (detected by the usage sync Function), Foundry Gate **suspends** the APIM subscription via `PATCH /subscriptions/{sid}?state=suspended` — APIM immediately returns 401 to the user. `IsHardStopped = true` mirrors this suspension state.

Files expected to be created or modified:
- `src/FoundryGate.Api/Services/IQuotaResolutionService.cs`
- `src/FoundryGate.Api/Services/QuotaResolutionService.cs`

### Implement QuotaAllocation read endpoints and manual reset (#33)
`GET /quota/allocations/me` returns the caller's current allocation (limit, used, remaining, quota level source, `IsHardStopped`). `GET /quota/allocations/{userId}` (admin) returns the same for any user. `GET /quota/allocations` (admin) is a paged list of all current-period allocations. `POST /quota/reset` (admin, idempotent) re-runs resolution for all active users against the current calendar month: for each user, upsert `QuotaAllocation` with `TokensUsed = 0`, set `IsHardStopped = false`, and re-enable any suspended APIM subscriptions via `PATCH .../state=active`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/QuotaController.cs`
- `src/FoundryGate.Api/Services/IQuotaAllocationService.cs`
- `src/FoundryGate.Api/Services/QuotaAllocationService.cs`

## Direction update (implemented in the #32/#33 PR)

The APIM-side story above is superseded by the #7 direction-update comment and plans/24: enforcement
is APIM's `llm-token-limit` on **tier products**, `token-quota` accepts literals only, so there is no
`cache-store-value` push and no suspension on exhaustion. What landed instead:

- `Services/Quota/QuotaResolutionService` walks the five levels and upserts `QuotaAllocation`, now
  recording `ResolvedLevelType`, `TierProductId` and `IsGatewayCapped`. The numeric quota is mapped to
  a tier by `GatewayTierMapper` from `Gateway:Tiers` (`Configuration/GatewayTierOptions.cs`, defaults
  in `appsettings.json` mirroring `infra/main.bicep`, parity-tested). **A budget is a tier
  (D-013):** a quota must equal a tier cap or be unlimited — `GatewayTierMapper.EnsureValidQuota`
  guards every write path (400 otherwise), `GET /quota/tiers` lists the choices, and a legacy value
  matching no cap is enforced at the next tier up and flagged `IsGatewayCapped` (never a read failure).
- `IGatewayTierSync` is the seam to move the subscription between tier products; the real
  `ApimGatewayTierSync` is #118 (a `NullGatewayTierSync` is registered until then).
- Nothing in resolution saves; `QuotaAllocationService` (the `/quota` orchestrator) commits mutation +
  audit atomically. `POST /quota/reset` re-resolves active users for the current UTC month, preserves
  `TokensUsed` on existing rows (the gateway window resets itself — #10 direction update), clears
  `IsHardStopped`, stamps `ResetDate`, one `quota.reset` audit row per run.
- `BillingPeriod` lives in `FoundryGate.Domain.Quota` so the Functions host (#38) can share it; how
  Functions reaches the resolution service at all is #119.

## Verification
- [x] `dotnet build FoundryGate.sln -c Release` passes with zero warnings; `dotnet format --verify-no-changes` clean
- [x] A user with a group policy gets the group limit, not the system default — `QuotaResolutionServiceTests.Level4_*` / `Level3_*`
- [x] A user with a direct unlimited flag returns `AllocatedTokens = null` — `Level1_*`; and a user override beats an unlimited group — `Level2_*` (pinned)
- [x] Superseded: "When `TokensUsed >= AllocatedTokens`, the APIM subscription is suspended" — exhaustion is a real-time gateway 403 on the tier product (#7 direction update); suspension is offboarding-only. Replaced by: numeric quota → tier product mapping incl. boundaries and the gateway-capped flag — `GatewayTierMapperTests`, `QuotaResolutionServiceTests.Resolved_quota_is_mapped_*`
- [x] Superseded: "Monthly reset re-enables suspended subscriptions and zeros `TokensUsed`" — the gateway window resets itself and `TokensUsed` is a reconciliation mirror that the reset preserves (#10 direction update) — `QuotaAllocationServiceTests.ResetAsync_*`. Re-enabling deactivation suspensions belongs to the lifecycle wave (#65/#66)
- [x] Manual reset (`POST /quota/reset`) is idempotent — running twice produces no duplicate rows, preserves `TokensUsed`, audits once per run — `QuotaAllocationServiceTests.ResetAsync_is_idempotent_*`, `QuotaEndpointTests.Admin_reset_is_idempotent_*`
- [x] `IGatewayTierSync` invoked only for users with an APIM subscription whose tier changed (or is unknown) — `QuotaResolutionServiceTests.Tier_sync_*`
- [x] Endpoint auth contract (401 anonymous / 403 non-admin / 403 unprovisioned on `/me` / 200 admin), paging, `/me` auto-create, admin 404s — `QuotaEndpointTests`
- [x] `SystemConfiguration[DefaultMonthlyTokenQuota]` missing or non-numeric fails with a clear configuration error, never a silent 0 — `QuotaResolutionServiceTests.System_default_*`
- [x] Schema parity: `QuotaAllocations.sql` matches the entity (new columns + `(PeriodYear, PeriodMonth)` index) — `SchemaParityTests`
- [ ] Deferred (#118): moving a subscription between tier products changes the enforced budget at the live gateway
- [ ] Deferred (#119): the Functions monthly reset reaches the same resolution logic (HTTP to `POST /quota/reset` vs. move to Data)
