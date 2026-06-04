# Developer UI — /me dashboard and quota increase request form

> GitHub: #16  
> Milestone: v0.4 — Frontend  
> Labels: epic, frontend

## Overview
This epic builds the two pages developers interact with daily: the `/me` dashboard showing their quota state and APIM key, and the `/me/request` form for submitting a quota increase request. The dashboard surfaces exactly what a developer needs to answer "how much quota do I have left and how do I get more?", keeping the UI focused and low-noise. Both pages consume the typed API client from epic #15 and produce immediate feedback via the shared toast service.

## Approach

### Build /me page: quota gauge, APIM key display/rotate, and request history (#49)
Create `Pages/Me/Index.razor` (route `/me`, requires `Developer` role). On `OnInitializedAsync`, call `GET /users/me` and `GET /keys` to load the user's current quota allocation and active API key. Render a circular or bar gauge showing `TokensUsed / TokenLimit` with colour coding (green/amber/red thresholds). Display the APIM key masked (last 4 chars visible) with a "Rotate key" button that calls `POST /keys/{id}/rotate` and reveals the new key value in a dismissible alert (never stored in component state beyond the single reveal). Show a table of the user's previous quota increase requests with status badges. Use `FoundryGateApiClient` for all calls and surface API errors via `ToastService`.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Me/Index.razor`
- `src/FoundryGate.Web/Pages/Me/Index.razor.cs`
- `src/FoundryGate.Web/Components/QuotaGauge.razor`
- `src/FoundryGate.Web/Components/ApiKeyDisplay.razor`

### Build /me/request quota increase form with validation and submission feedback (#50)
Create `Pages/Me/Request.razor` (route `/me/request`, requires `Developer` role). The form has two fields: `RequestedTokenLimit` (numeric input, must be greater than current allocation, validated client-side) and `Justification` (textarea, 500-char max with a live counter). On submit, call `POST /requests` and handle the response: on success, show a success toast and navigate back to `/me`; on `400` (invalid amount), display inline field errors; on `409` (pending request already exists), show a warning toast explaining they must wait for the current request to be decided. Use `EditForm` with `DataAnnotationsValidator` for client-side validation backed by the shared request DTO from `FoundryGate.Domain`.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Me/Request.razor`
- `src/FoundryGate.Web/Pages/Me/Request.razor.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] Quota gauge renders correctly at 0%, 50%, and 95% usage
- [ ] Rotating a key shows the new key value once, then masks it on dismiss
- [ ] Request form blocks submission if `RequestedTokenLimit` is less than or equal to current limit
- [ ] Successful request submission navigates back to `/me` with a success toast
- [ ] Duplicate pending request attempt shows warning toast without navigating away
