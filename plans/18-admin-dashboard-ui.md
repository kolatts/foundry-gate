# Admin dashboard, configuration editor, and audit log viewer

> GitHub: #18  
> Milestone: v0.4 — Frontend  
> Labels: epic, frontend

## Overview
This epic delivers the three remaining admin pages: a summary dashboard that gives a bird's-eye view of system health and activity, a system configuration editor for the `SystemConfiguration` key-value pairs, and an audit log viewer with filters. The dashboard is the admin landing page, so it uses `MudCard` stat cards for at-a-glance numbers and `MudDataGrid` for the top consumers table. The config editor uses an inline-edit `MudTable` pattern. The audit log viewer uses `MudDataGrid` with `MudSelect` and `MudDateRangePicker` filters — no custom date picker component needed.

## Approach

### Build /dashboard with summary stat cards, top consumers table, and pending badge (#54)
Create `Pages/Admin/Dashboard.razor` (route `/dashboard`, default admin landing). Call `GET /dashboard` to fetch aggregate counts. Render four `MudCard` stat cards in a `MudGrid` (2-column on mobile, 4-column on desktop): Total Users, Active Users, Pending Requests (with `MudBadge` colour warning if > 0), and System Token Usage % this month. Below, render a "Top consumers this month" `MudDataGrid` (non-interactive, no paging — top 10 only) with a `MudProgressLinear` usage bar per row.

Wire the pending request count to the `MudBadge` on the "Requests" `MudNavLink` in `NavMenu.razor` — pass it as a cascading value or via a lightweight `DashboardStateService` singleton. Refresh the dashboard data every 60 s using `PeriodicTimer` in `OnAfterRenderAsync`.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Dashboard.razor`
- `src/FoundryGate.Web/Pages/Admin/Dashboard.razor.cs`
- `src/FoundryGate.Web/Shared/NavMenu.razor` (pending badge)
- `src/FoundryGate.Web/Services/DashboardStateService.cs`

### Build /config key-value editor and /audit log viewer with filters (#55)
Create `Pages/Admin/Config.razor` (route `/config`). Load all `SystemConfiguration` rows from `GET /config` into a `MudTable` with `Hover=true`. Each row has the key name (read-only `MudText`), a `MudTooltip` description, and a `MudTextField` for the value. A "Save all changes" `MudButton` at the bottom calls `PUT /config/{key}` for each dirty row in sequence and shows a `MudDialog` diff preview (original vs new values) before proceeding.

Create `Pages/Admin/AuditLog.razor` (route `/audit`) with a `MudDataGrid` (`ServerData`, paged). Filter bar above the grid: `MudAutocomplete` for actor (searches users), `MudSelect<string>` for action (populated from known audit action constants in `FoundryGate.Domain`), `MudSelect<string>` for target type, and `MudDateRangePicker` for date range. Apply filters on change with 300 ms debounce. Each grid row has a `MudChip` for the action and a `MudIconButton` that expands a `MudPopover` showing the raw `Details` JSON blob formatted with `MudHighlighter` or a `<pre>` block.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Config.razor`
- `src/FoundryGate.Web/Pages/Admin/Config.razor.cs`
- `src/FoundryGate.Web/Pages/Admin/AuditLog.razor`
- `src/FoundryGate.Web/Pages/Admin/AuditLog.razor.cs`
- `src/FoundryGate.Web/Services/DashboardStateService.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] Dashboard stat cards show correct counts from the API
- [ ] Pending request MudBadge on the nav item reflects the live count
- [ ] Config editor shows only dirty rows in the diff preview dialog
- [ ] Saving config calls PUT for each changed key and shows a success snackbar
- [ ] Audit log MudDateRangePicker filter works across a month boundary
- [ ] Audit log details popover renders JSON without crashing on malformed input
