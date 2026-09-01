---
title: API Surface
description: All Foundry Gate REST endpoints, auth requirements, and response shapes.
---

Base path: `/api/v1`. All endpoints require a valid Entra ID bearer token. Admin endpoints additionally require the `FoundryGate.Admin` app role.

## Users

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/users` | Admin | List all users, paged. Query: `?search=&page=&pageSize=` |
| `GET` | `/users/me` | Any | Own profile + current quota. Auto-provisions on first call. |
| `GET` | `/users/{id}` | Admin | User detail including group memberships and allocation |
| `PUT` | `/users/{id}/quota` | Admin | Set `MonthlyTokenQuota` or `IsUnlimited` |
| `POST` | `/users/{id}/activate` | Admin | Re-activate user — runs full provision pipeline |
| `POST` | `/users/{id}/deactivate` | Admin | Deactivate user — deletes APIM subscription |
| `POST` | `/users/sync` | Admin | Trigger Entra bulk user sync |

## Groups

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/groups` | Admin | List all groups with member count |
| `POST` | `/groups` | Admin | Create group |
| `GET` | `/groups/{id}` | Admin | Group detail + member list |
| `PUT` | `/groups/{id}` | Admin | Update name, description, quota |
| `DELETE` | `/groups/{id}` | Admin | Delete group (does not delete members) |
| `POST` | `/groups/{id}/members` | Admin | Add user to group |
| `DELETE` | `/groups/{id}/members/{userId}` | Admin | Remove user from group |
| `POST` | `/groups/{id}/sync-entra` | Admin | Sync members from linked Entra group |

## Quota

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/quota/allocations` | Admin | All current-period allocations, paged (`?page=&pageSize=`), ordered by user display name; includes `userDisplayName`/`userEmail` |
| `GET` | `/quota/allocations/me` | Any | Own current allocation. Resolved and created on the first call of the month (`tokensUsed = 0`, no `resetDate`). `403` until `GET /users/me` has provisioned the caller. |
| `GET` | `/quota/allocations/{userId}` | Admin | Specific user's current allocation. Read-only: `404` if the user has none for this period yet. |
| `POST` | `/quota/reset` | Admin | Manually trigger monthly reset (idempotent) — see below |

"Current period" is always the UTC calendar month, matching the gateway's `token-quota` window.

`QuotaAllocationResponse` carries, besides the numeric fields (`allocatedTokens` — null when
unlimited — `tokensUsed`, `percentUsed`, `isHardStopped`):

- `resolvedLevelType` — which level of the five-level precedence chain produced the quota:
  `0` UserUnlimited, `1` UserOverride, `2` GroupUnlimited, `3` GroupMax, `4` SystemDefault.
  User-level settings (0, 1) always beat group-level ones (2, 3).
- `tierProductId` — the APIM tier product (`standard` / `power` / `unlimited`) the numeric
  quota mapped to: unlimited → `unlimited`; otherwise the smallest tier whose configured cap
  (`Gateway:Tiers`, see [Configuration](/foundry-gate/reference/configuration/)) is ≥ the quota.
  **This is what the gateway enforces**, not `allocatedTokens`.
- `isGatewayCapped` — `true` when the quota exceeds every finite tier cap. The developer is
  placed on the largest finite tier and the gateway returns `403` at *that tier's* cap, below
  their nominal quota. Raise the tier caps in infra or grant unlimited to clear it.

`POST /quota/reset` re-resolves every **active** user's allocation for the current UTC month
in one transaction: rows that do not exist are created with `tokensUsed = 0`; rows that do
exist are re-resolved (`allocatedTokens`, level, tier, capped flag) but **keep their reconciled
`tokensUsed`** — the gateway's monthly window resets itself, so zeroing the mirror mid-month
would only make dashboards lie. Every touched row gets `isHardStopped = false` and
`resetDate = now`. Exactly one audit row (`quota.reset`, attributed to the calling admin, details
`{ usersResetCount, periodYear, periodMonth, createdCount, tierSyncCount }`) per run. Returns
`{ usersResetCount, periodYear, periodMonth, resetDate }`. Running it twice in a month produces
the same row count.

## Quota Increase Requests

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/requests` | Any | Own requests; Admin sees all. Filter: `?status=Pending|Approved|Rejected` |
| `POST` | `/requests` | Any | Submit a quota increase request |
| `GET` | `/requests/{id}` | Owner or Admin | Request detail |
| `PUT` | `/requests/{id}/approve` | Admin | Approve with optional notes — updates quota immediately |
| `PUT` | `/requests/{id}/reject` | Admin | Reject with optional notes |

## Keys

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/keys/me` | Any | Own key info (masked, last 4 visible) |
| `POST` | `/keys/me/rotate` | Any | Rotate own key — returns new key value once |
| `POST` | `/keys/{userId}/rotate` | Admin | Rotate any user's key |
| `POST` | `/keys/{userId}/provision` | Admin | Provision a new key for a user with no active key |
| `DELETE` | `/keys/{userId}` | Admin | Revoke key (user stays active) |

## Foundry

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/foundry/models` | Any | List available model deployments (developer view) |
| `GET` | `/foundry/deployments` | Admin | Full deployment list with SKU and capacity |
| `POST` | `/foundry/deployments` | Admin | Create a new model deployment |
| `DELETE` | `/foundry/deployments/{name}` | Admin | Delete a model deployment |

## Admin

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/config` | Admin | All SystemConfiguration keys |
| `PUT` | `/config/{key}` | Admin | Update a configuration value |
| `GET` | `/audit` | Admin | Audit log, paged. Filter: `?actor=&action=&from=&to=` |
| `GET` | `/dashboard` | Admin | Summary stats for the dashboard |
