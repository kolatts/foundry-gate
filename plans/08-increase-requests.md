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

## Verification
- [ ] `dotnet build` passes
- [ ] Submitting a request with a lower limit than current allocation returns `400`
- [ ] Approving a request immediately updates `QuotaAllocation.TokenLimit`
- [ ] Approving an already-approved request returns `409`
- [ ] Developer `GET /requests` only returns that developer's own requests
- [ ] Audit log captures submit, approve, and reject events
