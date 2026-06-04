# Developer UI — /me dashboard and quota increase request form

> GitHub: #16  
> Milestone: v0.4 — Frontend  
> Labels: epic, frontend

## Overview
This epic builds the two pages developers interact with daily: the `/me` dashboard showing their quota state and APIM key, and the `/me/request` form for submitting a quota increase request. The dashboard surfaces exactly what a developer needs to answer "how much quota do I have left and how do I get more?", keeping the UI focused and low-noise. Both pages consume the typed API client from epic #15 and use MudBlazor components throughout — `MudProgressLinear` for the gauge, `MudTable` for history, `MudTextField`/`MudTextField` for form fields, and `MudDialog` for confirmations.

## Approach

### Build /me page: quota gauge, APIM key display/rotate, and request history (#49)
Create `Pages/Me/Index.razor` (route `/me`, requires authenticated user). On `OnInitializedAsync`, call `GET /quota/allocations/me` and `GET /keys/me` in parallel. Render a `MudProgressLinear` showing `TokensUsed / AllocatedTokens` with colour coding: green below 80%, `MudBlazor.Color.Warning` amber at 80–95%, `MudBlazor.Color.Error` red above 95%. Show an "Unlimited" `MudChip` when `AllocatedTokens` is null.

For the key panel: show the masked key in a `MudTextField` (read-only, `InputType.Password`), a "Reveal" `MudIconButton` that calls a separate endpoint and temporarily switches `InputType` to `Text` in component state only — never stored beyond the current render cycle. A "Rotate Key" `MudButton` opens a `MudDialog` confirmation; on confirm, call `POST /keys/me/rotate` and display the new key revealed once in the same panel. Below, render request history in a `MudTable` with status `MudChip` colour-coded by `RequestStatus`. Surface all API errors via `ISnackbar`.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Me/Index.razor`
- `src/FoundryGate.Web/Pages/Me/Index.razor.cs`
- `src/FoundryGate.Web/Components/QuotaGauge.razor`
- `src/FoundryGate.Web/Components/ApiKeyDisplay.razor`

### Build /me/request quota increase form with validation and submission feedback (#50)
Create `Pages/Me/Request.razor` (route `/me/request`, requires authenticated user). Use a `MudForm` with `MudNumericField<long?>` for the requested token count (nullable — leaving it empty means requesting unlimited) and a `MudTextField` multiline for justification (500-char max with live `MudText` character counter). Validate with `DataAnnotationsValidator` backed by the shared request DTO from `FoundryGate.Domain`. On submit, call `POST /requests`: success → `ISnackbar` success message + `NavigationManager.NavigateTo("/me")`; `400` → surface inline field errors via `MudForm.Validate()`; `409` (pending request already exists) → `ISnackbar` warning. Disable the submit button while the API call is in-flight using a `_submitting` flag.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Me/Request.razor`
- `src/FoundryGate.Web/Pages/Me/Request.razor.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] Quota gauge renders at correct colour thresholds (0%, 80%, 95%, 100%)
- [ ] "Unlimited" chip shows when allocation has no token cap
- [ ] Rotating a key opens confirmation dialog, then shows the new key revealed once
- [ ] Request form disables submit while in-flight and re-enables on response
- [ ] Duplicate pending request attempt shows MudSnackbar warning without navigating away
- [ ] Successful submission navigates back to `/me`
