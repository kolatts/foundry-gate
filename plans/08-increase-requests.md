# Quota increase requests — submit, list, approve, and reject

> GitHub: #8  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic delivers the workflow that allows developers to request a quota increase and admins to approve or reject those requests. It is a lightweight state-machine: a request starts as `Pending`, moves to `Approved` or `Rejected`, and approval triggers a re-resolution of the user's quota allocation so the new limit takes effect immediately. This keeps the approval loop tight and avoids a separate cron job to apply approved increases.

## Approach

### Implement quota increase request submission and listing (developer + admin) (#34)
Add a `QuotaRequestsController` with `POST /requests` (developer submits a request with `RequestedTokenLimit` and `Justification`; validates the requested amount is greater than current allocation; creates a `QuotaIncreaseRequest` with `Status = Pending` and writes an audit log entry). Add `GET /requests` for developers (returns their own requests, paginated, with status filter) and a separate admin variant `GET /admin/requests` that returns all pending and historical requests across all users. Use `IQueryable` projections to `QuotaIncreaseRequestResponse` to avoid over-fetching entity data.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/QuotaRequestsController.cs`
- `src/FoundryGate.Api/Services/IQuotaRequestService.cs`
- `src/FoundryGate.Api/Services/QuotaRequestService.cs`

### Implement admin approve and reject endpoints with quota re-resolution (#35)
Add `POST /admin/requests/{id}/approve` and `POST /admin/requests/{id}/reject` (both admin-only). Approval sets `Status = Approved`, writes `ReviewedByUserId` and `ReviewedAt`, creates or updates a user-level `QuotaPolicy` override to the approved amount, and calls `IQuotaResolutionService.ResolveAsync` to immediately update the `QuotaAllocation` for the current period. Rejection sets `Status = Rejected` and optionally stores a `RejectionReason` from the request body. Both actions write an audit log entry. Approving or rejecting an already-decided request returns `409 Conflict`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/QuotaRequestsController.cs`
- `src/FoundryGate.Api/Services/QuotaRequestService.cs`

## Direction update (implemented in the #34/#35 PR)

The shape above is superseded in four places by what CONVENTIONS.md and the quota wave (#32/#33,
plans/07) had already landed by the time this epic was implemented:

- **One controller, not two.** `Controllers/RequestsController` serves the whole surface at
  `/api/v1/requests` (the spec §4.4 path), with admin-only actions declared per action, instead of a
  separate `/admin/requests` tree — the same shape `QuotaController` uses for `allocations` vs
  `allocations/me`. The service is `Services/Requests/IQuotaRequestService` (per-area folder + one
  `AddRequestsServices()` line), not `Services/IQuotaRequestService`.
- **A request asks for a tier, not a number (D-013).** "Validates the requested amount is greater than
  current allocation" is necessary but not sufficient, and "current allocation" is the wrong source:
  `RequestedQuota` must be `null` (unlimited) or exactly a configured tier cap
  (`GatewayTierMapper.EnsureValidQuota`), and must be a genuine increase over what the five-level chain
  resolves to *live* — the `QuotaAllocation` row only reflects the last resolution, so a group change
  makes it stale. Both rules are re-checked at approval (a stale request must never *lower* a budget an
  admin or group has since raised — `409`), and the live read is the new side-effect-free
  `IQuotaResolutionService.PreviewAsync`, so a refused submission creates no allocation row and makes no
  gateway call. One pending request per user per period, else `409`; the review transition claims the row
  with a conditional update so two reviewers cannot both decide it.
- **No `QuotaPolicy` override on approval.** That entity does not exist; approval writes
  `User.IsUnlimited`/`User.MonthlyTokenQuota` (levels 1–2 of the five-level chain) and then calls
  `IQuotaResolutionService.ResolveAsync` for the current period, which upserts the allocation and moves
  the APIM subscription to the new tier product via `IGatewayTierSync`.
- **Review actions are `POST`, not `PUT`** — non-idempotent state transitions whose body is not the
  resource, matching `POST /users/{id}/activate`, `POST /keys/{userId}/rotate`, `POST /quota/reset`.

Also added beyond the original scope: `POST /requests/for/{userId}` (admin-on-behalf, which the #34
issue body calls for but the plan omitted) and `IQuotaRequestService.CancelPendingForUserAsync`, the
save-free, audit-free hook the deprovisioning path (#65/#66) uses to close a departing developer's
open requests inside its own unit of work.

## Verification
- [x] `dotnet build FoundryGate.sln -c Release` passes with zero warnings; `dotnet format --verify-no-changes` clean
- [x] Superseded: "Submitting a request with a lower limit than current allocation returns `400`" — a quota is a tier (D-013), so submission refuses *both* a non-tier value and a tier that is not an increase (including "already unlimited") — `QuotaRequestServiceTests.SubmitAsync_rejects_a_value_that_is_not_a_configured_tier_cap`, `..._rejects_a_request_for_the_tier_the_caller_is_already_on`, `..._rejects_a_smaller_tier_as_not_an_increase_naming_the_current_budget`, `..._rejects_a_caller_who_is_already_unlimited`, `RequestsEndpointTests.Submit_with_a_value_that_is_not_a_tier_returns_400_listing_the_tiers`
- [x] Approving a request immediately updates the user's quota *and* the current-period `QuotaAllocation` (and moves the gateway tier) — `QuotaRequestServiceTests.ApproveAsync_applies_the_tier_re_resolves_the_period_moves_the_gateway_and_audits_before_and_after`, `..._for_an_unlimited_request_sets_the_flag_and_clears_the_number`, `RequestsEndpointTests.Approve_applies_the_tier_to_the_user_re_resolves_the_period_and_audits_before_and_after`
- [x] Approving or rejecting an already-decided request returns `409` — `QuotaRequestServiceTests.ApproveAsync_on_an_already_decided_request_is_409_and_changes_nothing`, `RejectAsync_on_an_already_decided_request_is_409`, `RequestsEndpointTests.Approving_an_already_decided_request_returns_409`
- [x] Developer `GET /requests` only returns that developer's own requests; `?userId=` naming another user is `403`, and `GET /requests/{id}` for someone else's request is `404` (never an enumeration oracle) — `QuotaRequestServiceTests.ListAsync_*`, `GetAsync_for_someone_elses_request_is_404_not_403`, `RequestsEndpointTests.List_*`, `Get_someone_elses_request_returns_404_for_a_non_admin_and_200_for_an_admin`
- [x] A second pending request in the same period is `409`; a decided request frees the slot — `QuotaRequestServiceTests.SubmitAsync_rejects_a_second_pending_request_in_the_same_period`, `..._allows_a_new_request_once_the_previous_one_was_decided`
- [x] Audit log captures submit, approve (with before/after quota and the resolved tier) and reject, each attributed to the acting caller and committed with the mutation — assertions in every writer test above
- [x] Endpoint auth contract (401 anonymous / 403 non-admin on admin routes / 403 unprovisioned or deactivated submitter), request-body validation 400s, and `201` + lowercase `Location` that resolves — `RequestsEndpointTests`
- [x] Demo data depicts a request the API would actually accept (Standard-tier developer asking for Power, matching the landing page's "Asked for more" row) — `TestDataSeederTests.SeedAsync_creates_developers_with_varied_quota_tiers`
- [x] Approval can never lower a budget: a request the subject has outgrown (an admin made them unlimited, or a group raised them to the requested tier) is `409` and changes nothing — `QuotaRequestServiceTests.ApproveAsync_refuses_with_409_when_the_subject_is_now_unlimited_so_approval_would_downgrade_them`, `..._when_a_group_has_already_raised_the_subject_to_the_requested_tier`, `..._still_succeeds_when_the_subject_moved_up_but_the_request_asks_for_more_again`, `RequestsEndpointTests.Approving_a_request_the_user_has_already_outgrown_returns_409_and_does_not_downgrade_them`
- [x] Submission and approval measure against live resolution, not the stored allocation row — `QuotaRequestServiceTests.SubmitAsync_measures_against_live_resolution_not_the_stale_allocation_row`, `..._sees_a_group_quota_the_allocation_row_has_not_caught_up_with`, `QuotaResolutionServiceTests.PreviewAsync_*`
- [x] A refused submission touches neither the gateway nor the database (CONVENTIONS.md: every refusal before the external call) — `QuotaRequestServiceTests.SubmitAsync_that_is_refused_touches_neither_the_gateway_nor_the_database`, `RequestsEndpointTests.Submit_measures_against_live_resolution_and_creates_no_allocation`
- [x] Two reviewers deciding at once: one wins, the other is `409`, and nothing partial is left behind — `QuotaRequestServiceTests.ApproveAsync_loses_the_row_claim_to_a_concurrent_reviewer_and_is_409`, `RejectAsync_loses_the_row_claim_to_a_concurrent_reviewer_and_is_409`
- [x] Every writer composes inside an orchestrator's transaction instead of opening a second one — `QuotaRequestServiceTests.SubmitAsync_joins_a_transaction_the_caller_already_owns_instead_of_opening_its_own`, `ApproveAsync_joins_a_transaction_the_caller_already_owns`
- [x] `GET /requests`' ordering and filter columns are indexed and mirrored into `QuotaIncreaseRequests.sql` — `SchemaParityTests`
- [ ] Deferred (#147): the one-pending-request-per-period rule is a read-then-write check with no database constraint behind it, so two simultaneous submissions can both land
- [ ] Deferred (#159): approval applies to the current period regardless of the request's own, and nothing expires stale pending requests
- [ ] Deferred (#158): the repo-wide sweep for audit + save on `CancellationToken.None` past an external commit point (this wave's approval path is already done)
- [ ] Deferred (#160): the deprovisioning path adopts `CancelPendingForUserAsync` instead of cancelling pending requests inline
