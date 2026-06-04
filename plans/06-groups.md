# Groups endpoints and membership management

> GitHub: #6  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic delivers the group management API, which lets admins organise users into named groups that can carry their own quota policies. Groups are the primary mechanism for giving a team a shared or elevated quota without touching individual user records. The epic covers full group CRUD, membership management (add/remove individual users), and a sync endpoint that pulls members from a corresponding Entra ID group via Microsoft Graph, keeping FoundryGate's groups aligned with the organisation's directory automatically.

## Approach

### Implement group CRUD: create, list, get, update, and delete (#30)
Add a `GroupsController` with admin-only endpoints: `POST /groups` (create with name, description, optional `EntraGroupId` for sync, and optional `QuotaPolicy`), `GET /groups` (paginated list), `GET /groups/{id}` (detail including member count and active quota policy), `PUT /groups/{id}` (update name, description, quota policy), and `DELETE /groups/{id}` (soft-delete if no active members, or hard-delete with cascade depending on configuration). Use a `IGroupService` to encapsulate business rules, such as validating that a group name is unique and preventing deletion of groups with active members unless `force=true` is passed.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/GroupsController.cs`
- `src/FoundryGate.Api/Services/IGroupService.cs`
- `src/FoundryGate.Api/Services/GroupService.cs`

### Implement group membership management and Entra group sync (#31)
Add `POST /groups/{id}/members` (add a user by userId), `DELETE /groups/{id}/members/{userId}` (remove a user), and `GET /groups/{id}/members` (paginated member list with quota allocation summary). For Entra sync, add `POST /groups/{id}/sync-entra` which calls Microsoft Graph `GET /groups/{entraGroupId}/members` using the `GraphServiceClient` with `client_credentials` flow, then reconciles the result against the FoundryGate `GroupMembership` table (add new members, remove departed members). Write an audit log entry per member added or removed. Wrap Graph calls in a retry policy using `Polly` to handle transient Graph API errors.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/GroupsController.cs`
- `src/FoundryGate.Api/Services/IEntraGroupSyncService.cs`
- `src/FoundryGate.Api/Services/EntraGroupSyncService.cs`
- `src/FoundryGate.Api/FoundryGate.Api.csproj` (add Graph SDK and Polly packages)

## Verification
- [ ] `dotnet build` passes
- [ ] Creating a group and listing it returns the correct record
- [ ] Adding and removing members is reflected in `GET /groups/{id}/members`
- [ ] Sync endpoint reconciles members against a mock Graph response
- [ ] Audit log contains entries for all membership changes
- [ ] Deleting a group with active members without `force=true` returns `409`
