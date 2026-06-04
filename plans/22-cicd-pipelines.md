# CI/CD pipelines — infrastructure, code deployment, and destruction

> GitHub: #67  
> Milestone: v0.3 — Infrastructure  
> Labels: epic, infra

## Overview
All pipelines use OIDC federated credentials — no long-lived Azure secrets in GitHub. Infra and code pipelines are strictly separated: infrastructure changes never trigger code deploys and vice versa. Every environment-mutating pipeline is gated by a GitHub Environment with required reviewers. Destruction pipelines are `workflow_dispatch`-only with a typed confirmation guard and a separate high-protection GitHub Environment.

---

## GitHub Environments (#68)

Configure four GitHub Environments in the repo settings before any workflows run:

| Environment | Protection | Used by |
|---|---|---|
| `dev` | None — auto-deploys | infra-deploy, all code deploys (dev) |
| `production` | Required reviewers (at least 1) | infra-deploy, all code deploys (prod) |
| `dev-destroy` | Required reviewers + 5 min timer | infra-destroy (dev) |
| `prod-destroy` | Required reviewers (2+) + 30 min timer | infra-destroy (prod) |

Each environment also carries environment-scoped variables: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `RESOURCE_GROUP`, `LOCATION` — so the same workflow file targets the right subscription without any branching logic.

---

## Infrastructure pipelines

### infra-deploy.yml (#69)

```
Triggers:
  push to main       → paths: infra/**
  pull_request       → paths: infra/**
  workflow_dispatch  → input: environment (dev | prod)
```

**Jobs:**

```
validate
  - az bicep build --file infra/main.bicep   # lint + compile check
  - az bicep lint --file infra/main.bicep

what-if   (always runs; on PRs, posts output as PR comment)
  - az deployment sub what-if \
      --template-file infra/main.bicep \
      --parameters infra/parameters/${{ env }}.bicepparam \
      --parameters sqlAdminPassword=${{ secrets.SQL_ADMIN_PASSWORD }}

deploy-dev   (push to main or workflow_dispatch env=dev; uses 'dev' environment)
  needs: [validate, what-if]
  environment: dev
  - az deployment sub create ...

deploy-prod   (workflow_dispatch env=prod only; uses 'production' environment)
  needs: [deploy-dev]   # prod always follows a successful dev deploy
  environment: production
  - az deployment sub create ...
```

Files:
- `.github/workflows/infra-deploy.yml`

### infra-destroy.yml (#70)

```
Trigger:
  workflow_dispatch only
    inputs:
      environment:   choice  [dev, prod]   required
      confirmation:  string  "Type DESTROY-{environment} to confirm"   required
```

**Jobs:**

```
validate-confirmation
  - if: inputs.confirmation != format('DESTROY-{0}', inputs.environment)
    run: echo "Confirmation mismatch" && exit 1

list-resources   (always runs before any deletion)
  - az resource list --resource-group $RESOURCE_GROUP --output table
  - echo "The above resources will be permanently deleted."

destroy-dev   (environment=dev; uses 'dev-destroy' environment with reviewer gate)
  needs: [validate-confirmation, list-resources]
  if: inputs.environment == 'dev'
  environment: dev-destroy
  steps:
    - az group delete --name $RESOURCE_GROUP --yes
    - echo "::notice::Resource group $RESOURCE_GROUP deleted."

destroy-prod   (environment=prod; uses 'prod-destroy' environment with reviewer gate + 30 min timer)
  needs: [validate-confirmation, list-resources]
  if: inputs.environment == 'prod'
  environment: prod-destroy
  steps:
    - az group delete --name $RESOURCE_GROUP --yes
    - echo "::notice::Resource group $RESOURCE_GROUP deleted."
```

Files:
- `.github/workflows/infra-destroy.yml`

---

## Code deployment pipelines

All code pipelines share the same two-job structure: `build-test` always runs; `deploy` only runs on push to `main` (or `workflow_dispatch`) and requires a GitHub Environment gate.

### api-deploy.yml (#71)

```
Triggers:
  push to main  → paths: src/FoundryGate.Api/**, src/FoundryGate.Data/**, src/FoundryGate.Domain/**
  pull_request  → same paths (build-test only)
  workflow_dispatch → input: environment (dev | prod)
```

```
build-test
  - dotnet restore
  - dotnet build -c Release --no-restore
  - dotnet test --no-build -c Release

deploy   (environment: dev or production)
  - azure/login (OIDC)
  - docker build -t $ACR_LOGIN_SERVER/foundrygate-api:${{ github.sha }} src/FoundryGate.Api
  - docker push $ACR_LOGIN_SERVER/foundrygate-api:${{ github.sha }}
  - az containerapp update \
      --name $CONTAINER_APP_NAME \
      --resource-group $RESOURCE_GROUP \
      --image $ACR_LOGIN_SERVER/foundrygate-api:${{ github.sha }}
  - az containerapp revision list ... (verify new revision is active)
```

Files:
- `.github/workflows/api-deploy.yml`
- `src/FoundryGate.Api/Dockerfile`

### functions-deploy.yml (#72)

```
Triggers:
  push to main  → paths: src/FoundryGate.Functions/**, src/FoundryGate.Data/**, src/FoundryGate.Domain/**
  pull_request  → same paths (build-test only)
  workflow_dispatch → input: environment
```

```
build-test
  - dotnet build -c Release
  - dotnet test --no-build -c Release

deploy   (environment: dev or production)
  - dotnet publish src/FoundryGate.Functions -c Release -o publish/functions
  - azure/login (OIDC)
  - Azure/functions-action@v1
      app-name: $FUNCTION_APP_NAME
      package: publish/functions
```

Files:
- `.github/workflows/functions-deploy.yml`

### ui-deploy.yml (#73)

```
Triggers:
  push to main  → paths: src/FoundryGate.Web/**, src/FoundryGate.Domain/**
  pull_request  → same paths (preview environment auto-created by SWA action)
  workflow_dispatch → input: environment
```

```
build-test
  - dotnet build -c Release
  - dotnet publish src/FoundryGate.Web -c Release -o publish/web

deploy   (environment: dev or production)
  - Azure/static-web-apps-deploy@v1
      azure_static_web_apps_api_token: ${{ secrets.SWA_DEPLOY_TOKEN }}
      app_location: publish/web/wwwroot
      # PR creates preview URL and posts comment automatically
```

Files:
- `.github/workflows/ui-deploy.yml`

### docs-deploy.yml (#74)

```
Triggers:
  push to main  → paths: docs-site/**
  pull_request  → same paths (build only, no deploy)
  workflow_dispatch
```

```
build
  - cd docs-site && npm ci && npm run build

deploy   (push to main only; uses GitHub Pages — no Azure environment needed)
  permissions:
    pages: write
    id-token: write
  environment:
    name: github-pages
    url: ${{ steps.deployment.outputs.page_url }}
  - actions/configure-pages
  - actions/upload-pages-artifact  source: docs-site/dist
  - actions/deploy-pages@v4
```

Files:
- `.github/workflows/docs-deploy.yml`

---

## Full stack deploy (manual) (#75)

A convenience `workflow_dispatch`-only workflow that deploys everything in dependency order when you need a clean environment rebuilt from scratch (e.g. after a destroy).

```
Triggers:
  workflow_dispatch → input: environment (dev | prod)

Jobs (sequential):
  1. infra     → calls infra-deploy.yml via workflow_call
  2. api       → calls api-deploy.yml via workflow_call   needs: [infra]
  3. functions → calls functions-deploy.yml               needs: [infra]
  4. ui        → calls ui-deploy.yml                      needs: [infra]
  5. docs      → calls docs-deploy.yml                    needs: [] (no Azure dependency)
```

Files:
- `.github/workflows/deploy-all.yml`

---

## Required GitHub Actions secrets and variables

**Secrets** (set per environment or at repo level):

| Name | Scope | Purpose |
|---|---|---|
| `SQL_ADMIN_PASSWORD` | repo | Passed as secure Bicep param |
| `GRAPH_CLIENT_SECRET` | repo | Passed as secure Bicep param |
| `SWA_DEPLOY_TOKEN` | per environment | Static Web Apps deploy token |

**Variables** (set per environment):

| Name | Example (dev) | Example (prod) |
|---|---|---|
| `AZURE_CLIENT_ID` | (OIDC federated app) | (different registration) |
| `AZURE_TENANT_ID` | same | same |
| `AZURE_SUBSCRIPTION_ID` | dev subscription | prod subscription |
| `RESOURCE_GROUP` | `rg-foundrygate-dev` | `rg-foundrygate-prod` |
| `LOCATION` | `eastus2` | `eastus2` |
| `ACR_LOGIN_SERVER` | `foundrygate.azurecr.io` | same |
| `CONTAINER_APP_NAME` | `foundrygate-api-dev` | `foundrygate-api-prod` |
| `FUNCTION_APP_NAME` | `foundrygate-fn-dev` | `foundrygate-fn-prod` |

---

## Verification
- [ ] `infra-deploy.yml` what-if posts a PR comment with the diff
- [ ] `infra-deploy.yml` deploy-prod requires manual reviewer approval in GitHub UI
- [ ] `infra-destroy.yml` rejects mismatched confirmation string (no resources deleted)
- [ ] `infra-destroy.yml` destroy-prod has both required reviewers AND a 30-minute wait timer
- [ ] Each code pipeline builds and tests on PRs without deploying
- [ ] `deploy-all.yml` successfully rebuilds a fresh dev environment from zero
- [ ] No long-lived Azure credentials anywhere in GitHub secrets — OIDC only
