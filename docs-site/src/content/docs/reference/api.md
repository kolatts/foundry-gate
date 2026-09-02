---
title: API Surface
description: All Foundry Gate REST endpoints, auth requirements, and response shapes.
---

Base path: `/api/v1`. All endpoints require a valid Entra ID bearer token. Admin endpoints additionally require the `FoundryGate.Admin` app role.

## Users

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/users` | Admin | List users, paged, ordered by display name. Query: `?search=&isActive=&page=&pageSize=` |
| `GET` | `/users/me` | Any | Own profile, current quota, masked key and CLI connection details. Auto-provisions on first call. |
| `GET` | `/users/{id}` | Admin | User detail: the list row plus group memberships, current-period allocation and masked key |
| `PUT` | `/users/{id}/quota` | Admin | Set `monthlyTokenQuota` or `isUnlimited`; re-resolves the period and moves the APIM subscription to the new tier product |
| `POST` | `/users/{id}/activate` | Admin | Re-activate user — runs the full provision pipeline |
| `POST` | `/users/{id}/deactivate` | Admin | Deactivate user — deletes APIM subscription, hard-stops the allocation, rejects pending requests |
| `POST` | `/users/sync` | Admin | Reconcile `Users` against the people assigned to the FoundryGate app in Entra. Returns `{ addedCount, updatedCount, deactivatedCount, skippedGroupAssignmentCount }` |

### `GET /users/me` — first login provisions everything

A developer's first call creates their whole footprint in **one transaction**: the `User` row (from
the token's Entra claims, enriched from Microsoft Graph when `Entra:Enabled`), their allocation for
the current month, and their APIM subscription under the tier that allocation resolved to. If the
gateway refuses the subscription, nothing is written — no half-provisioned user, no `502` with a row
left behind. Later calls are idempotent: display name and email are refreshed from the token,
`lastSyncedDate` is stamped, and the same key and allocation come back.

The response carries `quota` (the same shape as `/quota/allocations/me`), `apiKey` (masked to the
last four characters — the plaintext only ever comes from `/keys/*`), and `cliConfig`:
`gatewayBaseUrl` (the gateway origin, empty on a host with no gateway configured),
`anthropicBasePath` (`/anthropic`), `openAiBasePath` (`/openai/v1`), and `modelAliases` — currently
always empty, because the alias map lives only in the gateway's Bicep
([#153](https://github.com/kolatts/foundry-gate/issues/153)); use
[CLI setup](/foundry-gate/getting-started/cli-setup/) for model names until it lands.

Errors: `403` when the caller's account is deactivated, `409` when two first logins for the same
identity race (retry — [#154](https://github.com/kolatts/foundry-gate/issues/154) will absorb it),
`502` when the gateway or Graph failed, `503` when APIM key management is not configured on the host.

### Deactivate and activate are the lifecycle pipelines

`POST /users/{id}/deactivate` is the full offboarding sequence, committed atomically: **delete** the
APIM subscription (there is no suspended state), clear the stored key, `isActive = false`, hard-stop
the current allocation, and reject every Pending quota-increase request of theirs with the note
"User deactivated" and no reviewer. `POST /users/{id}/activate` runs the provision pipeline in
reverse: `isActive = true`, quota re-resolved, and a fresh APIM subscription minted — adopting an
orphan named `foundrygate-{userId}` if a previous deprovision left one behind, rather than creating a
duplicate. The new key is **not** returned to the admin; the developer reveals their own with
`POST /keys/me/reveal`. Both are `409` when the user is already in the target state and `404` for an
unknown user; a gateway failure is `502` and leaves the user as they were.

To take a key away without deactivating anyone, use `DELETE /keys/{userId}` instead — that leaves the
user active, their quota untouched, and their requests pending.

`PUT /users/{id}/quota` accepts only a configured tier cap or `isUnlimited` (see
[Quota](#quota) below); `isUnlimited: true` clears any numeric override. Because a budget *is* a
gateway tier, a change here re-scopes the developer's APIM subscription to the new tier product —
their key is unchanged, so nothing needs reconfiguring — and a gateway that refuses the move fails
the request rather than leaving the database claiming a tier nobody enforces.

`POST /users/sync` is idempotent and pull-only. Users assigned to the application but missing locally are inserted with defaults and **no** API key (keys are provisioned on first login or by an admin); users present in both have `displayName`/`email`/`employeeId` refreshed and `lastSyncedDate` stamped (every matched user counts as *updated*); users present locally but no longer assigned run the **same deprovision pipeline as `POST /users/{id}/deactivate`** — APIM subscription deleted, `isActive = false`, allocation hard-stopped, pending requests rejected — so a departed employee never keeps a working gateway key. Rows are never deleted and never auto-reactivated if the person later returns; an admin must re-activate them. The whole run (adds, updates, departures) commits in one transaction, with one `users.synced` audit row attributed to the caller and system-attributed `user.deactivated` / `key.revoked` rows per departure.

**Group-assigned access suspends departure detection.** Only *user* assignees are read today; an app-role assignment granted to a *group* is not expanded to its members yet ([#121](https://github.com/kolatts/foundry-gate/issues/121)). Because a user assigned through such a group is invisible to the sync, "not in the user list" cannot mean "departed" — so when the directory reports one or more group assignments the run still adds and updates users but deactivates nobody, returns `deactivatedCount: 0` with `skippedGroupAssignmentCount > 0`, names the groups in a warning log and in the audit row (`departureDetectionSuspended: true`). Assign developers individually to the application if you need departure detection before #121 lands.

Errors: `400` when `Entra:Enabled` is false on the host, `403` when the calling admin has no `User` row yet (call `GET /users/me` first), `409` when the directory returns no assigned users while active users exist locally (nothing is changed — almost always a wrong service principal or a missing Graph role).

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
| `GET` | `/quota/tiers` | Any | The configured budget tiers `{ productId, displayName, monthlyTokenQuota, isUnlimited }` — the only values a quota may take |
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
- `tierProductId` — the APIM tier product (`standard` / `power` / `unlimited`) this budget
  *is*. **The rule: a finite monthly token quota must equal a configured tier's cap
  (`Gateway:Tiers`, see [Configuration](/foundry-gate/reference/configuration/)), or be
  unlimited.** Every write path that accepts a quota (`PUT /users/{id}/quota`, group
  create/update, request approval) rejects anything else with `400` listing the allowed
  values; `GET /quota/tiers` is the list to offer. The tier is what the gateway enforces,
  so under this rule `allocatedTokens` and the enforced cap are the same number.
- `isGatewayCapped` — `true` only for a legacy or hand-edited value that matches no tier
  cap. Reads never fail on such a row: it is enforced at the next tier up (or the largest
  finite tier) and flagged so an admin can correct it to a tier. To offer a new budget size,
  add a tier in both places: `quotaTiers` in `infra/main.bicep` (creates the APIM product and
  its policy) and `Gateway:Tiers` in the Api configuration (a predeployment test keeps them in
  step).

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
| `GET` | `/keys/me` | Any | Own key info (masked, last 4 visible; `isProvisioned: false` when none). Served from a stored hint — no decryption |
| `POST` | `/keys/me/reveal` | Any | Decrypt and return own full key once. Audited (`key.revealed`), never cached. `404` when no key |
| `POST` | `/keys/me/rotate` | Any | Rotate own key — regenerates **both** APIM keys (primary and never-issued secondary), returns the new primary once. `404` no key; `409` if the APIM subscription vanished |
| `POST` | `/keys/{userId}/rotate` | Admin | Rotate any user's key (same semantics) |
| `POST` | `/keys/{userId}/provision` | Admin | Provision a key for a user with none, under `?tier=standard\|power\|unlimited` (default `standard`). Returns plaintext once. `409` key exists or user deactivated; `400` unknown tier; reuses an orphaned APIM subscription with fresh keys |
| `DELETE` | `/keys/{userId}` | Admin | Revoke key only: APIM subscription deleted, stored key cleared, `key.revoked` audited. **User stays active** and can be re-provisioned; `204` even when no key existed. Deactivation is `POST /users/{id}/deactivate` |

Callers of every `/keys/me` route must already have a FoundryGate user row (`GET /users/me` provisions one) — otherwise `403`. The plaintext key is stored encrypted (Key Vault RSA key wrapping; see [Configuration](/reference/configuration/)) and appears in exactly one response per mint or reveal. Reveal is not yet rate-limited (tracked in #136). Provisioning is race-safe: two concurrent `provision` calls for one user cannot both mint — the second gets `409`.

## Foundry

The gateway runs one Azure AI Foundry account per region (`Gateway__FoundryAccountNames__{i}`; index 0 is the primary), so deployments are addressed as `{accountName}/{deploymentName}`. The API manages deployments in those accounts only.

**Ownership split.** Claude (Anthropic-format) deployments are managed by the infrastructure deploy end to end — `infra/main.bicep` creates them once, and the API neither creates nor deletes them. The API **lists every deployment** and **manages OpenAI-format deployments**. Why: Claude deployments need a Marketplace attestation the Azure SDK cannot send (issues #107/#126), a re-PUT drives one to `Failed`, delete/recreate churn has wedged a whole subscription, and Bicep can only recreate *all* of an account's deployments — so deleting one Claude deployment from the API would be a one-way door whose only recovery damages its neighbours (decision log E-006/E-007).

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/foundry/models` | Any | Developer view: distinct deployment names with model, version, format and provisioning state. A model deployed in several regions is listed once — `Succeeded` if any region serves it. Served from a 30-second in-memory cache (invalidated by every create/delete); a configured account that is missing in Azure is skipped, not fatal. |
| `GET` | `/foundry/deployments` | Admin | Every deployment in every configured account (account, name, model format/name/version, SKU, capacity in thousands of TPM, provisioning state, created/modified). Primary account first, then by name. Always live (no cache). |
| `GET` | `/foundry/deployments/{accountName}/{deploymentName}` | Admin | One deployment — poll this after a create until `provisioningState` is `Succeeded`. |
| `POST` | `/foundry/deployments` | Admin | Create one **OpenAI-format** deployment in one account. Body: `accountName`, `deploymentName`, `modelFormat` (`OpenAI`; default), `modelName`, `modelVersion`, `skuName`, `capacity` (thousands of TPM). `201` + `Location`; the body reflects ARM's initial state (usually `Creating`). |
| `DELETE` | `/foundry/deployments/{accountName}/{deploymentName}` | Admin | Delete one **OpenAI-format** deployment (`204`). Never recreates. In-flight requests pinned to the name get the backend's 404 once ARM finishes. |

Rules the mutation paths enforce (CLAUDE.md "Anthropic deployments are create-once"; decision log E-006/E-007):

- **An existing name is `409 Conflict`** — the API checks first and never re-PUTs an existing deployment. Replace an OpenAI deployment by deleting it and creating it again, as two explicit, audited actions.
- **Anthropic is `400 Bad Request` on both create and delete** (`modelFormat: Anthropic` in the body; an existing deployment whose ARM `model.format` is `Anthropic` on delete). Claude deployments are the infrastructure deploy's (#126 tracks lifting this).
- An `accountName` that is not one of the gateway's configured accounts is `400` on create and `404` on read/delete. A configured account that Azure does **not** have is `503 Service Unavailable — feature not configured` on the admin paths (the message names the account, never the resource group); `/foundry/models` skips it.
- **`503 Service Unavailable — feature not configured`** on every `/foundry/*` route when the `Gateway__*` section is absent (local dev without a gateway). The `detail` names the keys to set, so the UI can tell "not set up" from "broken".
- Every create and delete writes an audit entry (`foundry.deployment.created` / `foundry.deployment.deleted`, target type `FoundryDeployment`, target id `{accountName}/{deploymentName}`). The admin must have loaded the app once (`GET /users/me`) so the entry has an actor — otherwise the mutation is refused (`403`) before Azure is touched. Once Azure has accepted the change, the audit entry is written regardless of the client disconnecting.

## Admin

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/config` | Admin | All SystemConfiguration keys |
| `PUT` | `/config/{key}` | Admin | Update a configuration value |
| `GET` | `/audit` | Admin | Audit log, paged. Filter: `?actor=&action=&from=&to=` |
| `GET` | `/dashboard` | Admin | Summary stats for the dashboard |
