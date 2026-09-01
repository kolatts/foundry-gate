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
| `GET` | `/keys/me` | Any | Own key info (masked, last 4 visible; `isProvisioned: false` when none). Served from a stored hint — no decryption |
| `POST` | `/keys/me/reveal` | Any | Decrypt and return own full key once. Audited (`key.revealed`), never cached. `404` when no key |
| `POST` | `/keys/me/rotate` | Any | Rotate own key — regenerates **both** APIM keys (primary and never-issued secondary), returns the new primary once. `404` no key; `409` if the APIM subscription vanished |
| `POST` | `/keys/{userId}/rotate` | Admin | Rotate any user's key (same semantics) |
| `POST` | `/keys/{userId}/provision` | Admin | Provision a key for a user with none, under `?tier=standard\|power\|unlimited` (default `standard`). Returns plaintext once. `409` key exists or user deactivated; `400` unknown tier; reuses an orphaned APIM subscription with fresh keys |
| `DELETE` | `/keys/{userId}` | Admin | Revoke key only: APIM subscription deleted, stored key cleared, `key.revoked` audited. **User stays active** and can be re-provisioned; `204` even when no key existed. Deactivation is `POST /users/{id}/deactivate` |

Callers of every `/keys/me` route must already have a FoundryGate user row (`GET /users/me` provisions one) — otherwise `403`. The plaintext key is stored encrypted (Key Vault RSA key wrapping; see [Configuration](/reference/configuration/)) and appears in exactly one response per mint or reveal.

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
