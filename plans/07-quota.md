# Quota resolution logic and allocation endpoints

> GitHub: #7  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic implements the five-level quota resolution hierarchy that is the core business logic of FoundryGate. The resolved token limit is written to `QuotaAllocation` and, crucially, is also pushed to APIM: when a user exhausts their quota, FoundryGate suspends their APIM subscription via the Management API — APIM then returns 401 on all subsequent AI calls until the subscription is re-enabled at the monthly reset. `IsHardStopped` is a DB mirror of the APIM suspension state, not the enforcement mechanism itself. This means enforcement is always APIM-side, with no lag-risk of passing traffic that should be blocked.

## Approach

### Implement five-level quota resolution logic and write to QuotaAllocation (#32)
Create `IQuotaResolutionService` that accepts a `userId` and billing period, walks five levels (user unlimited → user override → group unlimited → max group quota → system default), and upserts a `QuotaAllocation` row. Also inject `IApimKeyService` so that when the resolved limit changes for an active user the service can push the new limit into APIM's token counter cache via `cache-store-value` (see APIM policy section below). Use a DB transaction to prevent concurrent double-writes. Expose `QuotaLevel` enum on `QuotaAllocation` so the UI can explain to the developer why they have the quota they have.

The APIM enforcement model: the `llm-token-limit` policy on the APIM product uses `counter-key="@(context.Subscription.Id)"` with the per-user limit stored in APIM's internal cache under the key `quota-{subscriptionId}`. When FoundryGate resolves a new limit it calls `cache-store-value` via the APIM Management API to update that key. When `TokensUsed >= AllocatedTokens` (detected by the usage sync Function), FoundryGate **suspends** the APIM subscription via `PATCH /subscriptions/{sid}?state=suspended` — APIM immediately returns 401 to the user. `IsHardStopped = true` mirrors this suspension state.

Files expected to be created or modified:
- `src/FoundryGate.Api/Services/IQuotaResolutionService.cs`
- `src/FoundryGate.Api/Services/QuotaResolutionService.cs`

### Implement QuotaAllocation read endpoints and manual reset (#33)
`GET /quota/allocations/me` returns the caller's current allocation (limit, used, remaining, quota level source, `IsHardStopped`). `GET /quota/allocations/{userId}` (admin) returns the same for any user. `GET /quota/allocations` (admin) is a paged list of all current-period allocations. `POST /quota/reset` (admin, idempotent) re-runs resolution for all active users against the current calendar month: for each user, upsert `QuotaAllocation` with `TokensUsed = 0`, set `IsHardStopped = false`, and re-enable any suspended APIM subscriptions via `PATCH .../state=active`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/QuotaController.cs`
- `src/FoundryGate.Api/Services/IQuotaAllocationService.cs`
- `src/FoundryGate.Api/Services/QuotaAllocationService.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] A user with a group policy gets the group limit, not the system default
- [ ] A user with a direct unlimited flag returns `AllocatedTokens = null`
- [ ] When `TokensUsed >= AllocatedTokens`, the APIM subscription is suspended (verified in Azure portal)
- [ ] Monthly reset re-enables suspended subscriptions and zeros `TokensUsed`
- [ ] Manual reset (`POST /quota/reset`) is idempotent — running twice produces no duplicate rows
