# Session handoff — 2026-09-02 state, owner actions, live-validation order

Everything below is the complete state for whoever (human or agent) picks this up. The
decision trail lives in `fable-refactor-log.md` (D-001–D-021, E-001–E-010); engineering
contract in `CONVENTIONS.md`; agent rules in `CLAUDE.md`. This supersedes the 2026-09-01
version of this file; issue #101 is the running orchestrator log (11 comments) and stays
open as the handoff pointer — it is not closed by this PR.

## What is on main now

**Solution** (`FoundryGate.sln`, 11 projects under `src/`): `FoundryGate.Domain` (zero
deps), `FoundryGate.Data` (EF Core + entities + seeding), `FoundryGate.Core` (services
shared by Api and Functions — no ASP.NET Core dependency, D-014), `FoundryGate.Api`,
`FoundryGate.Functions` (isolated worker), `FoundryGate.Web` (Blazor WASM, Domain-only
reference), `FoundryGate.Database` (.sqlproj → dacpac), `FoundryGate.Cli`
(`db deploy/seed/compare`, `ip setup/cleanup`, `db grant-identities`, local setup),
`FoundryGate.Tests.Predeployment`, `FoundryGate.Tests.Postdeployment`. Build is
zero-warning (`TreatWarningsAsErrors`); **`dotnet test src/FoundryGate.Tests.Predeployment
-c Release` → 1545/1545 passing** (verified this session, up from ~1010 at the last
orchestrator log entry).

**API surface** (`src/FoundryGate.Api/Controllers`, all under `/api/v1`, Entra JWT +
`FoundryGate.Admin` app role): `AuditController`, `ConfigController`, `DashboardController`,
`FoundryController` (ARM deployment CRUD, create-once safety), `GroupsController` (CRUD +
Entra group sync), `KeysController` (provision/rotate/reveal/revoke, Key Vault wrapping),
`QuotaController` (tiers, allocations, increase requests), `RequestsController`,
`UsersController` (lifecycle, Entra bulk sync, `/users/me` self-provisioning).

**Functions** (`src/FoundryGate.Functions`, isolated worker, timer-triggered):
`EntraSyncFunction` (daily 02:00 UTC — users then groups, blob-lease locked,
`Entra:Enabled` gated), `MonthlyQuotaResetFunction` (daily 00:01 UTC, honours
`ResetDayOfMonth`, idempotent via audit-row check — D-015/D-018),
`UsageSyncFunction` (every 15 min, reconciles `ApiManagementGatewayLlmLog` into
`QuotaAllocation.TokensUsed`, writes `usage.synced` only when something changed — D-016).

**Blazor pages** (`src/FoundryGate.Web/Pages`): developer `Me`/`MeRequest`; admin
`Dashboard`, `Users`/`UserDetail`, `Groups`/`GroupDetail`/`GroupNew`, `Requests`/
`RequestDetail`, `QuotaAllocations`, `Foundry`, `UsersSync`, `Config`, `Audit`; shell
`Authentication`, `Home`, `PageNotFound`. MSAL sign-in, MudBlazor theme, typed API client,
bUnit-tested.

**Infra** (`infra/`, subscription-scope `main.bicep`, 16 modules under `infra/modules/`):
gateway data plane (`ai-gateway.bicep`, `apim.bicep`, `foundry.bicep`, `foundry-rbac.bicep`
— live-validated 2026-09-01 on Imagile Paid, then torn down) plus control plane
(`control-plane.bicep`, `sql.bicep`, `container-app.bicep`, `container-registry.bicep`,
`function-app.bicep`, `key-vault.bicep`, `static-web-app.bicep`, `storage-account.bicep`,
`managed-identities.bicep`, `control-plane-rbac.bicep`, `swa-preview-role.bicep` — a
custom RBAC role, since no built-in Azure role grants any `Microsoft.Web/staticSites`
action). `createModelDeployments=false` on all re-runs; Anthropic deployments are
create-once (E-007) — never re-PUT, never delete/recreate in a loop.

**CI/CD**: single `deploy-all.yml` chain on push to main — infra → dacpac db deploy
(runner IP whitelist → firewall wait → deploy → seed → grant identities → remove
firewall rule) → api/functions/ui → postdeployment tests → summary, OIDC only (no
credential JSON). GitHub Environments: `dev`, `dev-plan` (Reader-only, PR what-if),
`ui-preview` (SWA preview publish, custom role), `production` (1 reviewer),
`dev-destroy` (reviewer + 5 min), `prod-destroy` (reviewer + 30 min, needs a **second**
reviewer — only one collaborator exists today), `github-pages`. A production
`deploy-all.yml` run asks for **six** approvals, one per gated job — D-019. Repo
variables today: `CLAUDE_AUTOMATION_ENABLED=false`, `IMAGILE_BOT_APP_ID=4428945` — the
imagile-bot triage/review automation (`claude-triage.yml`/`claude-review.yml`) is still
present but inert; secrets (`IMAGILE_BOT_PRIVATE_KEY`, `CLAUDE_CODE_OAUTH_TOKEN`) were
never added this pass, so merges have stayed `--admin` after a posted human/agent review
the whole way through.

**Docs site** (`docs-site/`, Astro/Starlight): landing page, progressive how-it-works
(`getting-started/why-foundrygate` → `architecture/overview` → `architecture/feasibility`
+ `reference/*`), architecture diagrams (system, deploy pipeline, gateway-centric
quota/key lifecycle), light theme + mobile-reflowed diagrams (PR #200). Doc invariant
check performed this session: `architecture/overview.mdx` and
`docs-site/src/components/DeployPipeline.astro` still correctly say
**"exists · unvalidated · #105"** on every pipeline stage — accurate, no change needed;
the badge stays until #105 closes.

**All product epics are closed.** The only open epic is **#81** (APIM GenAI gateway),
which stays open because Claude end-to-end is still unvalidated (#88) and its dependent
live-validation issues (#125, #132, #178) are open.

## Nothing has been deployed live yet

No `dev` or `production` environment has ever been stood up from this control-plane
Bicep. The gateway data plane was live-validated once (2026-09-01, Imagile Paid) and
**torn down** immediately after — `rg-foundrygate-test` deleted, soft-deleted resources
purged. Every "live-validate X" issue below describes work that literally cannot be
attempted until a real `dev` deploy exists.

The first `Deploy All` run on main **stops at the OIDC guard by design**: `dev`'s
`AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID` variables are unset, so
`Infra - Deploy (dev)` fails loudly with `GitHub Environment 'dev' is missing the
variable(s): ...` and every downstream stage is skipped. Confirmed run:
https://github.com/kolatts/foundry-gate/actions/runs/33596580833. This is the intended
failure mode, not a bug — every push to main touching a deployable path will produce
this red run until the owner actions below are done.

## Owner actions, in order

> **Status 2026-09-05 — dev is done.** Everything in steps 1–5 below was executed for
> `dev` from a CLI session signed in as the owner (`az` + `gh`), not by hand in the
> portal, and the exact commands are the runbook in
> `docs-site/src/content/docs/reference/owner-setup.md`. An agent with an owner-signed-in
> `az`/`gh` CLI *can* do all of it — the earlier "cannot be done by an agent" wording was
> about privileges, not tooling. Production identities are deliberately **not** created
> yet (#109). The identifiers created for dev:
>
> | Thing | Name | Client / object id |
> |---|---|---|
> | API app registration | `FoundryGate.Api (dev)` | `7e7d0561-0973-411d-ba62-a667cbfec1d9` |
> | SPA app registration | `FoundryGate.Web (dev)` | `21b82312-f9f9-4be6-b243-34bf7256b557` |
> | CI deploy identity (Owner) | `foundrygate-ci-dev` | `88f05620-03f2-408e-810f-0e25f668b6a7` |
> | PR what-if identity (Reader) | `foundrygate-ci-dev-plan` | `ec8f8758-43f7-4a5c-9ad4-70d71807ec76` |
> | SWA preview identity | `foundrygate-ci-ui-preview` | `073418b1-a73f-4997-8805-5f3a9ec0fbda` |
> | SQL admin group | `SG_FOUNDRYGATE_SQL_ADMINS` | `186dafe0-e7af-4bc8-940d-cac5314ffe82` |

The steps below stay as the reference shape (and are what a **production** environment
still needs). Full reference:
`docs-site/src/content/docs/reference/owner-setup.md`,
`docs-site/src/content/docs/reference/infrastructure.md` and
`docs-site/src/content/docs/reference/ci-cd.md`.

### 1. Entra app registrations (issue #109, item 1–2)

- **`FoundryGate.Api`** app registration: expose `api://{clientId}/access_as_user`, app
  role `FoundryGate.Admin`. Set GitHub Environment variable `FG_ENTRA_API_CLIENT_ID` on
  `dev` and `production` — `infra/parameters/{dev,prod}.bicepparam` read it with
  `readEnvironmentVariable('FG_ENTRA_API_CLIENT_ID', '<zero guid>')` (zero GUID lets a
  bootstrap deploy succeed but rejects every token until the real id is set).
- **`FoundryGate.Web`** SPA app registration with the SWA hostname
  (`stapp-foundrygate-{env}.<n>.azurestaticapps.net`, output `staticWebAppHostname`) as
  redirect URI. Set `FG_ENTRA_WEB_CLIENT_ID`; update
  `src/FoundryGate.Web/wwwroot/appsettings.json` placeholders.

### 2. Dedicated SQL Entra admin group (#109 item 3–4)

- Create `SG_FOUNDRYGATE_SQL_ADMINS` (dev) and a separate production group. Dev currently
  falls back to `SG_IMAGILE_SQL_ADMINS` (`2ed4d6b7-575c-4046-aeb0-eb51bc254ef5`);
  `prod.bicepparam` has **no default** and fails loudly (`FG_SQL_ADMIN_GROUP_OBJECT_ID`,
  `FG_SQL_ADMIN_GROUP_NAME` required, no fallback) until the production group exists.
- Add the CI OIDC app registration (step 3 below) to that group — Azure SQL is
  Entra-only, no password fallback, `_deploy-database.yml` connects with
  `Authentication=Active Directory Default`.

### 3. CI OIDC app registrations + federated credentials (#109 update, four+ Environments)

One app registration per Environment (or one shared, differing only in variables). Each
needs a federated credential:

```bash
az ad app federated-credential create --id <appObjectId> --parameters '{
  "name": "foundrygate-<env>",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:kolatts/foundry-gate:environment:<env>",
  "audiences": ["api://AzureADTokenExchange"]
}'
```
for `<env>` in `dev`, `production`, `dev-destroy`, `prod-destroy` — **plus two more,
narrower identities added mid-pass**:

- **`dev-plan`** (PR what-if only): a **separate, Reader-only** app registration.
  Subject `repo:kolatts/foundry-gate:environment:dev-plan`. RBAC: **Reader** on the
  subscription, nothing else. Variables: `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/
  `AZURE_SUBSCRIPTION_ID` (the Reader app), optionally `AZURE_LOCATION`.
- **`ui-preview`** (SWA PR previews, #155): a **third** app registration, preview-only.
  Subject `repo:kolatts/foundry-gate:environment:ui-preview`. RBAC is a **custom role**
  (no built-in role covers `Microsoft.Web/staticSites` actions without `Contributor`'s
  full blast radius) — `infra/modules/swa-preview-role.bicep` creates
  `FoundryGate SWA Preview Publisher ({env})` scoped to exactly one Static Web App.
  Grant it **after** an infra deploy has created the role:
  ```bash
  ROLE_ID=$(az deployment sub show -n foundrygate-dev \
    --query properties.outputs.swaPreviewRoleDefinitionId.value -o tsv)
  SCOPE=$(az deployment sub show -n foundrygate-dev \
    --query properties.outputs.swaPreviewRoleAssignableScope.value -o tsv)
  az role assignment create --assignee <ui-preview app clientId> --role "$ROLE_ID" --scope "$SCOPE"
  ```
  Variables: `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID` (the preview
  app), `FG_STATIC_WEB_APP_NAME` (`stapp-foundrygate-dev`), `FG_RESOURCE_GROUP`
  (`rg-foundrygate-dev`), `FG_API_BASE_URL` (dev Container App FQDN +
  `/api/v1/` — cannot be read from deployment outputs by design),
  `FG_ENTRA_WEB_CLIENT_ID`/`FG_ENTRA_API_CLIENT_ID` (same values as `dev`). No required
  reviewers or branch policy on `dev-plan` or `ui-preview` — a policy on either defeats
  the PR-track jobs they exist for.

Neither `dev-plan` nor `ui-preview` should be the deploy identity — both are read-only
or single-resource by design; leaking either does not compromise a deploy.

### 4. Azure RBAC for the main deploy identity (#109 update, section B)

- **Subscription: Owner** (or Contributor + User Access Administrator) — `main.bicep` is
  subscription-scope and writes role assignments.
- **AcrPush** on `crfoundrygate{env}{suffix}` (covered by Owner; listed for a narrower
  fork).
- **Member of the SQL Entra admin group** from step 2.
- Marketplace/SaaS permissions for day-0 Claude deployments (#107) — separate item.

### 5. GitHub Environment variables (#109 update, section C)

`AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID` required on `dev`,
`production`, `dev-destroy`, `prod-destroy`; `AZURE_LOCATION` optional (default
`eastus2`) on `dev`/`production`. `FG_ENTRA_API_CLIENT_ID`/`FG_ENTRA_WEB_CLIENT_ID`
recommended on `dev`/`production`. `FG_SQL_ADMIN_GROUP_OBJECT_ID`/
`FG_SQL_ADMIN_GROUP_NAME` **required** on `production` only. `RESOURCE_GROUP`,
`SQL_SERVER_NAME`, etc. are **not needed** — every workflow resolves them from
`az deployment sub show -n foundrygate-{dev|prod} --query properties.outputs`;
`FG_API_IMAGE` is computed by `.github/scripts/infra/resolve-api-image.sh`, never set by
hand.

### 6. Functions identity needs the same Graph app roles as the API identity (#109, latest comment)

`EntraSyncFunction` (#151, merged in PR #216) calls Microsoft Graph as **its own**
managed identity, `id-foundrygate-func-{env}` — granting the roles only to the API
identity gives a nightly job that fails every run with `Authorization_RequestDenied`.
Grant to **both** `id-foundrygate-api-{env}` and `id-foundrygate-func-{env}`
(Application-type app-role assignments on the Graph service principal
`00000003-0000-0000-c000-000000000000` — `az ad app permission` does not apply to
managed identities):

```powershell
Connect-MgGraph -Scopes AppRoleAssignment.ReadWrite.All, Application.Read.All
$graphSp = Get-MgServicePrincipal -Filter "appId eq '00000003-0000-0000-c000-000000000000'"
foreach ($mi in 'id-foundrygate-api-dev', 'id-foundrygate-func-dev') {
  $sp = Get-MgServicePrincipal -Filter "displayName eq '$mi'"
  foreach ($role in 'Application.Read.All','User.Read.All','GroupMember.ReadBasic.All') {
    $appRole = $graphSp.AppRoles | Where-Object { $_.Value -eq $role -and $_.AllowedMemberTypes -contains 'Application' }
    New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $sp.Id -PrincipalId $sp.Id -ResourceId $graphSp.Id -AppRoleId $appRole.Id
  }
}
```
`Entra__Enabled` stays deliberately unset by Bicep — turning the feature on is the owner
step that follows these grants, on both hosts.

### 7. Day 0 deploy

`Actions → Deploy All → Run workflow` with `environment: dev` and
**`create-model-deployments = true`** — once. Every later run leaves it `false`
(Anthropic deployments are create-once). The first API deploy replaces the bootstrap
placeholder image (`mcr.microsoft.com/k8se/quickstart:latest`) automatically on the next
infra re-run once `FG_API_IMAGE` resolves. This is issue **#105**.

### 8. Marketplace/SaaS permissions + Claude retry (#107, #88)

Runtime Claude deployment creation needs Marketplace/SaaS permissions beyond Cognitive
Services Contributor for the deploy identity (#107 — exact scope not yet nailed down,
needs an Azure support conversation). Separately, issue #88: the Imagile Paid
subscription's Anthropic Marketplace agreement wedged 2026-09-01 after a
delete/re-create cycle (E-007) — every Claude create after the first delete failed,
including fresh accounts. Retry a single, careful `claude-haiku-4-5` create ≥24h after
the incident, or open an Azure support ticket referencing the `InternalServerError`
tracking IDs in `fable-refactor-log.md` E-007.

### 9. Second reviewer on `prod-destroy`

#68 asked for 2+ required reviewers; only one collaborator exists today. Add a second
person/team under Settings → Environments → `prod-destroy` when one exists.

## Live-validation checklist — run in this order after the first `dev` deploy

Every item below is fully covered by hermetic tests with fakes (ARM, Graph, Key Vault,
Log Analytics); none has ever touched a live resource. Each issue has its own detailed
manual checklist — this is just the dependency order.

1. **#105** — control-plane Bicep deploy itself (Container App image/env vars/health
   probes, Function App storage, RBAC assignments, SQL Entra-only auth, SWA hostname,
   diagnostics, no-op re-run).
2. **#142** — CLI `ip setup`/`ip cleanup`/`db grant-identities` against the real Azure
   SQL server (contained users `WITH SID`, no Directory Readers needed — decided in the
   #143 review).
3. **#132** — APIM key lifecycle (provision/rotate/reveal/revoke) + Key Vault RSA-3072
   key wrapping against real APIM + Key Vault.
4. **#120** — Entra user sync against a real tenant (Graph app roles from owner step 6
   above; departure detection suspended while group-principal app-role assignments
   exist — #121).
5. **#125 / #205** — `/foundry` deployment endpoints against a real Foundry account
   (OpenAI create/409/delete, multi-account list; Claude creation is refused by design
   pending #107); #205 additionally live-validates the capacity PATCH before allowing it
   for Anthropic deployments.
6. **#178** — reconciliation KQL (`UsageBySubscription.kql`) and both Function timers
   (`UsageSyncFunction`, `MonthlyQuotaResetFunction`) against the deployed gateway and
   Log Analytics workspace — includes verifying the `max()`-before-sum de-duplication
   assumption (D-017) against real `ApiManagementGatewayLlmLog` data.
7. **#192** — admin UI pages against a deployed environment and a real Entra tenant
   (role-claim shape, routing gates bUnit's in-memory fakes cannot prove).
8. **#102** — E2E 401/403 assertions on `/api/v1` (needs real Entra tokens;
   `WebApplicationFactory` cannot mint them).
9. **#88** — Claude end-to-end: Messages through the gateway, TPM on the Anthropic
   schema, pool failover, prompt-cache token counting, a full Claude Code CLI session.
   Gated on owner action 8 above (Marketplace permissions + the retry/support ticket).

## Owner decisions pending

- **#122** — should `accountEnabled=false` count as departed for Entra sync purposes?
  Spec says "departed" = absent from the directory result, but a disabled account can
  sit for ~30 days before hard-delete, during which the user keeps a working APIM
  subscription under the current rule. Three options in the issue; option 1 (treat
  disabled as departed) matches "access follows the org chart" and costs one extra
  `$select` field.
- **#150** — should a group's Entra link be changeable after creation (link/unlink/
  re-point)? Today `UpdateGroupRequest` deliberately omits `EntraGroupId`; re-pointing a
  synced group silently rewrites its whole roster on the next sync. Options: allow it
  behind a preview-then-confirm endpoint, or keep it immutable and document the rule
  (plus decide the unlink case separately, since unlinking isn't destructive).
- **#138** — SWA PR-preview hostnames need SPA redirect URIs for MSAL sign-in to work on
  previews (Entra disallows wildcard SPA redirect URIs). Three options: a fixed set of
  preview redirect URIs, have the preview job register/deregister the hostname via Graph
  (needs `Application.ReadWrite.OwnedBy` on the CI principal), or document previews as
  UI-shell-only with no working auth. Not yet decided or implemented.

## Deferred engineering backlog

- **#214** — converge the two deprovision pipelines (Api's `IUserLifecycleService` and
  Core's `DeprovisioningDepartureHandler`, D-020) by lifting plan 21's departure sequence
  into Core once the key/quota-request services it composes can live there too.
- **#213** — store per-model prompt/completion token split so cost estimates stop being
  blended across models.
- **#212** — automate the API base-image digest refresh (Renovate/Dependabot against
  MCR) instead of a manually pinned digest.
- **#205** — (also live-validation, see above) allow the capacity PATCH for Anthropic
  deployments once live-validated.
- **#196** — enable Container Apps environment zone redundancy once private networking
  lands (zone redundancy adds ~60% to the SQL compute meter today with no network
  isolation to match it).
- **#185** — prune `SystemConfigurationKeys.Retired` once every fork has re-seeded past
  them.
- **#183** — verify Graph `$select`/`$top` on `/transitiveMembers` works without
  `ConsistencyLevel: eventual` (the client deliberately avoids the advanced-query OData
  cast; needs a live tenant with a large/nested group to confirm the pagination shape).
- **#179** — first login: shorten the lock window instead of only absorbing its
  conflict (the #184 fix absorbed the race; a shorter window is the deferred
  improvement).
- **#139** — make postdeployment tests gating in `deploy-all.yml` once real Playwright
  scenarios exist (today's is a scaffold smoke test, reporting-only).
- **#126** — Claude deployment creation via `POST /foundry/deployments` is blocked on
  two things: the ARM SDK (`Azure.ResourceManager.CognitiveServices` 1.5.2 and
  1.6.0-beta.4) cannot send `modelProviderData`, and #107's permissions gap.

Any issue opened after this handoff that doesn't fit the categories above gets filed and
referenced per `CLAUDE.md`'s everything-is-an-issue rule — check `gh issue list --state
open` for the current total rather than trusting a stale count here.

## Key rulings (decision log — `fable-refactor-log.md`)

- **D-004** — scope of "fully deployed app": author + deploy + validate the gateway data
  plane; the 23-epic .NET control plane stays issue-tracked (later fully built out this
  pass, see below).
- **D-011** — implementation execution model: one issue-set per subagent in an isolated
  worktree, verifiable gates, PR + review before merge; Azure SQL + EF Core single-tenant
  (no imagile-app sharding); fully automated deploy pipeline; architecture diagrams are
  first-class public docs.
- **D-012** — Blazor stays (re-confirmed after an owner challenge mid-pass) over React:
  compile-time DTO sharing via Domain, single .NET stack.
- **D-013** — a developer's monthly budget **is** a gateway tier: numeric quotas must
  equal a configured tier cap or be unlimited (APIM `token-quota` rejects policy
  expressions, #82's PoC).
- **D-014** — `FoundryGate.Core`: a class library, no ASP.NET Core dependency, for
  services more than one host (Api + Functions) needs; Api-only services stay in Api.
  Trap recorded: `ValidateRecursively()` only recurses into the *root object's own
  assembly*, so a Core-owned options section needs an explicit `IValidatableObject` hop
  per host.
- **D-015** — the monthly reset honours `ResetDayOfMonth` by waking daily rather than
  deriving a cron from a runtime-changeable value.
- **D-016** — the usage sync writes an audit row only when something meaningful changed
  (a value moved, or the *shape* of a problem differs from the last recorded pass) — not
  every 15-minute tick.
- **D-017** — the reconciliation KQL de-duplicates `ApiManagementGatewayLlmLog` with
  `max()` per `CorrelationId` before summing across subscriptions (chunked/duplicate
  entries would otherwise multiply usage); #178 verifies the assumption live.
- **D-018** — a missed reset day is not a missed month: the gate is "today ≥ configured
  day AND not yet reset this period" (existence of the period's own audit row), not an
  exact-day match.
- **D-019** — production keeps **six** separate Environment approvals, one per gated job
  in the actual job graph (not the stage diagram — corrected in the #198 review after an
  earlier undercount). Collapsing to one gate was rejected: OIDC mints per-job tokens
  scoped to the Environment named in the job, so a single upstream approval cannot be
  made to protect every downstream job without either removing the gate or passing a
  bearer credential between jobs.
- **D-020** — the nightly Entra sync's departure handling is a **second, real
  implementation** (`DeprovisioningDepartureHandler` in Core) of plan 21's deprovision
  sequence, not a flag-only stand-in — because a scheduled job that only marks someone
  inactive while their gateway key still works is worse than the duplication, which is
  bounded, tested against the same assertions on both sides (`DepartureHandlerParityTests`),
  and tracked for convergence as #214.
- **D-021** — see below (this pass).

## Working model that produced this (keep it)

One issue-set per agent (Sonnet or Opus, model chosen per issue complexity) in an
isolated worktree → verifiable gates (zero-warning build, `FoundryGate.Tests.Predeployment`
green, `dotnet format --verify-no-changes` clean, plan-file Verification items checked) →
PR with `Closes #N` in the body → a separate reviewer posts a Major/Minor/Nit review
(≥70-confidence threshold; consolidated single pass) → the same agent does a fix pass +
point-by-point response → `gh pr merge --admin` (branch protection needs one review no
second identity can give until bot secrets land, so admin merge is the standing exception,
not a shortcut) → orchestrator closes epics when every child issue is closed. Everything
discovered mid-work becomes a GitHub issue immediately, never an inline TODO. Commit early
inside each worktree. Rate-limit interruptions (hit twice this pass) are resumed via
`SendMessage` to the same agent, or — if that agent is gone — a fresh agent relaunched
with an explicit model override and the same issue set; either way the PR/review/fix-pass
contract above still applies unchanged.
