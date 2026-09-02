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
| `POST` | `/users/sync` | Admin | Reconcile `Users` against the people assigned to the FoundryGate app in Entra. Returns `{ addedCount, updatedCount, deactivatedCount, skippedGroupAssignmentCount, failedCount }` |

Every user shape carries three dates that are easy to confuse: `createdDate` (the row was made),
`lastSyncedDate` (an Entra sync last touched it) and `lastLoginDate` (the person last loaded their own
profile; `null` means they have never signed in — the signal an offboarding sweep wants).

### `GET /users/me` — first login provisions everything

A developer's first call creates their whole footprint in **one transaction**: the `User` row (from
the token's Entra claims, enriched from Microsoft Graph when `Entra:Enabled`), their allocation for
the current month, and their APIM subscription under the tier that allocation resolved to. If the
gateway refuses the subscription, nothing is written — no half-provisioned user, no `502` with a row
left behind. Later calls are idempotent and, in the common case, **read-only**: display name and
email are written back only when the token actually disagrees with the stored values. `lastSyncedDate`
is *not* touched — it means "an Entra sync last saw this user", and a profile load is not a sync.

The honest "this account is in use" column is **`lastLoginDate`**: null until the person's first
profile load, then stamped — but only when the stored value is already more than **15 minutes** old,
so a UI that reloads the profile on every navigation does not turn each read into an `UPDATE` on
`Users`. It is therefore accurate to within that window, never to the second, which is all an
offboarding or licence review needs. It appears on every user shape `GET /users` and `GET /users/{id}`
return.

The response carries `quota` (the same shape as `/quota/allocations/me`), `apiKey` (masked to the
last four characters — the plaintext only ever comes from `/keys/*`), and `cliConfig`:
`gatewayBaseUrl` (the gateway origin, empty on a host with no gateway configured),
`anthropicBasePath` (`/anthropic`), `openAiBasePath` (`/openai/v1`), and `modelAliases` — currently
always empty, because the alias map lives only in the gateway's Bicep
([#153](https://github.com/kolatts/foundry-gate/issues/153)); use
[CLI setup](/foundry-gate/getting-started/cli-setup/) for model names until it lands.

**Two tabs are not an error.** When two first logins for the same identity arrive together, both find
no row and both provision; `Users.EntraObjectId` is unique, so the loser's insert fails, its whole
transaction rolls back (no orphan row, no orphan allocation) and it then **returns the winner's
profile with a `200`** — the developer never sees the race. Only the unique-index collision is
absorbed: any other failed save still surfaces, and calling the first-login provision for an identity
that already had a row remains a `409`, because that one is a programming error rather than a race.
The loser does still wait out the winner's gateway round trip, because the provision pipeline holds
its transaction across it ([#179](https://github.com/kolatts/foundry-gate/issues/179)).

Errors: `403` when the caller's account is deactivated, `502` when the gateway or Graph failed, `503`
when APIM key management is not configured on the host.

### Deactivate and activate are the lifecycle pipelines

`POST /users/{id}/deactivate` is the full offboarding sequence, committed atomically: **delete** the
APIM subscription (there is no suspended state), clear the stored key, `isActive = false`, hard-stop
the current allocation, and reject every Pending quota-increase request of theirs with the note
"User deactivated" and no reviewer. `POST /users/{id}/activate` runs the provision pipeline in
reverse: `isActive = true`, quota re-resolved, and a fresh APIM subscription minted — adopting an
orphan named `foundrygate-{userId}` if a previous deprovision left one behind, rather than creating a
duplicate. The new key is **not** returned to the admin; the developer reveals their own with
`POST /keys/me/reveal`. Re-activation also clears the `isHardStopped` flag its own deactivation set,
so the developer's gauge is live again immediately rather than at the next monthly reset. Both are
`409` when the user is already in the target state and `404` for an unknown user.

**Where a gateway failure leaves you.** Activation is all-or-nothing: a `502` rolls the whole thing
back and the user stays deactivated. Deactivation cannot be, because an APIM `DELETE` has no undo — so
it deletes the subscription **first**, outside the transaction that then records the deactivation. A
`502` therefore means the deletion did not happen and nothing changed; a failure *after* it (rare, and
logged at Error) leaves the key revoked and audited but the user still marked active. Re-running
`POST /users/{id}/deactivate` finishes the job — revocation tolerates a subscription that is already
gone.

To take a key away without deactivating anyone, use `DELETE /keys/{userId}` instead — that leaves the
user active, their quota untouched, and their requests pending.

`PUT /users/{id}/quota` accepts only a configured tier cap or `isUnlimited` (see
[Quota](#quota) below); `isUnlimited: true` clears any numeric override. Because a budget *is* a
gateway tier, a change here re-scopes the developer's APIM subscription to the new tier product, and a
gateway that refuses the move is a `502` that leaves the database showing the old budget rather than
claiming a tier nobody enforces.

:::caution[Unverified: does a re-scope preserve the key?]
FoundryGate assumes re-scoping a subscription to another product leaves its keys alone, so a tier
change needs no CLI reconfiguration. That assumption is proven against the in-memory APIM the test
suite uses — **not** against real Azure API Management. It is on the live checklist in
[#132](https://github.com/kolatts/foundry-gate/issues/132). If a real re-scope turns out to rotate the
key, a tier change becomes a key event: FoundryGate would have to create the subscription on the new
product, delete the old one, and hand the developer a new key (audited `key.rotated`).
:::

`POST /users/sync` is idempotent and pull-only. Users assigned to the application but missing locally are inserted with defaults and **no** API key (keys are provisioned on first login or by an admin); users present in both have `displayName`/`email`/`employeeId` refreshed and `lastSyncedDate` stamped (every matched user counts as *updated*); users present locally but no longer assigned run the **same deprovision pipeline as `POST /users/{id}/deactivate`** — APIM subscription deleted, `isActive = false`, allocation hard-stopped, pending requests rejected — so a departed employee never keeps a working gateway key. Rows are never deleted and never auto-reactivated if the person later returns; an admin must re-activate them. Each departure is its own unit of work: one that the gateway refuses is counted in `failedCount`, logged, and skipped — the rest of the run still lands and the next run retries it, so a single bad ARM call can never undo deletions that already succeeded. Adds and updates then commit together with one `users.synced` audit row attributed to the caller; each departure writes system-attributed `user.deactivated` / `key.revoked` rows of its own.

**Group-assigned access is expanded.** An app-role assignment granted to a *group* — the common enterprise pattern of assigning `SG_AI_Developers` to the FoundryGate enterprise application — is flattened to that group's **transitive** user members (nested groups included) and merged with the directly assigned users, de-duplicated, before any of the reconciliation above runs ([#121](https://github.com/kolatts/foundry-gate/issues/121)). Assigning developers through a group is a first-class configuration: adds, updates and departures all work.

**Only a group the run could not read suspends departure detection.** If the API cannot read one of those groups — Graph refuses it (a missing `GroupMember.ReadBasic.All`, or a group that has been deleted), or Graph is briefly unreachable and the SDK's retries are exhausted — the run has seen only part of the population, so "not in the user list" cannot mean "departed". For that run the deactivation step is skipped entirely: users are still added and updated, `deactivatedCount` is `0`, `skippedGroupAssignmentCount` counts the unreadable groups, and they are named in a warning log and in the audit row (`departureDetectionSuspended: true`). Grant the role (or unassign the deleted group) and the next run deactivates normally. On a healthy tenant `skippedGroupAssignmentCount` is always `0`.

Errors: `403` when the calling admin has no `User` row yet (call `GET /users/me` first), `409` when the directory returns no assigned users while active users exist locally (nothing is changed — almost always a wrong service principal or a missing Graph role), `503` when `Entra:Enabled` is false on the host (the message names the setting and the Graph roles to grant — the request is fine, the host is not configured for the feature).

## Groups

A group is a budget policy for a set of people: levels 3-4 of the [quota resolution chain](#quota) read
group membership, so every write on this surface can move a developer's allocation and the APIM tier
product their key is scoped to. Every route is admin-only.

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/groups` | Admin | Groups ordered by name, paged (`?page=&pageSize=`), each with `memberCount`. `?search=` matches name and description, case-insensitively |
| `POST` | `/groups` | Admin | Create a group. `201` with a `Location` header pointing at `GET /groups/{id}` |
| `GET` | `/groups/{id}` | Admin | `{ group, members }` — the group plus its full roster, ordered by display name |
| `PUT` | `/groups/{id}` | Admin | Update `name`, `description`, `isUnlimited`, `monthlyTokenQuota`. Returns the updated group |
| `DELETE` | `/groups/{id}` | Admin | Delete the group and its memberships (`204`). Needs `?force=true` if it still has members |
| `GET` | `/groups/{id}/members` | Admin | The roster, paged, ordered by display name |
| `POST` | `/groups/{id}/members` | Admin | Add a user by `{ "userId": n }`. Returns the new membership |
| `DELETE` | `/groups/{id}/members/{userId}` | Admin | Remove a membership (`204`) |
| `POST` | `/groups/{id}/sync-entra` | Admin | Reconcile one group against its linked Entra group |
| `POST` | `/groups/sync-entra` | Admin | Reconcile every Entra-linked group; one summary each, including for a group that failed |

**Quota values are tiers.** `monthlyTokenQuota` on create and update must be `null` (unlimited) or
exactly one configured tier cap, or the request is `400` listing the allowed values — the same rule
`PUT /users/{id}/quota` follows. `GET /quota/tiers` is the list to offer.

**Names are unique.** A duplicate is `409` — case-insensitively, and enforced by the unique index
`IX_Groups_Name` so two concurrent creates cannot both win. **`entraGroupId` is unique too** (among the
groups that have one): one Entra group backs at most one FoundryGate group, or both would claim its
members and hand them the larger of the two quotas. A second link is `409`.

**An Entra-linked group's roster is read-only through this API.** `POST /groups/{id}/members` and
`DELETE /groups/{id}/members/{userId}` return `409` when the group has an `entraGroupId`: the edit
would be applied and then silently undone by the next `sync-entra`, which is a worse answer than
refusing. Change the membership in the directory group and sync. The group's *policy* — name,
description, quota — stays editable; the directory owns who is in the group, not what the group is
worth.

`isEntraSynced` in the response is derived from `entraGroupId` being set; it is not a separate stored
flag that could disagree with the link.

**Every quota-visible write re-resolves the members it affects**, in the same transaction as the
mutation and its audit row: a quota change on `PUT /groups/{id}` re-resolves every current member;
`DELETE /groups/{id}` re-resolves the former members *after* their memberships are removed (they fall
back down the chain, usually to the system default); adding or removing one member re-resolves that
one. Only **active** users are re-resolved — a deactivated developer has no key to enforce against, and
`GET /quota/allocations/me` refuses to mint them an allocation either. Members whose tier actually
changes have their APIM subscription moved to the new tier product; members whose tier is unchanged are
not touched.

**Deleting a group never deletes users**, and never clears their individual quota overrides — only the
group's own contribution to their resolution goes away. `EntraGroupId` is set at creation and is not
updatable: re-pointing a synced group at a different directory group would silently rewrite its whole
roster on the next sync, so delete and recreate instead.

Audit rows: `group.created`, `group.updated` (with before/after values), `group.deleted`,
`group.member-added`, `group.member-removed`, `group.entra-synced` — all with `targetType: "Group"` and
the group id as `targetId`.

### Entra group sync

`POST /groups/{id}/sync-entra` pulls the linked Entra group's membership (transitively, so nested
groups flatten to their people) and reconciles it. Idempotent: a second run with an unchanged directory
reports zeros. Returns
`{ groupId, addedCount, removedCount, skippedUnknownUserCount, succeeded, error, errorType }` —
`succeeded` is always `true` here (and `errorType` `"None"`), because a single-group failure is the
HTTP status. They are only ever otherwise inside a `POST /groups/sync-entra` summary (see below).

- Directory members missing from this group are added with **`addedByUserId: null`** — the system
  actor. The directory chose the membership, not the calling admin, and the audit trail should not
  claim otherwise; the UI can label such rows "from Entra".
- Memberships whose user is no longer in the Entra group are deleted. The `User` row is untouched —
  leaving a synced group is not leaving the company.
- Directory members with **no FoundryGate `User` row** are skipped, never invented, and counted in
  `skippedUnknownUserCount` (also logged at Warning). A non-zero count means those people have never
  signed in and were not imported: run `POST /users/sync` first, then sync again.
- Everyone whose membership changed is re-resolved, so a developer joining a Power group is on the
  Power product by the time the response returns.

`POST /groups/sync-entra` does the same for every group that has an `entraGroupId`, one unit of work
each, in group-id order; groups with no link are skipped and do not appear in the result. The `Users`
table is read **once for the whole run**, not once per group — nothing in this path creates a user, so
one snapshot is both cheaper and correct.

**One group's failure does not end the run.** A group whose reconciliation throws is left as it was,
the loop continues, and its summary carries `"succeeded": false` with the failure's message in
`"error"` and zeroes everywhere else; every other group reports its real counts. The call is still a
`200`, because "three of five groups reconciled and here is what went wrong with the other two" is a
more useful answer than a `500` that says nothing. Single-group `POST /groups/{id}/sync-entra` is
unchanged: its failure is the HTTP status, and it never returns `succeeded: false`.

**`errorType` says what a person has to do about it, and the two values are not interchangeable:**

| `errorType` | What happened | What to do |
|---|---|---|
| `"GraphRead"` | The read failed *before* anything outside the database was touched — Graph refused the group, or it no longer exists. Nothing was applied anywhere and the group's staged changes were discarded, so they cannot ride along on the next group's save. Logged at Warning | Fix the cause and re-run. The sync is idempotent, so that is sufficient |
| `"PostCommit"` | The APIM tier move for a member was **accepted** and the database write recording it then failed — twice, since the save is retried once on a fresh token with the pending rows still tracked. The gateway and the control plane disagree: someone is on a product their `QuotaAllocation` row does not name. Logged at **Error** with the group's full identity, plus an Error summary line for the run | Re-run to converge the database, and treat that group's reported state as untrustworthy until you have. This is the case CONVENTIONS.md's commit-point rule exists for |

A UI must render the two differently — "try again" is the right advice for one and actively
misleading for the other.

The one exception to per-group isolation is `Entra:Enabled` being false on the host: that is not a
property of any one group — every group would carry the same message — so it stays a whole-response
`503`.

Errors: `400` when the group has no `entraGroupId` (a real caller error — this group has nothing to
sync against); `404` for an unknown group; `503` when `Entra:Enabled` is false on the host — the
message names the setting and the Graph roles to grant. The 503 is deliberate: the request is
well-formed and the caller can do nothing about it, so it is the operator's problem, not theirs.

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
| `GET` | `/requests` | Any | Own requests; Admin sees all. Paged (`?page=&pageSize=`), newest first. Filters: `?status=` (`0` Pending, `1` Approved, `2` Rejected) and, for admins, `?userId=` |
| `POST` | `/requests` | Any | Submit own request. Body: `requestedQuota` (null = unlimited), `justification` (10–2000 chars). `201` + `Location` |
| `POST` | `/requests/for/{userId}` | Admin | Submit on a user's behalf — same rules, `requestedByUserId` is the admin. `201` + `Location` |
| `GET` | `/requests/{id}` | Owner or Admin | Request detail. `404` for anyone else — see below |
| `POST` | `/requests/{id}/approve` | Admin | Approve. Applies the tier to the user and re-resolves the current period immediately |
| `POST` | `/requests/{id}/reject` | Admin | Reject. Nothing about the user's quota changes |

**A request asks for a tier, not a number.** `requestedQuota` must be `null` (unlimited) or exactly
one of the configured tier caps from `GET /quota/tiers` — anything else is `400` listing the allowed
values (D-013; the same `EnsureValidQuota` guard as `PUT /users/{id}/quota`). It must also be a
genuine increase over the requester's currently resolved quota: a bigger finite tier, or unlimited.
A developer who is already unlimited has nothing larger to ask for, so their submission is `400`
too.

**Both rules are re-checked at approval, against live resolution — a request can never lower a
budget.** A user's quota is far more volatile than the tier table: `PUT /users/{id}/quota`, a group's
quota, or a change in group membership can all raise it between filing and review. So approval asks
the resolution service what the subject's budget is *now* and refuses with `409` when applying the
stored `requestedQuota` would not raise it — naming the current budget, so the reviewer can reject the
request instead. A request filed at 5M→20M and approved after an admin made that developer unlimited
would otherwise have silently demoted them. The tier check is re-run for the same reason: a request
whose value is no longer a configured tier is `400` at review time rather than persisting a budget the
gateway cannot enforce.

**Both submission and approval read live resolution, not the stored `QuotaAllocation` row.** The
allocation row is whatever the last resolution wrote — a group change since then makes it stale — so
the `currentQuota` recorded on the request, and the comparison at review time, come from re-walking
the five-level chain. That read is side-effect-free: submitting creates **no** allocation row and makes
no gateway call, so a refused submission leaves nothing behind. The developer's allocation still
appears on their first `GET /quota/allocations/me` of the month, as it always did.

**One open request per user per period.** A second submission while one is still `Pending` in the
same UTC calendar month is `409`; a decided (approved or rejected) request frees the slot at once, so
a rejected developer can re-ask with a better justification. The check is not yet backed by a database
constraint, so two simultaneous submissions can both land ([#147](https://github.com/kolatts/foundry-gate/issues/147)).

Every submission writes `quota.requested`; approval writes `quota.approved` with the before/after
quota, the quota and level resolution reported at review time, the tier product and whether the gateway
subscription was moved; rejection writes `quota.rejected`. All three carry target type `Request` and
the request id.

**Approval is the write path.** It sets the subject's `IsUnlimited` / `MonthlyTokenQuota`, then re-runs
quota resolution for the current period — which upserts the `QuotaAllocation` and moves the subject's
APIM subscription to the new tier product — before the response is written. No cron job, no lag. The
subject's reconciled `tokensUsed` is untouched. Everything (request, user, allocation, audit row)
commits in one transaction; a failed gateway move fails the approval, and once the gateway has accepted
the move the audit row and the commit are no longer cancellable by the client hanging up.

**Approve and reject claim the row.** The transition out of `Pending` is a single conditional update,
so two reviewers deciding at the same moment cannot both succeed: one wins, the other gets `409`.
Without that, a simultaneous approve and reject could leave the budget raised, the subscription moved,
the request reading `Rejected`, and two contradictory audit rows.

Status codes worth knowing: `403` when the caller has no user row yet (call `GET /users/me` first) or
is deactivated; `403` when a non-admin passes `?userId=` naming someone else; `409` when the request
was already decided (including by a reviewer racing this one), when the subject user is deactivated, or
when the subject's budget has moved past what the request asks for; `404` on `GET /requests/{id}` for a
request that belongs to someone else — deliberately identical to the response for an id that does not
exist, so the route cannot be used to enumerate other people's requests.

Approval applies to the **current** period and does not expire: a months-old pending request still
raises today's budget while reporting the period it was filed for
([#159](https://github.com/kolatts/foundry-gate/issues/159)).

Deprovisioning hooks into the same service: `IQuotaRequestService.CancelPendingForUserAsync` closes a
departing developer's pending requests so they do not sit in an admin's queue
([#65](https://github.com/kolatts/foundry-gate/issues/65)/[#66](https://github.com/kolatts/foundry-gate/issues/66)).

Spec §4.4 writes the two review actions as `PUT`; they are `POST` here — non-idempotent state
transitions whose body is not the resource, matching `POST /users/{id}/activate`,
`POST /keys/{userId}/rotate` and `POST /quota/reset` elsewhere in this API.

## Keys

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/keys/me` | Any | Own key info (masked, last 4 visible; `isProvisioned: false` when none). Served from a stored hint — no decryption |
| `POST` | `/keys/me/reveal` | Any | Decrypt and return own full key once. Audited (`key.revealed`), never cached. Rate-limited: **5 per minute per user**. `404` when no key; `429` over the limit |
| `POST` | `/keys/me/rotate` | Any | Rotate own key — regenerates **both** APIM keys (primary and never-issued secondary), returns the new primary once. Rate-limited: **3 per minute per user**. `404` no key; `409` if the APIM subscription vanished; `429` over the limit |
| `POST` | `/keys/{userId}/rotate` | Admin | Rotate any user's key (same semantics) |
| `POST` | `/keys/{userId}/provision` | Admin | Provision a key for an active user with none, under the tier **their quota resolves to** (no `?tier=`: a budget *is* a tier, so set the quota to change the product). Returns plaintext once. `409` key exists or user deactivated; reuses an orphaned APIM subscription with fresh keys |
| `DELETE` | `/keys/{userId}` | Admin | Revoke key only: APIM subscription deleted, stored key cleared, `key.revoked` audited. **User stays active** and can be re-provisioned; `204` even when no key existed. Deactivation is `POST /users/{id}/deactivate` |

Callers of every `/keys/me` route must already have a FoundryGate user row (`GET /users/me` provisions one) — otherwise `403`. The plaintext key is stored encrypted (Key Vault RSA key wrapping; see [Configuration](/reference/configuration/)) and appears in exactly one response per mint or reveal. Provisioning is race-safe: two concurrent `provision` calls for one user cannot both mint — the second gets `409`.

**Rate limits on the `/me` routes.** Reveal hands back the plaintext credential and rotate mints a new one, so a leaked bearer token could otherwise replay either indefinitely with nothing to show for it but a growing run of `key.revealed` audit rows. Both are capped per **caller identity** (the token's `oid`, not the caller's IP address — the UI sits behind a shared egress, so an address limit would throttle a whole office or nobody): 5 reveals and 3 rotations per minute. Over the limit is a `429` with a `Retry-After` header and the usual ProblemDetails body. The admin routes (`/keys/{userId}/rotate`, `/keys/{userId}/provision`, `DELETE /keys/{userId}`) are deliberately uncapped: an admin rotating a compromised team's keys is exactly the traffic a limit would get in the way of, and none of them discloses the caller's own credential.

**Rotation is committed once APIM has regenerated.** The developer's old key is dead the instant the gateway accepts the regeneration of the *primary*, so everything after that point — reading the new key back, storing it, the `key.rotated` audit row — completes even if the client disconnects. A rotation that fails after that point restores the previous stored values, logs at Error and writes a `key.rotation-failed` row naming the remedy (rotate again, or revoke and re-provision), rather than leaving a key nobody can decrypt. A request abandoned *during* that regeneration gets the same row, marked `regenerationConfirmed: false` — the gateway never said whether it acted, so the stored key is possibly rather than definitely stale.

The never-issued **secondary** key is regenerated after the new primary is stored, not before, and failing it does not fail the rotation: the developer keeps a working key, and the stale secondary is logged at Error and named on the `key.rotated` row (`keysRegenerated: ["primary"]`, `secondaryRotationError`) for the next rotation to retire.

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
| `GET` | `/config` | Admin | Every `SystemConfiguration` key, ordered by key: `{ key, value, updatedDate, updatedByUserId, updatedByDisplayName }`. The last two are `null` for a seeded key no admin has edited |
| `PUT` | `/config/{key}` | Admin | Set one key's value. Returns the row as it now stands |
| `GET` | `/audit` | Admin | Audit log, paged. Filter: `?actor=&action=&from=&to=` |
| `GET` | `/dashboard` | Admin | Summary stats for the dashboard. `?fresh=true` bypasses the 30-second cache |

### `PUT /config/{key}`

Body: `{ "value": "..." }`. The key is matched case-insensitively; an unknown one is `404`. The
value is validated **per key** before it is stored — a configuration editor that accepts a value
the system cannot use only moves the failure somewhere harder to find — and stored **normalized**
(trimmed, booleans lower-cased, URLs and resource ids without a trailing slash), so two admins
typing `True` and `true` leave the same row behind.

Emptiness is one of those per-key decisions, not a blanket rule: `{ "value": "" }` **clears** a key
whose rule allows empty (the URL and resource-id keys — how you unwire a resource) and is a `400`
for one that does not (a quota, a reset day and a feature flag have no empty form).

| Key | Rule |
|---|---|
| `DefaultMonthlyTokenQuota` | A non-negative whole number that is exactly one of the configured tier caps (`Gateway:Tiers`; `GET /quota/tiers` lists them). Same D-013 rule as every other quota write path — the gateway enforces a tier, not a number. Unlimited is not expressible fork-wide: it is set per user or per group |
| `ResetDayOfMonth` | A whole number from 1 to 28 (28 is the last day every month has) |
| `EntraGroupSyncEnabled` | `true` or `false` |
| `ApimResourceId`, `FoundryResourceId` | An ARM resource id (`/subscriptions/{id}/resourceGroups/{group}/providers/{namespace}/{type}/{name}`), or empty. Shape only — whether the resource exists is Azure's answer, reported as `503` by the endpoints that use it |
| Any other row a fork operator added | Free text, up to 4000 characters |

Three keys that earlier versions seeded — `ApimGatewayUrl`, `ApimProductId`, `EntraTenantId` — are
**retired** ([#164](https://github.com/kolatts/foundry-gate/issues/164),
[#123](https://github.com/kolatts/foundry-gate/issues/123)). Nothing read them, so they briefly
answered `409 read-only`; now the rows are gone entirely (the next `db seed-reference` deletes them),
`GET /config` does not list them, and `PUT` on one is the ordinary `404`. What replaced each is in the
[Configuration Reference](/foundry-gate/reference/configuration/#retired-keys).

Every accepted edit stamps `updatedByUserId` (the calling admin) and `updatedDate`, and writes one
`config.updated` audit row with `{ key, before, after }` — in the same transaction as the change, so
a value can never move without a trail. The calling admin must already have a FoundryGate user row
(`GET /users/me` provisions one) or the write is `403`; reading `/config` needs no such row.

**A deploy never reverts an edit.** `SystemConfiguration`'s value, timestamp and editor columns are
`[DoNotUpdate]`, so the reference-data seeder that runs after every database deploy only *inserts*
keys that are missing.

### `GET /dashboard`

Returns `{ totalUserCount, activeUserCount, unlimitedUserCount, pendingQuotaIncreaseRequestCount,
totalTokensUsedThisPeriod, topConsumers, hardStoppedUserCount, overBudgetUserCount }` for the
current UTC calendar month.

- `unlimitedUserCount` counts **active** unlimited users — a deactivated account consumes nothing.
- `totalTokensUsedThisPeriod` sums every allocation in the period, deactivated users included: the
  tokens they spent before offboarding are still tokens this month spent.
- `topConsumers` is the ten busiest **active** users this period, each with `userId`, `userUnique`,
  `displayName`, `tokensUsed`, `allocatedTokens` and `percentUsed` — `null` for an unlimited user,
  `100` for a zero quota with any usage.
- `hardStoppedUserCount` counts **active** users whose current-period allocation carries
  `isHardStopped`. That flag is set by the deprovision pipeline and cleared by re-activation and by
  the monthly reset; quota exhaustion never sets it. An active user carrying it is an
  inconsistency — the allocation says "stopped" while the account says "live" — and is normally
  zero.
- `overBudgetUserCount` counts **active** users whose finite budget reconciled usage has reached or
  passed (`tokensUsed >= allocatedTokens`): the "who has run out" figure. The gateway is already
  refusing them with its own `403`. Unlimited allocations are never counted.

Every usage figure here is a reconciliation number from the Log Analytics sync, refreshed on that
job's cadence — not a live view of gateway enforcement.

The summary is computed once and served to every admin for **30 seconds**, keyed by billing period
(so the first read after a month boundary can never show last month's figures). The admin page
refreshes itself every 60 s; pass `?fresh=true` to recompute immediately after a change.
