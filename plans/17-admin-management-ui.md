# Admin UI — users, groups, and quota requests

> GitHub: #17  
> Milestone: v0.4 — Frontend  
> Labels: epic, frontend

## Overview
This epic builds the core admin management pages: the user table and detail view, the group list and CRUD pages, and the quota increase request queue with inline approve/reject. All pages use MudBlazor — `MudDataGrid` for all tabular data (server-side paging and sorting), `MudTabs` for the user detail layout, `MudDrawer` (secondary) for the request detail panel, and `MudDialog` for confirmations. All pages require the `Admin` role and call admin-tier API endpoints.

## Approach

### Build admin user table (/users) and user detail page (/users/{id}) (#51)
Create `Pages/Admin/Users/Index.razor` (route `/users`). Use `MudDataGrid` with `ServerData` pointing to `GET /users?page=&pageSize=&search=` — wire the `MudDataGrid` search toolbar input with a 300 ms debounce. Each row shows display name, email, a `MudChip` status badge (`Active` / `Inactive`), a `MudProgressLinear` mini-gauge for token usage, and group count. Clicking a row navigates to the detail page.

Create `Pages/Admin/Users/Detail.razor` (route `/users/{id}`) using `MudTabs` with four tabs: **Overview** (user fields, activate/deactivate `MudSwitch` with `MudDialog` confirmation), **Quota** (current allocation, resolved-level `MudChip`, `MudNumericField` quota override, unlimited `MudSwitch`, manual reset button), **Groups** (membership `MudTable` with an "Add to group" `MudAutocomplete` and remove `MudIconButton` per row), **Keys** (key info, admin rotate and revoke buttons each behind `MudDialog` confirmation). Write all mutations via `Foundry GateApiClient` and surface results via `ISnackbar`.

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

### Build /users/sync admin page — trigger Entra sync and view results (#63)
Create `Pages/Admin/Users/Sync.razor` (route `/users/sync`, Admin role). The page has a single "Run Entra sync" `MudButton` that calls `POST /users/sync` and streams the result into a `MudAlert` showing `{ added, updated, deactivated }` counts. Below the button, show the timestamp and result of the last sync run (stored in `SystemConfiguration["LastUserSyncAt"]` and `"LastUserSyncResult"`). Add a `MudNavLink` to `/users/sync` in the admin nav under the Users group. This page covers the spec §8.1 route that was not tracked in any earlier plan.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Users/Sync.razor`
- `src/FoundryGate.Web/Shared/NavMenu.razor`

## Implementation notes (#51, #52, #53, #63, as built)

- Routes are the flat `Pages/*.razor` files the spec §8.1 skeleton already reserved
  (`Users.razor`, `UserDetail.razor`, …), not the `Pages/Admin/<Area>/Index.razor` tree this
  plan sketched — the stubs existed, and replacing them keeps one file per route.
- **No `MudNumericField` anywhere a quota is set.** A budget IS a gateway tier (D-013), so
  `Shared/QuotaTierPicker.razor` is a pick from `GET /quota/tiers` plus an unlimited switch, and
  `Shared/TierDisplay.cs` renders every stored quota as its tier's display name. A legacy value
  matching no tier is shown as itself rather than silently mapped.
- The user detail Quota tab has **no manual reset button** (this plan asked for one):
  `POST /quota/reset` resets the whole tenant for the period, so putting it on one person's page
  would misrepresent what it does. It belongs on an admin-wide surface.
- Group membership is edited **on the group, not on the user**: a roster is the thing an Entra
  sync owns, and the API refuses membership edits on an Entra-linked group (409). The user
  detail Groups tab lists memberships and links to each group.
- Approve/reject are `POST` (api.md), not the `PUT` the #48 shell client used; the same
  correction applies to user activate/deactivate.
- The review drawer's optimistic row update is a small session overlay keyed by request id —
  `MudDataGrid` owns the page `ServerData` handed it and won't accept a swapped row, and
  re-fetching would defeat the point.

## Verification
- [x] `dotnet build FoundryGate.sln -c Release` passes with zero warnings
- [x] User table search and server-side pagination work correctly — 300 ms debounce, filter changes reset to page 1 (`UsersPageTests`)
- [x] Status toggle requires MudDialog confirmation before calling the API; cancelling calls nothing (`UserDetailPageTests`)
- [x] Group Entra sync shows the added/removed/skipped-unknown counts in a dialog (`GroupDetailPageTests`)
- [x] Approving a request from the drawer updates the row status without a full grid reload (`RequestsPageTests`)
- [x] Non-admin navigating to `/users` is redirected to the AccessDenied page — every admin page carries `[Authorize(Roles = RoleNames.Admin)]`, which `App.razor` renders as `AccessDenied` (`AdminPageAuthorizationTests`); a 403 from the API does the same mid-page
- [x] Key provision/rotate reveal the plaintext once and only once; revoke leaves the account active (`UserDetailPageTests`)
- [x] An Entra-linked group hides the add/remove controls and explains why (`GroupDetailPageTests`)
- [x] `?status=Pending` — the dashboard badge's link — arrives as a filter (`RequestsPageTests`)
- [x] `/users/sync` explains `skippedGroupAssignmentCount` (#121) and `failedCount` (`UsersSyncPageTests`)
- [ ] Live: the pages against a real API + Entra tenant (manual, with #132's checklist)
- [ ] `/users/sync` shows the *previous* run's time and result — no durable last-sync metadata exists yet, #171
