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
| `GET` | `/quota/allocations` | Admin | All current-period allocations, paged |
| `GET` | `/quota/allocations/me` | Any | Own current allocation |
| `GET` | `/quota/allocations/{userId}` | Admin | Specific user's current allocation |
| `POST` | `/quota/reset` | Admin | Manually trigger monthly reset (idempotent) |

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
