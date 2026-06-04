# Entra ID sync — bulk users and group members

> GitHub: #11  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic adds two admin-triggered sync endpoints that reconcile FoundryGate's local user and group-membership tables against Azure Entra ID (Microsoft Graph). Bulk user sync ensures every Entra user who has been assigned to the FoundryGate application has a corresponding `User` row, while group member sync keeps `GroupMembership` rows aligned with Entra group membership. Both operations are designed to be idempotent and safe to call repeatedly, making them suitable for periodic automated invocation as well as one-off admin actions.

## Approach

### Implement bulk Entra user sync via Microsoft Graph SDK (POST /users/sync) (#40)
Add `POST /users/sync` (admin-only) to `UsersController`. Use `GraphServiceClient` with `client_credentials` to call `GET /applications/{appId}/appRoleAssignedTo` (or the group-assignment equivalent) to fetch all Entra users assigned to the FoundryGate service principal. For each returned user, upsert a `User` row (insert if `EntraObjectId` is new, update display name and email if changed). Use `ExecutePageIteratorAsync` to handle Graph paging transparently. Return a summary response with `Created`, `Updated`, and `Skipped` counts. Write a bulk audit log entry for the sync run.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/UsersController.cs`
- `src/FoundryGate.Api/Services/IEntraUserSyncService.cs`
- `src/FoundryGate.Api/Services/EntraUserSyncService.cs`

### Implement Entra group member sync (POST /groups/sync-entra) (#41)
Add `POST /groups/sync-entra` (admin-only, or per-group via `POST /groups/{id}/sync-entra` as defined in epic #6). For each `Group` that has a non-null `EntraGroupId`, call `GET /groups/{entraGroupId}/members` via Graph and reconcile against `GroupMembership` rows: insert missing memberships and remove memberships for users no longer in the Entra group. Only process users who already exist in the FoundryGate `Users` table (orphan Entra members are skipped with a warning, not errored). After reconciling memberships, trigger quota re-resolution for any user whose group membership changed, since their effective quota level may have shifted. Return a per-group summary.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/GroupsController.cs`
- `src/FoundryGate.Api/Services/EntraGroupSyncService.cs` (extends epic #6 service)

## Verification
- [ ] `dotnet build` passes
- [ ] Bulk user sync is idempotent when called twice with no changes between runs
- [ ] New Entra users appear as `User` rows after sync
- [ ] Removed Entra group members have their `GroupMembership` row deleted after group sync
- [ ] Quota re-resolution fires for users whose group membership changed
- [ ] Graph paging is handled correctly (more than 100 users in a group)
