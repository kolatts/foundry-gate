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
             ├─► FoundryGate.Web/**  → ui-deploy.yml      : SWA preview (ui-preview), closed when the PR closes
             └─► docs-site/**        → docs-deploy.yml    : Astro build

merge to main ─► anything deployable → deploy-all.yml   : THE chain, against dev
              │     (infra/**, src/**, Directory.*.props, global.json, NuGet.config,
              │      .github/workflows/_deploy-*.yml, .github/scripts/**, .github/actions/**)
              │
              │     plan → infra → prepare-database → database → api → functions ∥ ui
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
| `deploy-all.yml` | **push `main`** (`infra/**`, `src/**`, `Directory.*.props`, `global.json`, `NuGet.config`, `.github/workflows/_deploy-*.yml`, `.github/scripts/**`, `.github/actions/**`) · dispatch (`environment`, `create-model-deployments`, `run-seed-test`) | plan → infra → prepare-database → database → api → functions ∥ ui → postdeployment-tests → summary | the target environment, once per gated job (six for a full production run) | — |
| `infra-deploy.yml` | PR `infra/**` → what-if comment · dispatch (`environment`, `create-model-deployments`) | calls `_deploy-infra.yml` | `dev-plan` (PR what-if) · `dev` / `production` (dispatch) | — |
| `infra-destroy.yml` | dispatch only (`environment`, `confirmation`, `purge-soft-deleted`) | `validate-confirmation` → `list-resources` → `destroy` | `dev-destroy` / `prod-destroy` | `AZURE_*` on the destroy environment |
| `api-deploy.yml` | PR touching the Dockerfile → image build check · dispatch (`environment`, `run-seed-test`) | `image-build` (PR, no Azure) · `_prepare-database` → `_deploy-database` → `_deploy-api` | `dev` / `production` | — |
| `functions-deploy.yml` | dispatch only | `_deploy-functions` | `dev` / `production` | — |
| `ui-deploy.yml` | PR touching `src/FoundryGate.Web/**` (+ Domain/Core, shared props) → SWA preview · dispatch (`environment`) | `preview` / `close-preview` (PR) · `_deploy-ui` (dispatch) | `ui-preview` (PR) · `dev` / `production` (dispatch) | `AZURE_*`, `FG_STATIC_WEB_APP_NAME`, `FG_RESOURCE_GROUP`, `FG_API_BASE_URL`, `FG_ENTRA_*` on `ui-preview` |
| `docs-deploy.yml` | PR/push `docs-site/**`, `content/**` · dispatch | `build` → `deploy` (only when `github.ref == refs/heads/main`) | `github-pages` | — |
| `actionlint.yml` | PR `.github/**` | `actionlint` (+ shellcheck) · composite-action parse · `resolve-api-image.test.sh` | — | — |
| `claude-review.yml`, `claude-triage.yml` | see `CLAUDE.md` | — | — | `CLAUDE_AUTOMATION_ENABLED` |

Nothing on the PR track can reach an identity with subscription access. The PR track is:
`ci.yml`, `claude-review.yml`, `actionlint.yml`, the Docker image build (no credentials at all),
the Astro build, a Bicep what-if under the read-only `dev-plan` identity, and the Static Web Apps
preview under `ui-preview`.

That last one is the exception that proves the rule, and it is worth stating precisely. A
`pull_request` run executes the PR branch's own copy of the workflow files, so the workflow
cannot protect anything — only the identity can.

**Through ARM**, `ui-preview` holds exactly one role. There is no built-in one to hold:
`Static Web App Contributor` does not exist, and no built-in role grants *any*
`Microsoft.Web/staticSites` action, so the built-in answer would have been plain Contributor —
Write and Delete on the site included. `infra/modules/swa-preview-role.bicep` defines
`FoundryGate SWA Preview Publisher` instead: four actions (`staticSites/Read`,
`staticSites/listsecrets/action`, `staticSites/builds/Read`, `staticSites/builds/Delete`),
`assignableScopes` containing only the dev Static Web App, so it cannot be assigned anywhere
else even deliberately.

**Through the deployment token**, the boundary is softer, and the docs should not pretend
otherwise: the token is **app-scoped, not slot-scoped**. Azure exposes no slot-scoped Static Web
Apps credential, so a compromised PR-track run could publish to the dev site's *production*
slot, not merely a preview slot — and that hostname is a registered MSAL redirect origin for the
dev SPA app registration. FoundryGate accepts that explicitly as a **dev-only** risk: the
production Static Web App is a different resource this identity has no role on, the dev site
holds no data, and recovery is one `UI Deploy` dispatch. `deployment_environment: pr-<n>` makes
the intended staging target explicit and auditable, but it is a convention, not an enforcement.

The two invariants that keep the ARM half true: the preview jobs resolve nothing from the
deployment outputs (app name, resource group and API base URL are `ui-preview` Environment
variables — an identity with a role on one resource could not read those outputs anyway), and
the only Azure calls are `az staticwebapp secrets list` / `environment list` against that one
app, both inside `.github/actions/swa-preview-token`. A change that makes a PR-track job read
subscription-scope state is a change that reopens the hole #144 closed.

Two things a preview cannot do, both tracked on [#138](https://github.com/kolatts/foundry-gate/issues/138):
sign-in fails (the preview hostname is not a registered redirect URI) and every API call is
CORS-blocked (`control-plane.bicep` allows exactly one origin, the dev site's own). A preview is
honest for layout and for routes that never call the API, and nothing more, until #138 lands
wildcard-subdomain CORS on the API side as well as the redirect URIs.

`stapp-foundrygate-dev` is Free tier, which allows **three** staging environments. The preview
job counts them before it publishes and soft-skips with a `::notice::` at the limit rather than
failing inside the deploy action after the token fetch — close a stale UI PR, or move the dev
SWA to Standard.

### Reusable children (`workflow_call`)

| File | Does | Inputs | Outputs |
|---|---|---|---|
| `_deploy-infra.yml` | `validate` (rejects an unknown `mode`, then `az bicep build` + `lint` on main and every module, `build-params` for the env) → `what-if` (PR mode, under `plan-environment`; upserts one comment per environment, artifact `what-if-{env}`) or `deploy` (what-if preview in the same job, then `.github/scripts/infra/deploy.sh`; exports outputs) | `environment`, `plan-environment`, `mode` (`what-if` \| `deploy`), `create-model-deployments`, `post-what-if-comment` | resource names from the outputs contract, `container-app-is-bootstrap-image` |
| `_prepare-database.yml` | resolves SQL names and the API/Functions identity client ids from the infra outputs; builds the `dacpac` and `foundrygate-cli` artifacts `_deploy-database.yml` downloads | `environment` | `sql-server-name`, `sql-database-name`, `resource-group`, `api-identity-client-id`, `functions-identity-client-id` |
| `_deploy-database.yml` | runner IP whitelist (`foundrygate ip setup`) → 60 s firewall wait → TCP 1433 probe → dacpac deploy (DacFx, data-loss blocked unless `allow-data-loss`) → seed reference → seed test (never in production) → `db grant-identities` (contained users for the API and Functions identities) → `ip cleanup` | `environment`, `sql-*`, `run-seed-test`, `allow-data-loss`, `api-identity-client-id`, `functions-identity-client-id`, `allow-external-provider`, `firewall-rule-max-age-hours` — **no secrets** | — |

`_deploy-database.yml` takes no `secrets:` inputs: its `deploy` job declares
`environment: ${{ inputs.environment }}`, so it reads `vars.AZURE_CLIENT_ID` / `_TENANT_ID` /
`_SUBSCRIPTION_ID` itself through `.github/actions/azure-oidc-login` like every other
`_deploy-*.yml` ([#137](https://github.com/kolatts/foundry-gate/issues/137) — the
`_prepare-database.yml` bridge that used to pass them as job outputs is gone). It receives the
**GitHub Environment** name (`dev` | `production`) and passes it straight to the CLI's `--env`;
`FoundryGateAzureResources.NormalizeEnvironment` maps `production` → the Bicep `prod` that every
resource name embeds.

`_prepare-database.yml` passes `api-identity-client-id` / `functions-identity-client-id` from the
`apiIdentityClientId` / `functionsIdentityClientId` deployment outputs, so `db grant-identities`
issues `CREATE USER … WITH SID = <client id>, TYPE = E`. That is the path that does **not** need
Directory Readers on the SQL server identity — `CREATE USER … FROM EXTERNAL PROVIDER` (the
`allow-external-provider` fallback) does, because Azure SQL then has to resolve the name in
Entra on behalf of a service principal.
| `_deploy-api.yml` | **one gated job** (it was two until #198): GitVersion → `docker build` (`src/FoundryGate.Api/Dockerfile`) → local `/health` smoke → `az acr login` + push → roll the Container App (`az containerapp update --image`, or an infra re-run with `FG_API_IMAGE` when the app still runs the bootstrap placeholder — that flips port 8080 and the probes together with the image) → wait for a Healthy revision → `GET /health` 200 over the FQDN (`/health/ready` reported, not gating — [#106](https://github.com/kolatts/foundry-gate/issues/106)) | `environment` | `image`, `api-base-url` |
| `_deploy-functions.yml` | `dotnet publish` (artifact keeps the hidden `.azurefunctions` folder) → `Azure/functions-action@v1` with `sku: flexconsumption`, `remote-build: false`, RBAC auth → the ARM `properties.state` of the site must be `Running`, read with `az resource show` at a pinned API version. **Not** `az functionapp show`: that projection returns an empty `state` on Flex Consumption, so the assertion could never pass ([#252](https://github.com/kolatts/foundry-gate/issues/252)) | `environment` | `function-app-hostname` |
| `_deploy-ui.yml` | `dotnet publish` FoundryGate.Web → rewrites `wwwroot/appsettings.json` (`Api.BaseUrl` from the Container App FQDN, `AzureAd.Authority`/`ClientId` and `Api.Scopes` from variables; stale `.br`/`.gz` copies removed) → deployment token via `az staticwebapp secrets list` (masked, never stored) → `Azure/static-web-apps-deploy@v1` `upload` | `environment` | `static-web-app-hostname` |
| `_postdeployment-tests.yml` | waits for `/health`, really runs `dotnet test` on `FoundryGate.Tests.Postdeployment` with `FG_API_BASE_URL` / `FG_UI_BASE_URL`, and reports the step’s actual outcome; the step is `continue-on-error` so a failure does not gate the chain until [#139](https://github.com/kolatts/foundry-gate/issues/139). The project holds only the scaffold smoke test, and the summary says so — a green line here is not evidence the stack works | `environment` | `summary` |

### Composite actions and scripts

| Path | Purpose |
|---|---|
| `.github/actions/azure-oidc-login` | guard + `azure/login@v2` + dynamic CLI extension install; soft-skips on `pull_request` |
| `.github/actions/version` | GitVersion setup/execute, `image-tag` output |
| `.github/actions/build-api-image` | `docker build` of `src/FoundryGate.Api/Dockerfile`, then two checks on the built image: it ships no `appsettings.*.json` developer configuration (`.dockerignore` patterns are case-sensitive, so a mis-cased pattern fails silently — the check belongs on the artefact), and it answers `/health` 200 with configuration supplied **from the environment**, exactly as Container Apps supplies it. Shared by the PR image check and the real build-and-push |
| `.github/actions/swa-preview-token` | the whole Azure surface of the PR-preview track in one place: `ui-preview` OIDC login, target check and `az staticwebapp secrets list`. Both preview jobs use it, so neither carries its own copy to drift |
| `.github/scripts/infra/resolve-api-image.sh` | the `FG_API_IMAGE` the param files require: the Container App named by the deployment outputs and its current image, or the placeholder when the `foundrygate-{env}` deployment record does not exist yet. Everything else — auth, throttling, a resource group deleted behind the record, an app with no image — is **fatal**, and the value is written to `$GITHUB_OUTPUT` by the script so no caller can swallow the exit code |
| `.github/scripts/infra/resolve-api-image.test.sh` | 11 offline cases over the above, driven by a stub `az` (`FG_AZ`); run by `actionlint.yml` and by `bash .github/scripts/infra/resolve-api-image.test.sh` locally |
| `.github/scripts/infra/what-if.sh` | `az deployment sub what-if`, ANSI-stripped, summary line extracted |
| `.github/scripts/infra/deploy.sh` | the single `az deployment sub create`, used by the infra stage and by the day-0 bootstrap re-run; refuses to run with an empty `FG_API_IMAGE` |
| `.github/scripts/infra/export-outputs.sh` | deployment outputs → step outputs + `FG_*` environment variables + job summary |

## GitHub Environments

| Environment | Protection | Deploys from | Used by |
|---|---|---|---|
| `dev` | none — automatic | **protected branches only** | every dev deploy |
| `dev-plan` | none | any branch | the PR-track Bicep what-if — **currently unconfigured on purpose, see the note below** ([#229](https://github.com/kolatts/foundry-gate/issues/229)) |
| `ui-preview` | none | any branch | the PR-track Static Web Apps preview **only**. Its identity (`foundrygate-ci-ui-preview`) holds one custom role, **`FoundryGate SWA Preview Publisher`** (`infra/modules/swa-preview-role.bicep`), assignable on `stapp-foundrygate-dev` alone |
| `production` | 1 required reviewer | protected branches (`main`) only | production deploys |
| `dev-destroy` | required reviewer + 5 min wait | any branch | `infra-destroy.yml` (dev) |
| `prod-destroy` | required reviewer + 30 min wait (a second reviewer is an owner action, #109) | protected branches only | `infra-destroy.yml` (production) |
| `github-pages` | GitHub-managed | `main` | `docs-deploy.yml` |

`dev` is restricted to protected branches because its identity is Owner on the subscription and
a `pull_request` run executes the PR branch’s own copy of the workflow files — without the
restriction, any PR could have run arbitrary `az` against dev or printed the Static Web App
deployment token. That is why the PR what-if runs under `dev-plan` and the PR preview under
`ui-preview`, each holding one narrow role: the workflow file is attacker-controlled on the PR
track, so the identity is the only real boundary.

:::caution[The PR what-if is off — a read-only what-if identity turned out to be impossible]
That reasoning holds for `ui-preview` and fails for `dev-plan`. The design assumed `az
deployment sub what-if` is a read-only ARM operation; it is read-only in *effect* but ARM runs
its **full preflight**, which authorizes every resource in the template as a write. A Reader
identity fails with one `Authorization failed for template resource …/write` per resource
(thirteen, for `main.bicep`), and the narrowest identity that succeeds is roughly `Contributor`
— the blast radius `dev-plan` exists to avoid.

So `dev-plan` carries **no variables**, which makes `_deploy-infra.yml` skip the job with a
`::notice::` rather than failing every PR. The identity, its federated credential and its Reader
assignment exist and are inert. Reviewers see the what-if in the gated `deploy` job instead,
where `_deploy-infra.yml` runs one immediately before the real deploy.
[#229](https://github.com/kolatts/foundry-gate/issues/229) carries the options and needs a
decision.
:::

### Production approvals: one per gated job, deliberately

GitHub approves **pending jobs**, not whole runs — and not stages either. The count is a
property of the *job graph*: every job that declares `environment:` in any reusable workflow
`deploy-all.yml` calls stops the run once. A full-stack production run asks **six** times:

| # | Job(s) waiting | What you are approving |
|---|---|---|
| 1 | `Infra - Deploy` | the Bicep change — the what-if preview runs inside this job, so approving it is approving *running* the preview, not the result. `Infra - Validate` is ungated and has already reported |
| 2 | `Database - Prepare` | building the dacpac and reading the deployment outputs (no writes) |
| 3 | `Database - Deploy` | the dacpac deploy, seeding and the SQL firewall window — the irreversible one |
| 4 | `API - Build, push and deploy` | building the image, pushing it to ACR and rolling the Container App |
| 5 | `Functions` + `UI` | two jobs, one visit — they become pending together |
| 6 | `Postdeployment tests` | read-only smoke tests |

Two of those are recent corrections, and both are worth knowing about because they are the kind
of thing that silently drifts:

- `_deploy-api.yml` used to have **two** gated jobs, `build-push` and `deploy`. They were
  strictly sequential (`deploy` did `needs: build-push`, re-checked out, re-logged in and
  re-read the same deployment outputs), so the second gate bought nothing and cost a reviewer
  a click. They are one job now.
- Rows 4 and 5 are separate visits, not one, because `functions` and `ui` declare
  `needs: [database, api]`. Batching them with the API would save an approval and reintroduce
  the day-0 race that ordering exists to prevent: on a bootstrap run the API stage re-runs the
  whole subscription deployment to swap the placeholder image, and an infra re-run restarting
  the Function App mid-upload is a real flake. One extra click is the cheaper side of that trade.

If you add or remove a job that declares `environment:`, this table, the `plan` job in
`deploy-all.yml`, D-019 and `plans/22` are all wrong until you update them together.

This is the decision recorded as **D-019** ([#141](https://github.com/kolatts/foundry-gate/issues/141)):
**keep per-stage approvals.** Each stage is independently re-runnable, and the gates are what
make a `dev-then-production` chain unnecessary — the checkpoint between "infra applied" and
"schema migrated" is the whole point of stage 3 being separate from stage 1.

The single-gate alternative (#141 Option A — one `approve-production` job, then the real work
against an unprotected `production-deploy` Environment carrying the same variables and its own
federated credential) is rejected: it moves the deploy identity **behind no gate at all**. Any
job in any workflow could target `production-deploy` and get a production-Owner token without
a reviewer ever seeing it, which is exactly the property the environment gate exists to
provide. The variant that keeps the credential inside the approved job and passes it onward is
worse, not better: an Azure access token handed between jobs is a bearer credential in a job
output. OIDC gives no third option — the token is minted per job and the Environment name is
baked into its `subject`, so a job that does not declare `environment: production` cannot mint
a token that federates to production, full stop.

What we did instead is make the sequence legible and shorten it where it was genuinely
redundant: `deploy-all.yml` starts with an ungated `plan` job (no `permissions:` at all) that
writes the table above into the run summary before the first gate, so a reviewer knows how many
approvals are coming and what each one buys — and the API's two gates became one.

### Variables (per Environment)

| Variable | dev | dev-plan | production | *-destroy | Purpose |
|---|---|---|---|---|---|
| `AZURE_CLIENT_ID` | required | *unset (#229)* | required | required | app registration with a federated credential whose subject is `repo:<owner>/foundry-gate:environment:<name>` |
| `AZURE_TENANT_ID` | required | *unset* | required | required | also written into the SPA's `AzureAd.Authority` by `_deploy-ui.yml` |
| `AZURE_SUBSCRIPTION_ID` | required | *unset* | required | required | |
| `AZURE_LOCATION` | optional (`eastus2`) | — | optional | — | deployment metadata location |
| `FG_ENTRA_API_CLIENT_ID` | recommended | optional | recommended | — | FoundryGate.Api app registration → `entraApiClientId`, UI `Api.Scopes` |
| `FG_ENTRA_WEB_CLIENT_ID` | recommended | — | recommended | — | Blazor SPA app registration → UI `AzureAd.ClientId` |
| `FG_SQL_ADMIN_GROUP_OBJECT_ID` / `_NAME` | — | — | **required** | — | `prod.bicepparam` has no default (Entra-only SQL) |

`ui-preview` has its own set, because its identity is scoped to one resource and cannot read
the deployment outputs every other workflow resolves names from:

| Variable | Purpose |
|---|---|
| `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | the preview app registration (holds `FoundryGate SWA Preview Publisher` on `stapp-foundrygate-dev`, and nothing else) |
| `FG_STATIC_WEB_APP_NAME` / `FG_RESOURCE_GROUP` | which Static Web App to publish the preview slot to — the preview never resolves these from the deployment outputs |
| `FG_API_BASE_URL` | `Api.BaseUrl` in the preview's `appsettings.json`, e.g. `https://<dev container app fqdn>/api/v1/` |
| `FG_ENTRA_WEB_CLIENT_ID` / `FG_ENTRA_API_CLIENT_ID` | same MSAL ids as `dev` |

All of them missing is a **skip**, not a failure: the preview job emits a `::notice::` and goes
green, so a fork PR (which never receives an OIDC token) is never red for infrastructure it
cannot have.

There are **no secrets**: not for Azure (OIDC), not for SQL (Entra-only), not for Static Web
Apps (token read at run time). Resource names are not variables either — they come from the
infra outputs.

### What the deploying principal needs

Owner-equivalent on the subscription (resource group + role assignments at subscription scope),
`AcrPush` on the registry, membership of the SQL Entra admin group for the dacpac deploy, and —
only for a day-0 run with `create-model-deployments=true` — the Marketplace permissions from
[#107](https://github.com/kolatts/foundry-gate/issues/107). Exact commands:
[Owner Setup Runbook](/foundry-gate/reference/owner-setup/).

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
