# Admin dashboard, configuration editor, and audit log viewer

> GitHub: #18  
> Milestone: v0.4 — Frontend  
> Labels: epic, frontend

## Overview
This epic delivers the three remaining admin pages: a summary dashboard that gives a bird's-eye view of system health and activity, a system configuration editor for the seven `SystemConfiguration` key-value pairs, and an audit log viewer with filters. The dashboard is the landing page for admins after login, so it needs to surface the most actionable information (pending requests count, top consumers, quota utilisation) at a glance without requiring navigation to other pages.

## Approach

### Build /dashboard with summary stat cards, top consumers table, and pending badge (#54)
Create `Pages/Admin/Dashboard.razor` (route `/dashboard`, default admin landing page). Call `GET /admin/dashboard-summary` (a new lightweight endpoint that returns aggregate counts: total users, total groups, pending requests, system-wide token usage this month as a percentage of total allocated quota). Render four stat cards at the top of the page. Below, render a "Top consumers this month" table showing the 10 users with the highest `TokensUsed` for the current period. Add a red badge next to the "Requests" nav item showing the count of pending requests; update it on every dashboard load. Use `IntersectionObserver` (via JS interop) or a simple polling interval to keep the pending count fresh while the admin is on the dashboard.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Dashboard.razor`
- `src/FoundryGate.Web/Pages/Admin/Dashboard.razor.cs`
- `src/FoundryGate.Web/Components/StatCard.razor`
- `src/FoundryGate.Web/Shared/NavMenu.razor` (pending badge)
- `src/FoundryGate.Api/Controllers/AdminController.cs` (dashboard-summary endpoint)

### Build /config key-value editor and /audit log viewer with filters (#55)
Create `Pages/Admin/Config.razor` (route `/config`) that loads all `SystemConfiguration` rows from `GET /admin/config` and renders them as an editable key-value table. Each row has the key name (read-only), a description tooltip, and an editable value input. A single "Save changes" button at the bottom calls `PUT /admin/config` with the full updated set. Show a diff preview (original vs. new values) in a confirmation modal before saving. Create `Pages/Admin/AuditLog.razor` (route `/audit`) with a filterable, paginated table of audit log entries. Filters: actor (user search), action (enum dropdown), target entity type (dropdown), and date range picker. Each row shows timestamp, actor, action badge, target entity, and a details popover.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Config.razor`
- `src/FoundryGate.Web/Pages/Admin/Config.razor.cs`
- `src/FoundryGate.Web/Pages/Admin/AuditLog.razor`
- `src/FoundryGate.Web/Pages/Admin/AuditLog.razor.cs`
- `src/FoundryGate.Web/Components/DiffPreviewModal.razor`
- `src/FoundryGate.Web/Components/DateRangePicker.razor`

## Verification
- [ ] `dotnet build` passes
- [ ] Dashboard stat cards show correct counts from the API
- [ ] Pending request badge on the nav item shows the correct count
- [ ] Config editor shows a diff preview before saving and reflects saved values after
- [ ] Audit log filters correctly narrow the result set
- [ ] Audit log date range filter works across a month boundary
