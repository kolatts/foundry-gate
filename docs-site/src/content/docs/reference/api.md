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

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/foundry/models` | Any | Developer view: distinct deployment names with model, version, format and provisioning state. A model deployed in several regions is listed once — `Succeeded` if any region serves it. |
| `GET` | `/foundry/deployments` | Admin | Every deployment in every configured account (account, name, model format/name/version, SKU, capacity in thousands of TPM, provisioning state, created/modified). Primary account first, then by name. |
| `GET` | `/foundry/deployments/{accountName}/{deploymentName}` | Admin | One deployment — poll this after a create until `provisioningState` is `Succeeded`. |
| `POST` | `/foundry/deployments` | Admin | Create one deployment in one account. Body: `accountName`, `deploymentName`, `modelFormat` (`OpenAI`; default), `modelName`, `modelVersion`, `skuName`, `capacity` (thousands of TPM). `201` + `Location`; the body reflects ARM's initial state (usually `Creating`). |
| `DELETE` | `/foundry/deployments/{accountName}/{deploymentName}` | Admin | Delete one deployment (`204`). Never recreates. In-flight requests pinned to the name get the backend's 404 once ARM finishes. |

Rules the create path enforces (CLAUDE.md "Anthropic deployments are create-once"; decision log E-006/E-007):

- **An existing name is `409 Conflict`** — the API checks first and never re-PUTs an existing deployment (a re-PUT drives an Anthropic deployment to `Failed`). Replace a deployment by deleting it and creating it again, as two explicit, audited actions.
- **`modelFormat: Anthropic` is `400 Bad Request`** for now: creating a Claude deployment needs the Marketplace `modelProviderData` attestation, which the current Azure SDK cannot send and the API's identity is not permitted to make (issue #107). Existing Claude deployments — created by `infra/main.bicep` on the first run — list and delete normally.
- An `accountName` that is not one of the gateway's configured accounts is `400` on create and `404` on read/delete.
- Every create and delete writes an audit entry (`foundry.deployment.created` / `foundry.deployment.deleted`, target type `FoundryDeployment`, target id `{accountName}/{deploymentName}`). The admin must have loaded the app once (`GET /users/me`) so the entry has an actor — otherwise the mutation is refused (`403`) before Azure is touched.

## Admin

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/config` | Admin | All SystemConfiguration keys |
| `PUT` | `/config/{key}` | Admin | Update a configuration value |
| `GET` | `/audit` | Admin | Audit log, paged. Filter: `?actor=&action=&from=&to=` |
| `GET` | `/dashboard` | Admin | Summary stats for the dashboard |
