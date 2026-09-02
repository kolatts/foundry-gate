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
             ├─► .github/**          → actionlint.yml     : actionlint + shellcheck + script tests
             ├─► infra/**            → infra-deploy.yml   : validate + what-if (dev-plan), posted as a PR comment
             ├─► Dockerfile          → api-deploy.yml     : image build + /health smoke test
             └─► docs-site/**        → docs-deploy.yml    : Astro build

merge to main ─► anything deployable → deploy-all.yml   : THE chain, against dev
              │     (infra/**, src/**, Directory.*.props, global.json, NuGet.config,
              │      .github/workflows/_deploy-*.yml, .github/scripts/**, .github/actions/**)
              │
              │     infra → prepare-database → database → api → functions ∥ ui
              │           → postdeployment tests → summary
              └─► docs-site/**       → docs-deploy.yml : GitHub Pages

manual ───────► deploy-all.yml    : the same chain against dev or production
              ├─► infra-/api-/functions-/ui-deploy.yml : redeploy one component
              └─► infra-destroy.yml : typed confirmation → listing → destroy gate → delete resource group
```

**One workflow deploys on merge.** `deploy-all.yml` is the only workflow with a `push` trigger
(besides docs). The single-component wrappers — `infra-deploy.yml`, `api-deploy.yml`,
`functions-deploy.yml`, `ui-deploy.yml` — have **no** `push` trigger: they keep their PR-track
jobs and a `workflow_dispatch` for targeted redeploys. That is deliberate. They all shared the
`deploy-{env}` concurrency group, and GitHub keeps exactly one *pending* run per group, so a
change to `src/FoundryGate.Domain/**` or `Directory.Packages.props` used to start three
workflows, run one, queue one and silently cancel the third.

Inside the chain, `functions` and `ui` wait for `api` rather than fanning out beside it: on a
day-0 run the api stage re-runs the whole subscription deployment to replace the bootstrap
placeholder, and an infra re-run restarting the Function App mid-upload is a real flake.
GitHub Actions has no conditional `needs:`, so the dependency is unconditional.

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
  checks they are set (and that an OIDC token is actually obtainable) and fails with a readable
  `::error::` pointing at #109 instead of an opaque AADSTS error. On **any `pull_request` event**
  both checks are soft: the action emits a `::notice::`, sets `configured=false` and the job
  skips. A fork resolves the base repo’s Environment variables but never receives an OIDC token,
  so the id-token branch has to be soft too.
- **Permissions**: callers grant `id-token: write` + `contents: read` (+ `pull-requests: write`
  where a comment is posted); reusable workflows only narrow.
- **Concurrency**: one lock per environment (`deploy-dev`, `deploy-production`) held by
  `deploy-all.yml` and by every wrapper’s `workflow_dispatch`, never cancelled mid-flight. Each
  reusable child has its own group (`infra-{env}`, `api-{env}`, `functions-{env}`, `ui-{env}`,
  `deploy-database-{env}`) so nesting never deadlocks; `_prepare-database.yml` and
  `_postdeployment-tests.yml` have no group of their own — they are ordered by `needs:` inside
  the chain and neither mutates the environment. PR jobs use a per-PR group with
  `cancel-in-progress: true`.
- **Version**: `.github/actions/version` runs GitVersion 6 (`GitVersion.yml`, trunk-based —
  every commit on `main` bumps patch; `+semver: minor|major` in a commit message overrides).
  Assemblies get `Version`/`AssemblyVersion`; the API image is tagged `{semVer}-{shortSha}` and
  `{semVer}`. There are no tags in the repository yet, so `next-version: 0.1.0` sets the floor;
  the first release should be an annotated tag on `main` (`git tag -a v0.1.0 -m "v0.1.0" && git
  push origin v0.1.0`), after which the tag — not `GitVersion.yml` — is the source of truth.
- **`timeout-minutes` on every job**: 20 for builds and validation, 30–45 for deploys, 60 for
  the infra deploy and the destroy. A hung `az` command would otherwise burn the 6-hour default
  *while holding a per-environment lock*.

## Workflow inventory

| File | Triggers | Jobs | Gate | Reads |
|---|---|---|---|---|
| `ci.yml` | PR, merge queue | `build-test` (**required check**), `docs-build` | — | — |
| `deploy-all.yml` | **push `main`** (`infra/**`, `src/**`, `Directory.*.props`, `global.json`, `NuGet.config`, `.github/workflows/_deploy-*.yml`, `.github/scripts/**`, `.github/actions/**`) · dispatch (`environment`, `create-model-deployments`, `run-seed-test`) | infra → prepare-database → database → api → functions ∥ ui → postdeployment-tests → summary | the target environment, per stage | — |
| `infra-deploy.yml` | PR `infra/**` → what-if comment · dispatch (`environment`, `create-model-deployments`) | calls `_deploy-infra.yml` | `dev-plan` (PR what-if) · `dev` / `production` (dispatch) | — |
| `infra-destroy.yml` | dispatch only (`environment`, `confirmation`, `purge-soft-deleted`) | `validate-confirmation` → `list-resources` → `destroy` | `dev-destroy` / `prod-destroy` | `AZURE_*` on the destroy environment |
| `api-deploy.yml` | PR touching the Dockerfile → image build check · dispatch (`environment`, `run-seed-test`) | `image-build` (PR, no Azure) · `_prepare-database` → `_deploy-database` → `_deploy-api` | `dev` / `production` | — |
| `functions-deploy.yml` | dispatch only | `_deploy-functions` | `dev` / `production` | — |
| `ui-deploy.yml` | dispatch only | `_deploy-ui` | `dev` / `production` | — |
| `docs-deploy.yml` | PR/push `docs-site/**`, `content/**` · dispatch | `build` → `deploy` (only when `github.ref == refs/heads/main`) | `github-pages` | — |
| `actionlint.yml` | PR `.github/**` | `actionlint` (+ shellcheck) · composite-action parse · `resolve-api-image.test.sh` | — | — |
| `claude-review.yml`, `claude-triage.yml` | see `CLAUDE.md` | — | — | `CLAUDE_AUTOMATION_ENABLED` |

Nothing that can mutate Azure or read a deployment token runs on `pull_request`. The PR track is:
`ci.yml`, `claude-review.yml`, `actionlint.yml`, the Docker image build (no credentials at all),
the Astro build, and a Bicep what-if under the read-only `dev-plan` identity.

### Reusable children (`workflow_call`)

| File | Does | Inputs | Outputs |
|---|---|---|---|
| `_deploy-infra.yml` | `validate` (rejects an unknown `mode`, then `az bicep build` + `lint` on main and every module, `build-params` for the env) → `what-if` (PR mode, under `plan-environment`; upserts one comment per environment, artifact `what-if-{env}`) or `deploy` (what-if preview in the same job, then `.github/scripts/infra/deploy.sh`; exports outputs) | `environment`, `plan-environment`, `mode` (`what-if` \| `deploy`), `create-model-deployments`, `post-what-if-comment` | resource names from the outputs contract, `container-app-is-bootstrap-image` |
| `_prepare-database.yml` | resolves SQL names and the API/Functions identity client ids from the infra outputs; builds the `dacpac` and `foundrygate-cli` artifacts `_deploy-database.yml` downloads; surfaces the OIDC ids for its `secrets:` inputs ([#137](https://github.com/kolatts/foundry-gate/issues/137) removes that bridge) | `environment` | `sql-server-name`, `sql-database-name`, `resource-group`, `api-identity-client-id`, `functions-identity-client-id`, `azure-*` |
| `_deploy-database.yml` | runner IP whitelist (`foundrygate ip setup`) → 60 s firewall wait → TCP 1433 probe → dacpac deploy (DacFx, data-loss blocked unless `allow-data-loss`) → seed reference → seed test (never in production) | `environment`, `sql-*`, `run-seed-test`, `allow-data-loss`; secrets `AZURE_*` | — |
| `_deploy-api.yml` | GitVersion → `docker build` (`src/FoundryGate.Api/Dockerfile`) → local `/health` smoke → `az acr login` + push → roll the Container App (`az containerapp update --image`, or an infra re-run with `FG_API_IMAGE` when the app still runs the bootstrap placeholder — that flips port 8080 and the probes together with the image) → wait for a Healthy revision → `GET /health` 200 over the FQDN (`/health/ready` reported, not gating — [#106](https://github.com/kolatts/foundry-gate/issues/106)) | `environment` | `image`, `api-base-url` |
| `_deploy-functions.yml` | `dotnet publish` (artifact keeps the hidden `.azurefunctions` folder) → `Azure/functions-action@v1` with `sku: flexconsumption`, `remote-build: false`, RBAC auth → `az functionapp show` state must be `Running` | `environment` | `function-app-hostname` |
| `_deploy-ui.yml` | `dotnet publish` FoundryGate.Web → rewrites `wwwroot/appsettings.json` (`Api.BaseUrl` from the Container App FQDN, `AzureAd.Authority`/`ClientId` and `Api.Scopes` from variables; stale `.br`/`.gz` copies removed) → deployment token via `az staticwebapp secrets list` (masked, never stored) → `Azure/static-web-apps-deploy@v1` `upload` | `environment` | `static-web-app-hostname` |
| `_postdeployment-tests.yml` | waits for `/health`, really runs `dotnet test` on `FoundryGate.Tests.Postdeployment` with `FG_API_BASE_URL` / `FG_UI_BASE_URL`, and reports the step’s actual outcome; the step is `continue-on-error` so a failure does not gate the chain until [#139](https://github.com/kolatts/foundry-gate/issues/139). The project holds only the scaffold smoke test, and the summary says so — a green line here is not evidence the stack works | `environment` | `summary` |

### Composite actions and scripts

| Path | Purpose |
|---|---|
| `.github/actions/azure-oidc-login` | guard + `azure/login@v2` + dynamic CLI extension install; soft-skips on `pull_request` |
| `.github/actions/version` | GitVersion setup/execute, `image-tag` output |
| `.github/actions/build-api-image` | `docker build` of `src/FoundryGate.Api/Dockerfile` + local `/health` 200 smoke test; shared by the PR image check and the real build-and-push |
| `.github/scripts/infra/resolve-api-image.sh` | the `FG_API_IMAGE` the param files require: the Container App named by the deployment outputs and its current image, or the placeholder when the `foundrygate-{env}` deployment record does not exist yet. Everything else — auth, throttling, a resource group deleted behind the record, an app with no image — is **fatal**, and the value is written to `$GITHUB_OUTPUT` by the script so no caller can swallow the exit code |
| `.github/scripts/infra/resolve-api-image.test.sh` | 11 offline cases over the above, driven by a stub `az` (`FG_AZ`); run by `actionlint.yml` and by `bash .github/scripts/infra/resolve-api-image.test.sh` locally |
| `.github/scripts/infra/what-if.sh` | `az deployment sub what-if`, ANSI-stripped, summary line extracted |
| `.github/scripts/infra/deploy.sh` | the single `az deployment sub create`, used by the infra stage and by the day-0 bootstrap re-run; refuses to run with an empty `FG_API_IMAGE` |
| `.github/scripts/infra/export-outputs.sh` | deployment outputs → step outputs + `FG_*` environment variables + job summary |

## GitHub Environments

| Environment | Protection | Deploys from | Used by |
|---|---|---|---|
| `dev` | none — automatic | **protected branches only** | every dev deploy |
| `dev-plan` | none | any branch | the PR-track Bicep what-if **only**. Its identity is intended to hold **Reader** on the subscription and nothing else (#109) |
| `production` | 1 required reviewer | protected branches (`main`) only | production deploys |
| `dev-destroy` | required reviewer + 5 min wait | any branch | `infra-destroy.yml` (dev) |
| `prod-destroy` | required reviewer + 30 min wait (a second reviewer is an owner action, #109) | protected branches only | `infra-destroy.yml` (production) |
| `github-pages` | GitHub-managed | `main` | `docs-deploy.yml` |

`dev` is restricted to protected branches because its identity is Owner on the subscription and
a `pull_request` run executes the PR branch’s own copy of the workflow files — without the
restriction, any PR could have run arbitrary `az` against dev or printed the Static Web App
deployment token. That is also why the Static Web Apps PR previews were removed
([#155](https://github.com/kolatts/foundry-gate/issues/155) tracks restoring them behind a
narrower identity) and why the PR what-if runs under `dev-plan`.

GitHub approves **pending jobs**, not whole runs: a full-stack production deploy asks once per
stage (infra, database prepare, database, api, functions and ui together, tests). Whether to
collapse that to a single gate is [#141](https://github.com/kolatts/foundry-gate/issues/141).

### Variables (per Environment)

| Variable | dev | dev-plan | production | *-destroy | Purpose |
|---|---|---|---|---|---|
| `AZURE_CLIENT_ID` | required | required | required | required | app registration with a federated credential whose subject is `repo:<owner>/foundry-gate:environment:<name>`. `dev-plan`’s is a **separate, Reader-only** registration |
| `AZURE_TENANT_ID` | required | required | required | required | |
| `AZURE_SUBSCRIPTION_ID` | required | required | required | required | |
| `AZURE_LOCATION` | optional (`eastus2`) | optional | optional | — | deployment metadata location |
| `FG_ENTRA_API_CLIENT_ID` | recommended | optional | recommended | — | FoundryGate.Api app registration → `entraApiClientId`, UI `Api.Scopes` |
| `FG_ENTRA_WEB_CLIENT_ID` | recommended | — | recommended | — | Blazor SPA app registration → UI `AzureAd.ClientId` |
| `FG_SQL_ADMIN_GROUP_OBJECT_ID` / `_NAME` | — | — | **required** | — | `prod.bicepparam` has no default (Entra-only SQL) |

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

**Change infra.** Open a PR touching `infra/**`; read the what-if comment (run under the
read-only `dev-plan` identity); merge → `deploy-all.yml` runs the whole chain against dev,
starting with that infra deploy. Promote with `Actions → Deploy All → environment: production`
and approve the gate at each stage; `Actions → Infra Deploy → environment: production` promotes
the infrastructure alone. One run holds one environment lock, so promotion is two dispatches
rather than one `dev-then-production` run.

**Ship code.** Merge to `main`. `deploy-all.yml` runs the whole ordered chain against dev — not
just the component you touched, because schema, API, Functions and UI share `FoundryGate.Domain`
and the ordering between them is the point. To redeploy one component (a flaky Functions
publish, a rollback), dispatch its wrapper: `API Deploy`, `Functions Deploy`, `UI Deploy`,
`Infra Deploy`. Production is `deploy-all.yml` with `environment: production`, or the same
per-component dispatch.

**Destroy an environment.** `Actions → Infra Destroy`, type `DESTROY-dev` or
`DESTROY-production`, read the listing in the summary, approve the destroy gate after the wait
timer. The summary then tells you what the next day-0 run needs (`create-model-deployments`,
soft-deleted APIM/Key Vault names — production's Key Vault has purge protection and blocks its
name for the retention period).

## Verified so far

- `actionlint` clean on every workflow and composite action — and now enforced on PRs touching
  `.github/**` by `actionlint.yml` ([#140](https://github.com/kolatts/foundry-gate/issues/140)).
- `resolve-api-image.sh`: 11 offline cases pass, covering every bootstrap and every fatal branch.
- `src/FoundryGate.Api/Dockerfile` builds locally; the container runs as the non-root `app`
  user on 8080 and answers `/health` with 200 (`/health/ready` 503 without a database, as
  designed).
- `az bicep build` / `build-params` untouched by this work (validated in PR #111).
- Not yet: any run against Azure — [#105](https://github.com/kolatts/foundry-gate/issues/105).
  The chain also cannot complete end to end until [#143](https://github.com/kolatts/foundry-gate/pull/143)
  lands: `_deploy-database.yml`’s `foundrygate ip setup` is still the #96 stub behind
  `continue-on-error: true`, so the dacpac deploy would reach an unwhitelisted runner.
