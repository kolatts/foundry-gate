# Admin UI — users, groups, and quota requests

> GitHub: #17  
> Milestone: v0.4 — Frontend  
> Labels: epic, frontend

## Overview
This epic builds the core admin management pages: the user table and detail view, the group list and CRUD pages, and the quota increase request queue with inline approve/reject. All pages use MudBlazor — `MudDataGrid` for all tabular data (server-side paging and sorting), `MudTabs` for the user detail layout, `MudDrawer` (secondary) for the request detail panel, and `MudDialog` for confirmations. All pages require the `Admin` role and call admin-tier API endpoints.

## Approach

### Build admin user table (/users) and user detail page (/users/{id}) (#51)
Create `Pages/Admin/Users/Index.razor` (route `/users`). Use `MudDataGrid` with `ServerData` pointing to `GET /users?page=&pageSize=&search=` — wire the `MudDataGrid` search toolbar input with a 300 ms debounce. Each row shows display name, email, a `MudChip` status badge (`Active` / `Inactive`), a `MudProgressLinear` mini-gauge for token usage, and group count. Clicking a row navigates to the detail page.

Create `Pages/Admin/Users/Detail.razor` (route `/users/{id}`) using `MudTabs` with four tabs: **Overview** (user fields, activate/deactivate `MudSwitch` with `MudDialog` confirmation), **Quota** (current allocation, resolved-level `MudChip`, `MudNumericField` quota override, unlimited `MudSwitch`, manual reset button), **Groups** (membership `MudTable` with an "Add to group" `MudAutocomplete` and remove `MudIconButton` per row), **Keys** (key info, admin rotate and revoke buttons each behind `MudDialog` confirmation). Write all mutations via `FoundryGateApiClient` and surface results via `ISnackbar`.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Users/Index.razor`
- `src/FoundryGate.Web/Pages/Admin/Users/Index.razor.cs`
- `src/FoundryGate.Web/Pages/Admin/Users/Detail.razor`
- `src/FoundryGate.Web/Pages/Admin/Users/Detail.razor.cs`

### Build admin group pages (/groups, /groups/new, /groups/{id}) with Entra sync trigger (#52)
Create `Pages/Admin/Groups/Index.razor` (route `/groups`) with a `MudDataGrid` showing name, member count, a quota policy `MudChip`, and an Entra-linked `MudIcon` indicator. A `MudFab` (floating action button) in the bottom-right links to `/groups/new`. Create `Pages/Admin/Groups/Create.razor` with a `MudForm` for name, description, optional `EntraGroupId`, and an optional `MudNumericField` quota limit with an unlimited `MudSwitch`.

Create `Pages/Admin/Groups/Detail.razor` (route `/groups/{id}`): quota policy editor at the top, member `MudDataGrid` with an "Add member" `MudAutocomplete` (searches `GET /users`) and per-row remove button. If `EntraGroupId` is set, show a "Sync with Entra" `MudButton`; on click, call `POST /groups/{id}/sync-entra` and display the `{ added, removed }` result in a `MudDialog` with a simple diff list.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Groups/Index.razor`
- `src/FoundryGate.Web/Pages/Admin/Groups/Create.razor`
- `src/FoundryGate.Web/Pages/Admin/Groups/Detail.razor`
- `src/FoundryGate.Web/Pages/Admin/Groups/Detail.razor.cs`

### Build admin requests queue (/requests) with inline approve/reject panel (#53)
Create `Pages/Admin/Requests/Index.razor` (route `/requests`). Use a `MudDataGrid` with a `MudSelect` status filter (All / Pending / Approved / Rejected) wired to `GET /requests?status=`. Clicking a Pending row opens a secondary `MudDrawer` (Anchor.End, 480 px wide) showing the requester's name, current quota, requested quota, and justification `MudTextField` (read-only). The drawer footer has an Approve `MudButton` (Color.Success) and a Reject `MudButton` (Color.Error). Reject expands a `MudTextField` for review notes before confirming. After approve or reject, close the drawer and refresh the grid row status optimistically. Non-pending rows open the same drawer in read-only mode showing the reviewer name, timestamp, and notes.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Requests/Index.razor`
- `src/FoundryGate.Web/Pages/Admin/Requests/Index.razor.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] User table search and server-side pagination work correctly
- [ ] Status toggle requires MudDialog confirmation before calling the API
- [ ] Group Entra sync shows added/removed member diff in a dialog
- [ ] Approving a request from the drawer updates the row status without a full grid reload
- [ ] Non-admin navigating to `/users` is redirected to the AccessDenied page
