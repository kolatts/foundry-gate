# GitHub Actions CI/CD — API, UI, Functions, docs, and infra workflows

> GitHub: #14  
> Milestone: v0.3 — Infrastructure  
> Labels: epic, infra

## Overview
Five GitHub Actions workflows automate the full build, test, and deploy lifecycle using OIDC federation — no long-lived credentials stored in GitHub. `api.yml` builds/tests/deploys the API Container App. `functions.yml` builds and deploys the Azure Functions app. `ui.yml` publishes and deploys the Blazor WASM frontend. `docs.yml` builds the Astro docs site and deploys to GitHub Pages. `infra.yml` runs Bicep what-if on PRs and full deploy on merge to main.

## Approach

### Write api.yml: dotnet build/test, Docker push to ACR, and Container App deploy (#45)
Create `.github/workflows/api.yml` triggered on `push` to `main` (paths: `src/FoundryGate.Api/**`, `src/FoundryGate.Data/**`, `src/FoundryGate.Domain/**`) and on `pull_request`. Two jobs: `build-test` runs `dotnet restore`, `dotnet build -c Release`, `dotnet test --no-build`. The `deploy` job (main only) uses OIDC `azure/login`, runs `docker build` and `docker push` to ACR, then `az containerapp update --image` to roll out the new tag. Store ACR name, Container App name, and resource group as GitHub Actions variables (not secrets).

Files expected to be created or modified:
- `.github/workflows/api.yml`
- `src/FoundryGate.Api/Dockerfile`

### Write functions.yml: dotnet publish and Azure Functions deploy (#59)
Create `.github/workflows/functions.yml` triggered on `push` to `main` (paths: `src/FoundryGate.Functions/**`, `src/FoundryGate.Data/**`) and on `pull_request` (build only). The `build-test` job runs `dotnet build -c Release` on the Functions project. The `deploy` job (main only) runs `dotnet publish -c Release -o publish/functions`, then uses `Azure/functions-action@v1` with OIDC to deploy the publish output to the Function App. The Function App name is stored as a GitHub Actions variable.

Files expected to be created or modified:
- `.github/workflows/functions.yml`

### Write ui.yml: Blazor WASM publish and Static Web Apps deploy (#46)
Create `.github/workflows/ui.yml` triggered on `push` to `main` (paths: `src/FoundryGate.Web/**`, `src/FoundryGate.Domain/**`) and on `pull_request`. Use `actions/setup-dotnet@v4` pinned to .NET 10, run `dotnet publish -c Release -o publish/web`, then `Azure/static-web-apps-deploy@v1` with the deployment token to deploy `publish/web/wwwroot`. On pull requests, Static Web Apps automatically creates a preview environment and posts the URL as a PR comment.

Files expected to be created or modified:
- `.github/workflows/ui.yml`

### Write infra.yml: Bicep what-if on PRs and deploy on merge to main (#47)
Create `.github/workflows/infra.yml` triggered on `push` to `main` (paths: `infra/**`) and `pull_request`. On PRs: OIDC login → `az deployment sub what-if` → post output as PR comment via `actions/github-script`. On merge: `az deployment sub create` with `prod.bicepparam`, gated by a GitHub Environment (`production`) requiring manual approval. No long-lived credentials — OIDC only.

Files expected to be created or modified:
- `.github/workflows/infra.yml`

## Verification
- [ ] `api.yml` green on a clean branch; Docker image pushed and Container App updated
- [ ] `functions.yml` deploys Functions app and both timer functions appear in Azure portal
- [ ] `ui.yml` deploys Blazor WASM; Static Web Apps preview URL posted on PRs
- [ ] `infra.yml` posts what-if diff as a PR comment
- [ ] No long-lived Azure credentials stored anywhere in GitHub secrets
