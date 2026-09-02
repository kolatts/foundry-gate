# GitHub Actions CI/CD — overview

> GitHub: #14  
> Milestone: v0.3 — Infrastructure  
> Labels: epic, infra

## Overview
All CI/CD is defined in `.github/workflows/`. Infra and code pipelines are strictly separated. Every deploy to a live environment requires a GitHub Environment gate. Destruction pipelines are `workflow_dispatch`-only with typed confirmation. See **plan #22** for the complete pipeline reference including all job steps, secrets, variables, and environment configuration.

**Pipeline inventory:**

| File | Trigger | Purpose |
|---|---|---|
| `infra-deploy.yml` | PR `infra/**`, `workflow_dispatch` | Bicep what-if on PRs (read-only `dev-plan`); dispatch redeploys one environment |
| `infra-destroy.yml` | `workflow_dispatch` only | Tear down an entire environment's resource group |
| `api-deploy.yml` | PR `src/FoundryGate.Api/Dockerfile`, `workflow_dispatch` | Image build + `/health` smoke on PRs; dispatch redeploys the API |
| `functions-deploy.yml` | `workflow_dispatch` | Build → publish → Functions deploy |
| `ui-deploy.yml` | `workflow_dispatch` | Build → publish → Static Web Apps |
| `docs-deploy.yml` | push/PR `docs-site/**` | Astro build → GitHub Pages |
| `deploy-all.yml` | **push `main`** (anything deployable), `workflow_dispatch` | Full-stack deploy in dependency order — the only workflow that deploys on merge |

## Approach

### Configure GitHub Environments with protection rules (#68)
Create four environments in repo Settings → Environments: `dev` (no protection), `production` (required reviewer), `dev-destroy` (required reviewer + 5 min wait timer), `prod-destroy` (2+ required reviewers + 30 min wait timer). Set environment-scoped variables (`RESOURCE_GROUP`, `CONTAINER_APP_NAME`, etc.) and OIDC-specific variables (`AZURE_CLIENT_ID`, `AZURE_SUBSCRIPTION_ID`) per environment so workflow files have no hardcoded values.

Files expected to be created or modified:
- GitHub repo Settings → Environments (configured in UI, not a file)
- `docs/fork-guide.md` — environment setup instructions

### Write infra-deploy.yml: what-if on PRs, deploy dev then prod (#69)
Three jobs: `validate` (bicep build + lint), `what-if` (posts diff as PR comment; always runs), `deploy-dev` (environment: dev; runs on push to main), `deploy-prod` (environment: production; runs on `workflow_dispatch` with env=prod; requires successful dev deploy). See plan #22 for full job definitions.

Files expected to be created or modified:
- `.github/workflows/infra-deploy.yml`

### Write infra-destroy.yml: typed confirmation guard + environment approval (#70)
`workflow_dispatch` only. Inputs: `environment` (dev|prod) and `confirmation` (must equal `DESTROY-{environment}`). Jobs: `validate-confirmation` (fails fast if mismatch), `list-resources` (prints what will be deleted), then `destroy-dev` or `destroy-prod` behind the appropriate destroy environment gate. See plan #22 for full job definitions.

Files expected to be created or modified:
- `.github/workflows/infra-destroy.yml`

### Write api-deploy.yml, functions-deploy.yml, ui-deploy.yml, docs-deploy.yml (#71 #72 #73 #74)
Four separate files, each with a `build-test` job (always runs, including on PRs) and a `deploy` job (main/dispatch only, gated by GitHub Environment). PRs get build validation + SWA preview URLs where applicable. See plan #22 for per-pipeline details.

Files expected to be created or modified:
- `.github/workflows/api-deploy.yml`
- `.github/workflows/functions-deploy.yml`
- `.github/workflows/ui-deploy.yml`
- `.github/workflows/docs-deploy.yml`
- `src/FoundryGate.Api/Dockerfile`

### Write deploy-all.yml: full-stack deploy via workflow_call (#75)
Manual `workflow_dispatch` with environment input. Calls each deployment workflow in dependency order (infra first, then api/functions/ui in parallel, docs independently). Used to rebuild a fresh environment after a destroy. See plan #22.

Files expected to be created or modified:
- `.github/workflows/deploy-all.yml`

## Verification
- [x] Workflow files for every row of the inventory exist on `main` (#68–#75; `docs.yml` renamed to `docs-deploy.yml`) — see plan #22 for the per-item checklist and what still needs a live run (#105)
- [x] Exactly one workflow deploys on a merge to `main` (`deploy-all.yml`); the single-component wrappers are `workflow_dispatch` + PR-track only, so nothing is silently cancelled out of the shared `deploy-{env}` concurrency group
