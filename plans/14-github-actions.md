# GitHub Actions CI/CD — API, UI, and infra workflows

> GitHub: #14  
> Milestone: v0.3 — Infrastructure  
> Labels: epic, infra

## Overview
This epic delivers three GitHub Actions workflows that automate the full build, test, and deploy lifecycle using OIDC federation (no long-lived credentials stored in GitHub). `api.yml` builds and tests the .NET solution, pushes a Docker image to Azure Container Registry, and deploys to the Container App. `ui.yml` publishes the Blazor WASM project and deploys to Static Web Apps. `infra.yml` runs a Bicep what-if check on PRs and a full deploy on merge to main. Together these workflows mean every merge to main ships a complete, tested update to Azure with no manual steps.

## Approach

### Write api.yml: dotnet build/test, Docker push to ACR, and Container App deploy (#45)
Create `.github/workflows/api.yml` triggered on `push` to `main` (paths: `src/**`) and on `pull_request`. The workflow has two jobs: `build-test` (runs `dotnet restore`, `dotnet build --no-restore -c Release`, `dotnet test --no-build`) and `deploy` (depends on `build-test`, runs only on `push` to `main`). The deploy job uses OIDC with `azure/login` and the federated credential for the Container Registry's managed identity, runs `docker build` and `docker push` to ACR, then calls `az containerapp update` to deploy the new image tag. Store ACR name, Container App name, and resource group as GitHub Actions variables (not secrets) since they are not sensitive.

Files expected to be created or modified:
- `.github/workflows/api.yml`
- `src/FoundryGate.Api/Dockerfile`

### Write ui.yml: Blazor WASM publish and Static Web Apps deploy (#46)
Create `.github/workflows/ui.yml` triggered on `push` to `main` (paths: `src/FoundryGate.Web/**`) and on `pull_request`. Use `actions/checkout`, `actions/setup-dotnet@v4` pinned to .NET 10, run `dotnet publish -c Release -o publish/web`, then use the `Azure/static-web-apps-deploy@v1` action with the deployment token (stored as a GitHub secret) to deploy the `publish/web/wwwroot` output. On pull requests, the Static Web Apps action automatically creates a preview environment and posts the preview URL as a PR comment.

Files expected to be created or modified:
- `.github/workflows/ui.yml`

### Write infra.yml: Bicep what-if on PRs and deploy on merge to main (#47)
Create `.github/workflows/infra.yml` triggered on `push` to `main` (paths: `infra/**`) and on `pull_request` (paths: `infra/**`). Use OIDC login, then on pull requests run `az deployment sub what-if --template-file infra/main.bicep --parameters infra/parameters/dev.bicepparam` and post the what-if output as a PR comment using `actions/github-script`. On push to main, run `az deployment sub create` with the `prod.bicepparam` parameter file. Gate the deploy on a manual approval step using GitHub Environments (`production` environment with required reviewers configured).

Files expected to be created or modified:
- `.github/workflows/infra.yml`

## Verification
- [ ] `api.yml` passes on a clean branch with a green dotnet test run
- [ ] Docker image is pushed to ACR and Container App restarts with new image
- [ ] `ui.yml` deploys Blazor WASM artifacts and Static Web App shows updated build
- [ ] `infra.yml` posts what-if output as a PR comment
- [ ] No long-lived Azure credentials are stored in GitHub secrets (OIDC only)
