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

## Verification
- [ ] `dotnet build` passes for both API and Web projects
- [ ] `GET /foundry/deployments` returns real deployments from the configured Foundry resource
- [ ] Creating a deployment via the UI provisions it in Azure (verify in portal)
- [ ] Deleting a deployment via the UI removes it from Azure
- [ ] Provisioning state chip updates on grid refresh
- [ ] Audit log records `foundry.deployment.created` and `foundry.deployment.deleted` entries
- [ ] Non-admin users cannot access `/foundry` (redirected to AccessDenied)
