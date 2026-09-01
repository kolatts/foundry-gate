---
title: CI/CD Reference
description: Every GitHub Actions workflow in the repo — what triggers it, what it deploys, which GitHub Environment gates it, and which variables it reads.
---

FoundryGate deploys itself from GitHub Actions with **OIDC federated credentials only** — no
Azure credential JSON, no deploy tokens stored in GitHub (`CONVENTIONS.md` §CI/CD). Infra and
code pipelines are separate files that never trigger each other; every environment-mutating job
targets a **GitHub Environment**, which is both the approval gate and the scope of the variables
it reads. Status: the workflow files exist on `main` and lint clean; none has run against Azure
yet — the first live run is [#105](https://github.com/kolatts/foundry-gate/issues/105) and needs
the owner setup in [#109](https://github.com/kolatts/foundry-gate/issues/109).

## The shape

```text
pull request ──► ci.yml (build · Predeployment tests · format)   required check: build-test
             ├─► claude-review.yml
             ├─► infra/**            → infra-deploy.yml   : validate + what-if, posted as a PR comment
             ├─► src/FoundryGate.Web → ui-deploy.yml      : Static Web Apps preview environment
             ├─► Dockerfile          → api-deploy.yml     : image build + /health smoke test
             └─► docs-site/**        → docs-deploy.yml    : Astro build

merge to main ─► infra/**            → infra-deploy.yml   : deploy dev
              ├─► Api/Data/Domain/Database/Cli → api-deploy.yml : dacpac → Container App (dev)
              ├─► Functions/Data/Domain        → functions-deploy.yml (dev)
              ├─► Web/Domain                   → ui-deploy.yml (dev)
              └─► docs-site/**                 → docs-deploy.yml : GitHub Pages

manual ───────► deploy-all.yml   : infra → database → api ∥ functions ∥ ui → postdeployment tests → summary
              ├─► infra-deploy.yml (dev, or dev-then-production)
              ├─► api-/functions-/ui-deploy.yml (choose environment)
              └─► infra-destroy.yml : typed confirmation → listing → destroy gate → delete resource group
```

Every `*-deploy.yml` is a thin trigger wrapper; the work lives in reusable `_deploy-*.yml`
children (`workflow_call`) that take `environment:` and are composed by `deploy-all.yml`.

## Conventions every workflow follows

- **`environment` = the GitHub Environment name**: `dev` or `production`. The Bicep/FoundryGate
  name (`dev` | `prod` — `prod.bicepparam`, `rg-foundrygate-prod`) is derived from it.
- **Resource names come from the infra deployment outputs**, never from GitHub variables:
  every code deploy runs `az deployment sub show -n foundrygate-{dev|prod}` and reads the
  [outputs contract](/foundry-gate/reference/infrastructure/#outputs-contract)
  (`.github/scripts/infra/export-outputs.sh`). If the deployment does not exist the job fails
  with "run infra first".
- **OIDC identifiers are Environment variables** (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
  `AZURE_SUBSCRIPTION_ID`) — identifiers, not secrets. `.github/actions/azure-oidc-login`
  checks they are set (and that the job has `id-token: write`) and fails with a readable
  `::error::` pointing at #109, so a fork without setup never sees an AADSTS error. Jobs that
  must stay green on forks (PR what-if, UI previews) skip with a warning instead.
- **Permissions**: callers grant `id-token: write` + `contents: read` (+ `pull-requests: write`
  where a comment is posted); reusable workflows only narrow.
- **Concurrency**: one lock per environment shared by every deploy wrapper (`deploy-dev`,
  `deploy-production`), never cancelled mid-flight; PR jobs cancel-in-progress per PR. Each
  reusable child has its own group (`infra-{env}`, `api-{env}`, …) so nesting never deadlocks.
- **Version**: `.github/actions/version` runs GitVersion 6 (`GitVersion.yml`, trunk-based —
  every commit on `main` bumps patch; `+semver: minor|major` in a commit message overrides).
  Assemblies get `Version`/`AssemblyVersion`; the API image is tagged `{semVer}-{shortSha}` and
  `{semVer}`.

## Workflow inventory

| File | Triggers | Jobs | Gate | Reads |
|---|---|---|---|---|
| `ci.yml` | PR, merge queue | `build-test` (**required check**), `docs-build` | — | — |
| `infra-deploy.yml` | PR `infra/**` → what-if · push `main` `infra/**` → deploy dev · dispatch (`dev` / `dev-then-production`, `create-model-deployments`) | calls `_deploy-infra.yml` | `dev`, then `production` | — |
| `infra-destroy.yml` | dispatch only (`environment`, `confirmation`, `purge-soft-deleted`) | `validate-confirmation` → `list-resources` → `destroy` | `dev-destroy` / `prod-destroy` | `AZURE_*` on the destroy environment |
| `api-deploy.yml` | push `main` (Api/Data/Domain/Database/Cli) · PR touching the Dockerfile → build check · dispatch (`environment`, `run-seed-test`) | `image-build` (PR) · `_prepare-database` → `_deploy-database` → `_deploy-api` | `dev` / `production` | — |
| `functions-deploy.yml` | push `main` (Functions/Data/Domain) · dispatch | `_deploy-functions` | `dev` / `production` | — |
| `ui-deploy.yml` | push `main` (Web/Domain) · PR opened/sync → preview · PR closed → cleanup · dispatch | `_deploy-ui` (`upload` / `close`) | `dev` / `production` | — |
| `docs-deploy.yml` | PR/push `docs-site/**`, `content/**` · dispatch | `build` → `deploy` (main only) | `github-pages` | — |
| `deploy-all.yml` | dispatch (`environment`, `create-model-deployments`, `run-seed-test`) | infra → prepare-database → database → api ∥ functions ∥ ui → postdeployment-tests → summary | the chosen environment, per stage | — |
| `claude-review.yml`, `claude-triage.yml` | see `CLAUDE.md` | — | — | `CLAUDE_AUTOMATION_ENABLED` |

### Reusable children (`workflow_call`)

| File | Does | Inputs | Outputs |
|---|---|---|---|
| `_deploy-infra.yml` | `validate` (`az bicep build` + `lint` on main and every module, `build-params` for the env) → `what-if` (PR mode; upserts one comment per environment, artifact `what-if-{env}`) or `deploy` (what-if preview in the same job, then `az deployment sub create --name foundrygate-{env}`; exports outputs) | `environment`, `mode` (`what-if` \| `deploy`), `create-model-deployments`, `post-what-if-comment` | resource names from the outputs contract, `container-app-is-bootstrap-image` |
| `_prepare-database.yml` | resolves SQL names from the infra outputs; builds the `dacpac` and `foundrygate-cli` artifacts `_deploy-database.yml` downloads; surfaces the OIDC ids for its `secrets:` inputs ([#137](https://github.com/kolatts/foundry-gate/issues/137) removes that bridge) | `environment` | `sql-server-name`, `sql-database-name`, `resource-group`, `azure-*` |
| `_deploy-database.yml` | runner IP whitelist (`foundrygate ip setup`) → 60 s firewall wait → TCP 1433 probe → dacpac deploy (DacFx, data-loss blocked unless `allow-data-loss`) → seed reference → seed test (never in production) | `environment`, `sql-*`, `run-seed-test`, `allow-data-loss`; secrets `AZURE_*` | — |
| `_deploy-api.yml` | GitVersion → `docker build` (`src/FoundryGate.Api/Dockerfile`) → local `/health` smoke → `az acr login` + push → roll the Container App (`az containerapp update --image`, or an infra re-run with `FG_API_IMAGE` when the app still runs the bootstrap placeholder — that flips port 8080 and the probes together with the image) → wait for a Healthy revision → `GET /health` 200 over the FQDN (`/health/ready` reported, not gating — [#106](https://github.com/kolatts/foundry-gate/issues/106)) | `environment` | `image`, `api-base-url` |
| `_deploy-functions.yml` | `dotnet publish` (artifact keeps the hidden `.azurefunctions` folder) → `Azure/functions-action@v1` with `sku: flexconsumption`, `remote-build: false`, RBAC auth → `az functionapp show` state must be `Running` | `environment` | `function-app-hostname` |
| `_deploy-ui.yml` | `dotnet publish` FoundryGate.Web → rewrites `wwwroot/appsettings.json` (`Api.BaseUrl` from the Container App FQDN, `AzureAd.Authority`/`ClientId` and `Api.Scopes` from variables; stale `.br`/`.gz` copies removed) → deployment token via `az staticwebapp secrets list` (masked, never stored) → `Azure/static-web-apps-deploy@v1` `upload` or `close` | `environment`, `action` | `static-web-app-hostname` |
| `_postdeployment-tests.yml` | waits for `/health`, runs `FoundryGate.Tests.Postdeployment` with `FG_API_BASE_URL` / `FG_UI_BASE_URL`; **reporting only** until [#139](https://github.com/kolatts/foundry-gate/issues/139) | `environment` | `summary` |

### Composite actions and scripts

| Path | Purpose |
|---|---|
| `.github/actions/azure-oidc-login` | guard + `azure/login@v2` + dynamic CLI extension install |
| `.github/actions/version` | GitVersion setup/execute, `image-tag` output |
| `.github/scripts/infra/resolve-api-image.sh` | the `FG_API_IMAGE` the param files require: current Container App image, or the placeholder on day 0 — any other error is fatal so a re-run can never reset the API |
| `.github/scripts/infra/what-if.sh` | `az deployment sub what-if`, ANSI-stripped, summary line extracted |
| `.github/scripts/infra/export-outputs.sh` | deployment outputs → step outputs + `FG_*` environment variables + job summary |

## GitHub Environments

| Environment | Protection | Deploys from | Used by |
|---|---|---|---|
| `dev` | none — automatic | any branch | every dev deploy, PR what-if, UI previews |
| `production` | 1 required reviewer | protected branches (`main`) only | production deploys |
| `dev-destroy` | required reviewer + 5 min wait | any branch | `infra-destroy.yml` (dev) |
| `prod-destroy` | required reviewer + 30 min wait (a second reviewer is an owner action, #109) | protected branches only | `infra-destroy.yml` (production) |
| `github-pages` | GitHub-managed | `main` | `docs-deploy.yml` |

GitHub approves **pending jobs**, not whole runs: a full-stack production deploy asks once per
stage (infra, database prepare, database, the three app deploys together, tests). Whether to
collapse that to a single gate is [#141](https://github.com/kolatts/foundry-gate/issues/141).

### Variables (per Environment)

| Variable | dev | production | *-destroy | Purpose |
|---|---|---|---|---|
| `AZURE_CLIENT_ID` | required | required | required | app registration with a federated credential whose subject is `repo:<owner>/foundry-gate:environment:<name>` |
| `AZURE_TENANT_ID` | required | required | required | |
| `AZURE_SUBSCRIPTION_ID` | required | required | required | |
| `AZURE_LOCATION` | optional (`eastus2`) | optional | — | deployment metadata location |
| `FG_ENTRA_API_CLIENT_ID` | recommended | recommended | — | FoundryGate.Api app registration → `entraApiClientId`, UI `Api.Scopes` |
| `FG_ENTRA_WEB_CLIENT_ID` | recommended | recommended | — | Blazor SPA app registration → UI `AzureAd.ClientId` |
| `FG_SQL_ADMIN_GROUP_OBJECT_ID` / `_NAME` | — | **required** | — | `prod.bicepparam` has no default (Entra-only SQL) |

There are **no secrets**: not for Azure (OIDC), not for SQL (Entra-only), not for Static Web
Apps (token read at run time). Resource names are not variables either — they come from the
infra outputs.

### What the deploying principal needs

Owner-equivalent on the subscription (resource group + role assignments at subscription scope),
`AcrPush` on the registry, membership of the SQL Entra admin group for the dacpac deploy, and —
only for a day-0 run with `create-model-deployments=true` — the Marketplace permissions from
[#107](https://github.com/kolatts/foundry-gate/issues/107). Exact steps: #109.

## Runbooks

**Bring up a new environment (day 0).** `Actions → Deploy All → Run workflow`, choose the
environment, tick **create-model-deployments** — once. The infra stage deploys with the
placeholder API image; the API stage detects that and replaces it via an infra re-run with the
real image. Afterwards every run (automatic or manual) leaves `create-model-deployments` off:
Anthropic deployments are create-once under ARM.

**Change infra.** Open a PR touching `infra/**`; read the what-if comment; merge → dev deploys.
Promote to production with `Actions → Infra Deploy → dev-then-production` and approve the gate.

**Ship code.** Merge to `main`; the matching `*-deploy.yml` deploys dev. Production is a
dispatch of the same workflow with `environment: production`, or `deploy-all.yml`.

**Destroy an environment.** `Actions → Infra Destroy`, type `DESTROY-dev` or
`DESTROY-production`, read the listing in the summary, approve the destroy gate after the wait
timer. The summary then tells you what the next day-0 run needs (`create-model-deployments`,
soft-deleted APIM/Key Vault names — production's Key Vault has purge protection and blocks its
name for the retention period).

## Verified so far

- `actionlint` clean on every workflow and composite action.
- `src/FoundryGate.Api/Dockerfile` builds locally; the container runs as the non-root `app`
  user on 8080 and answers `/health` with 200 (`/health/ready` 503 without a database, as
  designed).
- `az bicep build` / `build-params` untouched by this work (validated in PR #111).
- Not yet: any run against Azure — [#105](https://github.com/kolatts/foundry-gate/issues/105).
