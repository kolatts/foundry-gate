# Groups endpoints and membership management

> GitHub: #6  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic delivers the group management API, which lets admins organise users into named groups that can carry their own quota policies. Groups are the primary mechanism for giving a team a shared or elevated quota without touching individual user records. The epic covers full group CRUD, membership management (add/remove individual users), and a sync endpoint that pulls members from a corresponding Entra ID group via Microsoft Graph, keeping Foundry Gate's groups aligned with the organisation's directory automatically.

## Approach

### Implement group CRUD: create, list, get, update, and delete (#30)
Add a `GroupsController` with admin-only endpoints: `POST /groups` (create with name, description, optional `EntraGroupId` for sync, and optional `QuotaPolicy`), `GET /groups` (paginated list), `GET /groups/{id}` (detail including member count and active quota policy), `PUT /groups/{id}` (update name, description, quota policy), and `DELETE /groups/{id}` (soft-delete if no active members, or hard-delete with cascade depending on configuration). Use a `IGroupService` to encapsulate business rules, such as validating that a group name is unique and preventing deletion of groups with active members unless `force=true` is passed.

**As landed (#30):** `Services/Groups/IGroupService` + `GroupService` (scoped, sharing the request's
`AppDbContext` with quota resolution and the audit writer) behind an admin-only `GroupsController`.
Deletion is a **hard** delete of the group and its `GroupMember` rows — there is no soft-delete flag on
`Group` and no configuration switch; `?force=true` is the guard, and a populated group without it is a
`409`. Users and their individual overrides are never touched. Name uniqueness is enforced both in the
service (case-insensitively, so SQL Server and the SQLite test harness agree) and by a new unique index
`IX_Groups_Name`, so two concurrent creates cannot both win. Quota values go through
`GatewayTierMapper.EnsureValidQuota` (D-013), and a quota change re-resolves every **active** member in
the same unit of work — which is what moves their APIM tier product.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/GroupsController.cs`
- `src/FoundryGate.Api/Services/Groups/IGroupService.cs`
- `src/FoundryGate.Api/Services/Groups/GroupService.cs`

### Implement group membership management and Entra group sync (#31)
Add `POST /groups/{id}/members` (add a user by userId), `DELETE /groups/{id}/members/{userId}` (remove a user), and `GET /groups/{id}/members` (paginated member list with quota allocation summary). For Entra sync, add `POST /groups/{id}/sync-entra` which calls Microsoft Graph `GET /groups/{entraGroupId}/members` using the `GraphServiceClient` with `client_credentials` flow, then reconciles the result against the Foundry Gate `GroupMembership` table (add new members, remove departed members). Write an audit log entry per member added or removed. Wrap Graph calls in a retry policy using `Polly` to handle transient Graph API errors.

**As landed (#31, #41):** Graph is reached only through `Services/Entra/IEntraDirectoryClient`
(`ListGroupMemberIdsAsync`, added with #40), so no Polly is needed here — the Graph SDK's own retry
handler covers transient failures and the reconciliation is tested against an in-memory fake. Sync is
**per group**, at `POST /groups/{id}/sync-entra`, with `POST /groups/sync-entra` looping every linked
group; each group is one unit of work with **one** `group.entra-synced` audit row carrying the counts,
rather than one row per membership (a 500-member group would otherwise bury the audit viewer).
Directory-added memberships carry `AddedByUserId = null` — the system actor. Entra members with no
FoundryGate `User` row are skipped and counted in `GroupSyncResult.SkippedUnknownUserCount`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/GroupsController.cs`
- `src/FoundryGate.Api/Services/Groups/IEntraGroupSyncService.cs`
- `src/FoundryGate.Api/Services/Groups/EntraGroupSyncService.cs`

## Verification
- [x] `dotnet build` passes
- [x] Creating a group and listing it returns the correct record
- [x] Adding and removing members is reflected in `GET /groups/{id}/members`
- [x] Sync endpoint reconciles members against a mock Graph response (`FakeEntraDirectoryClient`, service and endpoint level)
- [x] Audit log contains entries for all membership changes
- [x] Deleting a group with active members without `force=true` returns `409`
