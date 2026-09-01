# Entra ID sync — bulk users and group members

> GitHub: #11  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic adds two admin-triggered sync endpoints that reconcile Foundry Gate's local user and group-membership tables against Azure Entra ID (Microsoft Graph). Bulk user sync ensures every Entra user who has been assigned to the Foundry Gate application has a corresponding `User` row, while group member sync keeps `GroupMembership` rows aligned with Entra group membership. Both operations are designed to be idempotent and safe to call repeatedly, making them suitable for periodic automated invocation as well as one-off admin actions.

## Approach

### Implement bulk Entra user sync via Microsoft Graph SDK (POST /users/sync) (#40)
Add `POST /users/sync` (admin-only). Call Graph `GET /applications/{appId}/appRoleAssignedTo` to fetch all assigned Entra users. For each:
- **Present in both** → upsert display fields, update `LastSyncedAt`
- **New in Entra, not in DB** → INSERT User with default quota (`IsActive = true`); do NOT provision an APIM key (key only provisioned on first actual login or explicit admin action)
- **In DB but absent from Entra** → call `IUserLifecycleService.DeprovisionAsync(trigger: EntraDeparture, userId)`: **deletes** APIM subscription (not just suspends), sets `IsActive = false`, hard-stops current allocation, cancels Pending requests (see **plan #21**). Do NOT delete the User row — preserve audit history.

Use `ExecutePageIteratorAsync` for Graph paging. Return `{ added, updated, deactivated }`. Write a `sync.bulk-users` audit log entry.

**As landed (#40, with #110):** Graph goes behind `Services/Entra/IEntraDirectoryClient` (`GetUserAsync`, `ListAssignedUsersAsync`, `ListGroupMemberIdsAsync`) with a Graph implementation authenticated by the app's registered `TokenCredential` (managed identity / Azure CLI — no client secret) and a `DisabledEntraDirectoryClient` when `Entra:Enabled` is false (→ 400). The user population is `servicePrincipals/{id}/appRoleAssignedTo` (user principals only; group-principal assignments are reported as `SkippedGroupAssignmentCount` and suspend departure detection for the run until #121 expands them), hydrated via `GET /users?$filter=id in (...)` in chunks of 15. Departed users are **only** flagged `IsActive = false` in this wave — the full deprovision (APIM subscription deletion, hard stop, request cancellation) is #65's `IUserLifecycleService`, which replaces that branch. Audit action is the Domain constant `users.synced`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/UsersController.cs`
- `src/FoundryGate.Api/Services/IEntraUserSyncService.cs`
- `src/FoundryGate.Api/Services/EntraUserSyncService.cs`

### Implement Entra group member sync (POST /groups/sync-entra) (#41)
Add `POST /groups/sync-entra` (admin-only, or per-group via `POST /groups/{id}/sync-entra` as defined in epic #6). For each `Group` that has a non-null `EntraGroupId`, call `GET /groups/{entraGroupId}/members` via Graph and reconcile against `GroupMembership` rows: insert missing memberships and remove memberships for users no longer in the Entra group. Only process users who already exist in the Foundry Gate `Users` table (orphan Entra members are skipped with a warning, not errored). After reconciling memberships, trigger quota re-resolution for any user whose group membership changed, since their effective quota level may have shifted. Return a per-group summary.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/GroupsController.cs`
- `src/FoundryGate.Api/Services/EntraGroupSyncService.cs` (extends epic #6 service)

## Verification
- [x] `dotnet build` passes
- [x] Bulk user sync is idempotent when called twice with no changes between runs
- [x] New Entra users appear as `User` rows after sync
- [x] Bulk user sync handles more than one Graph page (250-user fake directory at the service level; stubbed `@odata.nextLink` paging and 15-id `in` chunks at the Graph client level)
- [ ] Removed Entra group members have their `GroupMembership` row deleted after group sync
- [ ] Quota re-resolution fires for users whose group membership changed
- [ ] Graph paging is handled correctly (more than 100 users in a group)
