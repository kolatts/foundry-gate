# Admin UI — users, groups, and quota requests

> GitHub: #17  
> Milestone: v0.4 — Frontend  
> Labels: epic, frontend

## Overview
This epic builds the core admin management pages: the user table and detail view, the group list and CRUD pages, and the quota increase request queue with inline approve/reject. These pages are the primary operational interface for FoundryGate admins — they need to be fast, scannable, and make common actions (approve a request, add a user to a group) reachable in as few clicks as possible. All pages require the `Admin` role and call admin-tier API endpoints.

## Approach

### Build admin user table (/users) and user detail page (/users/{id}) (#51)
Create `Pages/Admin/Users/Index.razor` (route `/users`) with a searchable, sortable, paginated table of users. Each row shows display name, email, status badge, current token usage bar, and group count. Wire the search input to `GET /users?search=...` with debouncing. Create `Pages/Admin/Users/Detail.razor` (route `/users/{id}`) with a tabbed layout: Overview (user info, status toggle with confirmation modal), Quota (current allocation with resolved level badge and a manual reset button), Groups (membership list with add/remove controls), and Keys (active API key with admin-rotate and admin-revoke buttons). All mutating actions show a confirmation modal before calling the API.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Users/Index.razor`
- `src/FoundryGate.Web/Pages/Admin/Users/Index.razor.cs`
- `src/FoundryGate.Web/Pages/Admin/Users/Detail.razor`
- `src/FoundryGate.Web/Pages/Admin/Users/Detail.razor.cs`
- `src/FoundryGate.Web/Components/ConfirmModal.razor`
- `src/FoundryGate.Web/Components/StatusBadge.razor`

### Build admin group pages (/groups, /groups/new, /groups/{id}) with Entra sync trigger (#52)
Create `Pages/Admin/Groups/Index.razor` (route `/groups`) with a group list table (name, member count, has quota policy badge, Entra-linked indicator). Create `Pages/Admin/Groups/Create.razor` (route `/groups/new`) with a form for name, description, optional `EntraGroupId`, and optional quota limit. Create `Pages/Admin/Groups/Detail.razor` (route `/groups/{id}`) showing group metadata, member table with add/remove, quota policy editor, and a "Sync with Entra" button that calls `POST /groups/{id}/sync-entra` and shows a diff summary of members added/removed in a slide-over panel.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Groups/Index.razor`
- `src/FoundryGate.Web/Pages/Admin/Groups/Create.razor`
- `src/FoundryGate.Web/Pages/Admin/Groups/Detail.razor`
- `src/FoundryGate.Web/Pages/Admin/Groups/Detail.razor.cs`
- `src/FoundryGate.Web/Components/SlideOverPanel.razor`

### Build admin requests queue (/requests) with inline approve/reject panel (#53)
Create `Pages/Admin/Requests/Index.razor` (route `/requests`) showing all quota increase requests in a filterable table (filter by status: Pending / Approved / Rejected). Clicking a Pending row opens an inline detail panel (not a separate page) showing user info, current quota, requested quota, and justification text. The panel has Approve and Reject buttons; Reject shows a reason textarea before confirming. After approve or reject the row status updates optimistically and the panel closes. Use `GET /admin/requests?status=Pending` for the default view.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Requests/Index.razor`
- `src/FoundryGate.Web/Pages/Admin/Requests/Index.razor.cs`
- `src/FoundryGate.Web/Components/RequestDetailPanel.razor`

## Verification
- [ ] `dotnet build` passes
- [ ] User table search and pagination work correctly
- [ ] Status toggle requires confirmation before calling the API
- [ ] Group Entra sync shows added/removed member diff
- [ ] Approving a request from the queue updates the row status without a full page reload
- [ ] Non-admin navigating to `/users` is redirected to the access denied page
