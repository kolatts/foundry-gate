# Foundry model deployment provisioning

> GitHub: #60  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
Foundry Gate admins can list, create, and delete Azure AI Foundry model deployments directly from the UI without touching the Azure portal. This uses `Azure.ResourceManager.CognitiveServices` with the Container App's Managed Identity (which holds `Cognitive Services Contributor` on the Foundry resource). Because APIM already covers all Foundry routes under the `Foundry Gate` product, a newly provisioned deployment is immediately accessible via existing user subscription keys — no APIM changes required. This keeps the entire LLM access lifecycle (models, keys, quotas) managed in one place.

## Approach

### Implement /foundry/deployments API endpoints using Azure SDK (#61)
Create `FoundryDeploymentsController` with three admin-only endpoints and one any-authenticated-user endpoint:

- `GET /foundry/models` — **any authenticated user** — returns the same deployment list with only the fields a developer needs to configure their CLI: deployment name, model name, model version, and provisioning state. This is what the `/me` page uses to populate the CLI setup section.
- `GET /foundry/deployments` — **admin only** — full deployment list including SKU name, capacity units, and provisioning state.
- `POST /foundry/deployments` — accepts a `CreateFoundryDeploymentRequest` (deployment name, model name, model version, SKU, capacity) and calls `CreateOrUpdateAsync` on the deployment collection. Returns the created deployment. Validates that the deployment name is unique before calling the SDK to give a clean error rather than a raw Azure SDK exception.
- `DELETE /foundry/deployments/{name}` — calls `DeleteAsync` on the named deployment resource. Returns `204 No Content`. Write an audit log entry for create and delete actions.

Create `IFoundryDeploymentService` backed by `ArmClient` (using `DefaultAzureCredential` — the Managed Identity in prod, local `AzureCLICredential` in dev). The Foundry resource ID comes from `SystemConfiguration["FoundryResourceId"]`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/FoundryDeploymentsController.cs`
- `src/FoundryGate.Api/Services/IFoundryDeploymentService.cs`
- `src/FoundryGate.Api/Services/FoundryDeploymentService.cs`
- `src/FoundryGate.Domain/Foundry/FoundryDeploymentDto.cs`
- `src/FoundryGate.Domain/Foundry/CreateFoundryDeploymentRequest.cs`
- `src/FoundryGate.Api/FoundryGate.Api.csproj` (Azure.ResourceManager.CognitiveServices package)

### Build admin /foundry page in Blazor UI (#62)
Create `Pages/Admin/Foundry/Index.razor` (route `/foundry`, requires Admin role). On load, call `GET /foundry/deployments` and render a `MudDataGrid` with columns: deployment name, model name, model version, SKU, capacity, and provisioning state `MudChip` (colour-coded: green = Succeeded, amber = Creating, red = Failed). A "New deployment" `MudFab` opens a `MudDialog` form with `MudSelect` for model name (populated from a hardcoded list of known Azure AI Foundry model IDs, or fetched from a `GET /foundry/available-models` endpoint if we add it later), `MudTextField` for deployment name, `MudSelect` for SKU, and `MudNumericField<int>` for capacity. On create, POST to the API and refresh the grid. A delete `MudIconButton` per row opens a `MudDialog` confirmation before calling `DELETE /foundry/deployments/{name}`. Add a `/foundry` `MudNavLink` to the Admin section of `NavMenu.razor`.

Files expected to be created or modified:
- `src/FoundryGate.Web/Pages/Admin/Foundry/Index.razor`
- `src/FoundryGate.Web/Pages/Admin/Foundry/Index.razor.cs`
- `src/FoundryGate.Web/Shared/NavMenu.razor`

### Implementation notes (#61, as built)

- Controller is `FoundryController` (route `api/v1/foundry/…` derives from the class name via `ApiControllerBase`), with an extra `GET /foundry/deployments/{accountName}/{deploymentName}` so `POST` has a `Location` to point at and the UI has something to poll.
- Multi-account: the gateway runs one Foundry account per region, so deployments are addressed `{accountName}/{deploymentName}` and the create body names its account. Accounts come from `Gateway__SubscriptionId` / `Gateway__ResourceGroup` / `Gateway__FoundryAccountNames__{i}` (#108) bound to `GatewayOptions`, not from `SystemConfiguration["FoundryResourceId"]`.
- ARM is behind `IFoundryManagementClient` (`ArmFoundryManagementClient` over `Azure.ResourceManager.CognitiveServices` 1.5.2); tests use an in-memory fake — no live Azure in tests.
- Safety rules (CLAUDE.md, E-006/E-007): create checks existence first → 409, never re-PUT; delete never recreates; **Anthropic-format create AND delete are refused with 400** — Claude deployments belong to infra end to end (the SDK, 1.5.2 and 1.6.0-beta.4, has no `modelProviderData` and its api-version predates the one that accepts it (E-005); the identity lacks Marketplace permissions (#107); and Bicep can only recreate all of an account's deployments, so an API delete of one Claude deployment has no safe recovery). Lifting the refusal is #126; capacity resize (PATCH, from the #60 direction update) is #130.
- Mutations resolve the caller's `User` row before touching ARM, so an unprovisioned admin gets 403 with nothing created; once ARM accepts, the audit row + save run on `CancellationToken.None` (a disconnecting client cannot produce an unaudited deployment); the residual save-after-accept orphan is logged at Error.
- Unconfigured `Gateway` section, or a configured account Azure does not have → `FeatureNotConfiguredException` → 503 on admin paths; `/foundry/models` skips a missing account with a Warning and is served from a 30 s `IMemoryCache` (invalidated on create/delete).
- `WaitUntil.Started` on create/delete: the response carries ARM's initial state (`Creating`); the UI polls the single-deployment GET.

## Verification
- [x] `dotnet build` passes for the API project (Web: #62)
- [x] Unit/integration coverage (no live Azure): multi-account aggregation, developer-view de-duplication, 409 on an existing name with no ARM PUT, Anthropic → 400 before any ARM call, unconfigured account → 400/404, delete → 204/404 with no recreate, auth matrix 401/403/200/201/204, request validation, options fail-fast (`FoundryDeploymentServiceTests`, `FoundryEndpointTests`, `RequestDtoValidationTests`, `AppSettingsValidationTests`)
- [x] Audit log records `foundry.deployment.created` and `foundry.deployment.deleted` entries (target `{accountName}/{deploymentName}`; asserted at service and endpoint level)
- [ ] `GET /foundry/deployments` returns real deployments from the configured Foundry accounts — live validation, #125
- [ ] Creating an OpenAI deployment via the API provisions it in Azure (verify in portal) — live validation, #125
- [ ] Deleting a deployment via the API removes it from Azure — live validation, #125
- [x] `/foundry` grids `GET /foundry/deployments` with account, name, model, version, SKU, capacity as thousands of TPM, and a colour-coded state chip (`FoundryPageTests`)
- [x] Provisioning state chip updates on grid refresh — the create path refreshes and says the deployment is provisioning; there is an explicit refresh button, since ARM finishes asynchronously (`FoundryPageTests`)
- [x] Delete is behind a confirmation and is disabled outright on Anthropic-format rows — the API refuses those, so the button is never offered and then refused (`FoundryPageTests`)
- [x] The create dialog is OpenAI-format only and says why Claude deployments aren't created here (`FoundryPageTests`)
- [x] Non-admin users cannot access `/foundry` — `[Authorize(Roles = RoleNames.Admin)]` renders `AccessDenied` via `App.razor` (`AdminPageAuthorizationTests`); a 403 from the API does the same
- [ ] Creating a deployment via the UI provisions it in Azure (verify in portal) — live validation, #125
- [ ] Deleting a deployment via the UI removes it from Azure — live validation, #125

### Implementation notes (#62, as built)

- Model and SKU suggestions are a hardcoded shortcut list from the #60 direction update, in
  `CoerceValue` autocompletes so anything typed is still accepted. A hardcoded catalogue goes
  stale; the real fix is an endpoint listing what an account can serve — **#173**.
- Account names are offered from the accounts that already have deployments, and a lone account
  is pre-selected, so the common case is a pick rather than a guess.
- Capacity resize (PATCH, from the #60 direction update) is still #130 — the API has no such
  route, so the page has no resize action.
