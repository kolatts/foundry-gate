---
title: Infrastructure Reference
description: Every Azure resource FoundryGate's Bicep creates, how it is named, the parameters that shape it, the role assignments between them, and the outputs the deploy pipeline consumes.
---

Everything FoundryGate needs in Azure is one subscription-scope Bicep template,
`infra/main.bicep`, deployed with `az deployment sub create`. It has two halves:

- **Gateway data plane** — API Management v2, the Azure AI Foundry accounts and their model
  deployments, the backend pools, quota-tier products and policies, Log Analytics and
  Application Insights. Always deployed. Documented in
  [Architecture → Feasibility](/foundry-gate/architecture/feasibility/) and the policy files
  under `infra/policies/`.
- **Control plane** — the FoundryGate app that manages the gateway: the API on Container
  Apps, the Blazor UI on Static Web Apps, background jobs on Functions Flex Consumption,
  Azure SQL, Key Vault, a container registry, two managed identities and the role
  assignments between them. Deployed only when `deployControlPlane = true`
  (`dev.bicepparam` and `prod.bicepparam` set it; `test.bicepparam` is gateway-only).

Every resource lands in one resource group per environment, `rg-foundrygate-{env}`, and
carries the tags `workload=foundrygate`, `environment`, `managed-by=bicep`, `repo`, an
`fg-role` (`gateway` | `foundry` | `monitoring` | `control-plane`) and, on control-plane
resources, an `fg-component` (`api` | `sql` | `keyvault` | `registry` | `functions` |
`storage` | `ui`). Cost and inventory queries filter on those.

## Deploying

```bash
# Gateway only (what the team validated live first):
az deployment sub create --location eastus2 \
  --template-file infra/main.bicep --parameters infra/parameters/test.bicepparam

# Full stack, dev:
az deployment sub create --location eastus2 --name foundrygate-dev \
  --template-file infra/main.bicep --parameters infra/parameters/dev.bicepparam

# Preview any change without touching anything:
az deployment sub what-if --location eastus2 \
  --template-file infra/main.bicep --parameters infra/parameters/dev.bicepparam
```

Two invariants for re-runs:

1. **`createModelDeployments = false` after day 0.** Anthropic (Claude) model deployments
   are create-once under ARM — re-PUTing an existing one drives it to `Failed`. The
   parameter files ship with `false`; override to `true` on the command line for the very
   first deployment of a new environment only. Model lifecycle after that belongs to the
   control plane, not Bicep.
2. **Pass the current API image — always.** The Container App's image is a parameter
   (`apiContainerImage`), and the param files read it from the `FG_API_IMAGE` environment
   variable **with no default**, so `build-params` fails loudly rather than silently
   swapping the API for a placeholder page. On the bootstrap run the registry is created by
   the same deployment and holds nothing yet, so pass the public placeholder explicitly:
   `FG_API_IMAGE=mcr.microsoft.com/k8se/quickstart:latest`. The Container App module
   recognises it and switches to **port 80 with `/health`-only probes** (that image listens
   on :80 and serves only `/` and `/health` — verified under docker). Every later infra run
   sets `FG_API_IMAGE` to the tag currently running (the api-deploy workflow's
   `az containerapp update --image` is what moves it forward):

   ```bash
   FG_API_IMAGE=$(az containerapp show -n ca-foundrygate-api-dev -g rg-foundrygate-dev \
     --query properties.template.containers[0].image -o tsv)
   ```

   The `containerAppIsBootstrapImage` output reports which mode the app is in.

Prod additionally requires `FG_SQL_ADMIN_GROUP_OBJECT_ID` and `FG_SQL_ADMIN_GROUP_NAME`
(a dedicated production SQL admin group — with Entra-only auth, group membership *is* the
access model, so prod deliberately fails to build until that group exists rather than
pointing at the dev group; see [#109](https://github.com/kolatts/foundry-gate/issues/109)).

## Naming convention

`{env}` is `environmentName` (`test`, `dev`, `prod`); `{suffix}` is `nameSuffix`, needed
only where the name is a global DNS label. The CLI `ip setup` command and the deploy
workflows resolve resources from these patterns — changing one is a contract change.

| Resource | Name | Notes |
|---|---|---|
| Resource group | `rg-foundrygate-{env}` | |
| Log Analytics workspace | `log-foundrygate-{env}` | shared by gateway and control plane |
| Application Insights | `appi-foundrygate-{env}` | |
| API Management | `apim-foundrygate-{env}-{suffix}` | v2 tier |
| Foundry account | `fg{env}-{suffix}-{region}` | one per `foundryRegions` entry, e.g. `fgdev-e7k2-eus2` |
| Azure SQL server | `sql-foundrygate-{env}-{suffix}` | FQDN `<name>.database.windows.net` |
| Azure SQL database | `sqldb-foundrygate-{env}` | |
| Key Vault | `kv-fg-{env}-{suffix}` | 24-char limit forces the short form |
| Container registry | `crfoundrygate{env}{suffix}` | alphanumeric only |
| Container Apps environment | `cae-foundrygate-{env}` | |
| API container app | `ca-foundrygate-api-{env}` | |
| API identity | `id-foundrygate-api-{env}` | user-assigned |
| Functions identity | `id-foundrygate-func-{env}` | user-assigned |
| Function App | `func-foundrygate-{env}-{suffix}` | Flex Consumption |
| Functions plan | `asp-foundrygate-func-{env}` | `FC1` |
| Functions storage | `stfg{env}{suffix}` | 3–24 lowercase alphanumeric; shared-key access off |
| Static Web App | `stapp-foundrygate-{env}` | |

## Modules

| Module | Creates |
|---|---|
| `monitoring.bicep` | Log Analytics (62-day retention — two full quota months) + App Insights |
| `foundry.bicep` | one AIServices account + chained model deployments; keys disabled |
| `foundry-rbac.bicep` | APIM identity → Cognitive Services User on each account |
| `apim.bicep` | APIM v2, App Insights logger, `GatewayLlmLogs` diagnostic |
| `ai-gateway.bicep` | backends, priority pool + breakers, front-door APIs, tier products, alias named values |
| `control-plane.bicep` | orchestrates everything below; owns the control-plane naming |
| `managed-identities.bicep` | the two user-assigned identities |
| `key-vault.bicep` | RBAC-mode vault, soft delete, optional purge protection, optional `fg-apim-key-encryption` RSA-3072 key |
| `sql.bicep` | Entra-only SQL server, `AllowAllWindowsAzureIps` firewall rule, database (zone-redundant in prod) |
| `container-registry.bicep` | ACR (Basic dev / Standard prod), admin user disabled |
| `storage-account.bicep` | Functions storage (LRS dev / ZRS prod), shared keys off, `function-deployments` container |
| `static-web-app.bicep` | SWA (Free/Standard), `provider: Custom` |
| `control-plane-rbac.bicep` | all role assignments below |
| `container-app.bicep` | Container Apps environment (logs via diagnostic setting — no workspace key) + the API app; bootstrap-image detection (port 80, `/health`-only probes) |
| `function-app.bicep` | Flex Consumption plan + Function App, identity-based storage |

## Control-plane parameters

| Parameter | dev | prod | Purpose |
|---|---|---|---|
| `deployControlPlane` | `true` | `true` | default `false` (gateway only) |
| `appEnvironment` | `qa` | `prod` | `ASPNETCORE_ENVIRONMENT` — lowercase `qa`/`prod`; `local` is docker-only |
| `sqlAdminGroupObjectId` / `sqlAdminGroupName` | `SG_FOUNDRYGATE_SQL_ADMINS` | `$FG_SQL_ADMIN_GROUP_OBJECT_ID` / `$FG_SQL_ADMIN_GROUP_NAME` (required) | SQL server administrator (Entra-only auth; no SQL login exists) |
| `sqlLocation` | `centralus` | set it explicitly | region for the SQL logical server, defaulting to `location`. Dev overrides it because **`eastus2` and `eastus` are both closed to new Azure SQL servers** on this subscription — see below |
| `sqlDatabaseSku` | `GP_S_Gen5` ×1 — serverless, 60-min auto-pause | `GP_Gen5_2`, provisioned | serverless is derived from the SKU name (`GP_S_*`) |
| `sqlBackupStorageRedundancy` | `Local` | `Geo` | |
| `sqlZoneRedundant` | `false` | `true` | survives the loss of one availability zone without a restore; adds ~60% to the SQL compute meter (see [Cost & capacity](/foundry-gate/reference/cost-and-capacity/)) |
| `entraTenantId` | tenant | tenant | `AzureAd__TenantId` |
| `entraApiClientId` | `$FG_ENTRA_API_CLIENT_ID` | same | `AzureAd__ClientId`; zero GUID until the app registration exists |
| `entraApiAudience` | `api://{clientId}` | same | `AzureAd__Audience` |
| `apiContainerImage` | `$FG_API_IMAGE` (required) | same | see re-run invariant 2 |
| `containerAppMinReplicas` / `MaxReplicas` | 1 / 2 | 1 / 3 | min 1 — the admin API is the UI's only backend |
| `containerAppCpu` / `containerAppMemory` | `0.25` / `0.5Gi` | `0.5` / `1.0Gi` | only the Consumption profile pairs are valid (0.25/0.5Gi, 0.5/1.0Gi, 0.75/1.5Gi, 1.0/2.0Gi, …) |
| `containerAppsZoneRedundant` | `false` | `false` | plumbed but not enabled: ARM rejects it without a VNet-integrated environment, so it waits on private networking ([#196](https://github.com/kolatts/foundry-gate/issues/196)) |
| `functionsStorageSku` | `Standard_LRS` | `Standard_ZRS` | Functions host state + the Flex deployment container |
| `containerRegistrySku` | `Basic` | `Standard` | 100 GB included storage and higher throughput vs Basic's 10 GB; Premium only adds geo-replication and private link |
| `staticWebAppSku` / `staticWebAppLocation` | `Free` / `eastus2` | `Standard` / `eastus2` | SWA is only offered in a handful of regions |
| `functionsRuntimeVersion` | `10.0` | `10.0` | .NET isolated worker |
| `keyVaultPurgeProtection` | `false` | `true` | irreversible once on |
| `keyVaultSoftDeleteRetentionInDays` | 7 | 90 | |
| `createKeyEncryptionKey` | `true` | `true` | RSA key for wrapping APIM subscription keys at rest |

Nothing in a parameter file is a secret: SQL is Entra-only, storage is identity-based,
registry pulls are identity-based, and the App Insights connection string is a module
output. Entra object ids and client ids are identifiers, not credentials.

### Why `sqlLocation` exists

Azure closes individual regions to **new** Azure SQL logical servers as a capacity action,
and there is no way to see it coming: it is not a quota you can read, not a SKU
availability query, not anything `az sql server list-usages` reports. The only signal is
attempting a create and getting `ProvisioningDisabled` / `RegionDoesNotAllowProvisioning`.

Probed on the dev subscription, 2026-09-05:

| Region | New SQL server |
|---|---|
| `eastus2` | refused |
| `eastus` | refused |
| `centralus` | created |

Both the primary region and its obvious neighbour were shut, which is why dev's SQL lives
in `centralus` while everything else stays in `eastus2`. Existing servers in a closed
region keep working — only creation is blocked, and it can reopen (or close) without
notice. The cross-region hop between the Container App and SQL is a real latency cost,
accepted for dev.

**Set `sqlLocation` explicitly for production** rather than letting it inherit `location`,
so a prod day-0 does not discover this the way dev did
([#241](https://github.com/kolatts/foundry-gate/issues/241)). The alternative is a support
request (Issue type *Service and subscription limits*) to reopen the primary region — worth
it when co-location matters, not worth blocking a first deploy on.

## Role assignments

All scoped to the individual resource, never the resource group; names are
`guid(scope, principal, role)` so re-runs are idempotent.

| Identity | Role | Scope | Why |
|---|---|---|---|
| API | Key Vault Secrets User | Key Vault | `@KeyVault()` reference resolution at startup |
| API | Key Vault Crypto User | Key Vault | wrap/unwrap APIM subscription keys with `fg-apim-key-encryption` |
| API | AcrPull | registry | pull the API image |
| API | API Management Service Contributor | APIM instance | create/rotate/delete developer subscriptions, move between tier products, edit `fg-model-map-*` named values |
| API | Cognitive Services Contributor | each Foundry account | model deployment lifecycle |
| API | Log Analytics Reader | workspace | usage queries for the admin dashboard |
| Functions | Key Vault Secrets User | Key Vault | |
| Functions | Log Analytics Reader | workspace | reconciliation reads `ApiManagementGatewayLlmLog` |
| Functions | API Management Service Contributor | APIM instance | re-scope a developer's subscription when the monthly reset resolves a new tier — see the note below |
| Functions | Storage Blob Data Owner | storage | host state + Flex deployment container + the scheduled jobs' lock leases (`foundrygate-locks`: one blob per job) |
| Functions | Microsoft Graph `Application.Read.All`, `User.Read.All`, `GroupMember.ReadBasic.All` | Graph service principal | the nightly directory sync ([#151](https://github.com/kolatts/foundry-gate/issues/151)) — **not Bicep-able**, see [Graph application roles](#role-assignments) below |
| Functions | Storage Queue Data Contributor | storage | queue triggers/outputs |
| Functions | Storage Table Data Contributor | storage | table bindings |
| APIM (system-assigned) | Cognitive Services User | each Foundry account | gateway → model backends, no account keys |

### Why the Functions identity can write to APIM

Quota tiers are APIM products, so "this developer's monthly budget changed" means "move their
subscription to another product". Almost every such move happens in the API, on somebody's request —
including a change to the system-wide `DefaultMonthlyTokenQuota`, which since
[#204](https://github.com/kolatts/foundry-gate/issues/204) re-resolves its default-tier users in the
same transaction as the edit. What is left over lands on the scheduled monthly reset: a developer
with no earlier allocation has no known previous tier, and anything a re-resolution missed surfaces
there first. Until [#194](https://github.com/kolatts/foundry-gate/issues/194) the Functions host
could only log a warning that the database and the gateway had diverged, and they stayed diverged
until the API next touched that user.

Giving the Functions identity **API Management Service Contributor** closes that, and it is a real
widening of what a scheduled job can do: Azure offers no narrower built-in for "re-scope a
subscription", so the same role that permits the move also permits creating, deleting and
regenerating developer subscriptions and editing the gateway's named values. Two things bound the
risk rather than remove it — the Functions host carries **no key protection** (it cannot read or
write a developer's key: [`IKeyProtector`](/foundry-gate/reference/configuration/) stays in the API), and every
move it makes writes a system-attributed `key.tier-changed` audit row alongside the run's own
`quota.monthly-reset` row, so the trail names every subscription a job touched.

A fork that would rather keep the job read-only can delete the `functionsApimContributor` resource
from `modules/control-plane-rbac.bicep`. The reset then records each refused move at Error with the
developer's identity, counts it into its own audit row, and leaves that developer on the tier the
gateway is still enforcing — loud and reconcilable rather than silently divergent, which is the
honest failure mode for a missing role.

Not expressible in Bicep. Two of these the deploy pipeline now does itself through the
`foundrygate` CLI; the rest are operator steps, with the exact commands in the
[Owner Setup Runbook](/foundry-gate/reference/owner-setup/) and the remaining production
work tracked in [#109](https://github.com/kolatts/foundry-gate/issues/109):

| Step | Who does it |
|---|---|
| **Contained database users** for `id-foundrygate-api-{env}` and `id-foundrygate-func-{env}` — `CREATE USER ... WITH SID = <client id>, TYPE = E` + `db_datareader`/`db_datawriter`, never `db_ddladmin` (the dacpac owns the schema) | **Automated**: `foundrygate db grant-identities --env {env} --api-identity-client-id <guid> --functions-identity-client-id <guid>`, run by `_deploy-database.yml` after seeding. Idempotent T-SQL (`IF NOT EXISTS` / `IS_ROLEMEMBER` guards); identity names default from the naming convention (`--api-identity` / `--functions-identity` override). **The client ids are required** — see [Why the client ids, not `FROM EXTERNAL PROVIDER`](#why-the-client-ids-not-from-external-provider) below. `--dry-run` prints the T-SQL. ([#106](https://github.com/kolatts/foundry-gate/issues/106)) |
| **Runner / developer firewall rules** on the SQL server | **Automated**: `foundrygate ip setup --env {env}` and `ip cleanup` — see [Firewall model](#firewall-model-for-azure-sql). ([#96](https://github.com/kolatts/foundry-gate/issues/96)) |
| The CI/OIDC principal must be a **member of the SQL Entra admin group** or the dacpac deploy, the seeders and `db grant-identities` cannot connect (there is no password fallback by design). It also needs **SQL Server Contributor** (or Contributor) on the resource group for the firewall-rule writes. | Operator (#109) |
| Runtime creation of **Claude** deployments needs Marketplace/SaaS permissions beyond Cognitive Services Contributor | Operator ([#107](https://github.com/kolatts/foundry-gate/issues/107)) |
| **Microsoft Graph application roles** for the Entra sync on **both** the API and Functions identities' service principals — no client secret; details below | Operator ([#110](https://github.com/kolatts/foundry-gate/issues/110), [#120](https://github.com/kolatts/foundry-gate/issues/120)) |

### Why the client ids, not `FROM EXTERNAL PROVIDER`

The obvious way to give a managed identity a database user is
`CREATE USER [id-foundrygate-api-{env}] FROM EXTERNAL PROVIDER`, and it is the wrong way
here. That statement asks **Azure SQL** to resolve the identity's *name* in Entra, which
requires the **logical server's own managed identity** to hold the Entra **Directory
Readers** role. `modules/sql.bicep` gives the server no identity at all, so on FoundryGate's
infrastructure as deployed the statement fails — and granting Directory Readers is a
tenant-level, privileged change no deploy should assume.

So `db grant-identities` takes the identities' **client ids** (the deployment outputs
`apiIdentityClientId` / `functionsIdentityClientId`) and creates each user
`WITH SID = <client id as varbinary(16)>, TYPE = E`. No directory lookup happens, nothing
beyond SQL Entra admin membership is needed, and the result is the same contained user.

- `_deploy-database.yml` requires the two client-id inputs and **fails the grant step with
  an actionable message** when they are empty — it never silently falls back.
- A fork whose SQL server *does* have a managed identity with Directory Readers can set the
  workflow input `allow-external-provider: true` (CLI: `--allow-external-provider`) to take
  the name-resolution path deliberately.

A related invariant sits on the other side of the pipeline: `db deploy` excludes both
`Users` **and** `RoleMembership` from the DacFx comparison, so a `--drop-objects` deploy
removes neither these users nor their `db_datareader`/`db_datawriter` memberships.

### Keeping the checked-in schema in sync: `db compare`

`FoundryGate.Data`'s EF entities are the schema source of truth; `FoundryGate.Database`'s
`dbo/Tables/*.sql` are hand-authored to match them, and `SchemaParityTests` (Predeployment,
cross-platform) is the CI alarm when they drift. `foundrygate db compare
[--connection-string <cs>] [--apply] [--check]` is the Windows-only developer convenience for
the other side of that loop — actually regenerating the `.sql` files instead of just failing a
test:

- Source = a live SQL Server database (default: the local docker instance `foundrygate local
  setup` keeps current via `EnsureCreated` against the current EF model); target =
  `FoundryGate.Database`'s `.sqlproj`. Only table shape is compared — DacFx's own logins,
  permissions, and every other non-table object type are excluded, so the command can never
  propose touching anything outside `dbo/Tables`.
- With no flags, or with `--check`: prints the differences and exits non-zero if any exist
  (usable as a local pre-commit gate). No differences means no files are ever touched.
- `--apply` additionally regenerates the affected table files via DacFx's own
  `PublishChangesToProject` — the same engine SqlPackage/SSDT's schema-compare tooling uses,
  which is why the checked-in files already look like its output — then exits 0 on success.
- Not a CI gate: `SchemaParityTests` remains the cross-platform backstop. On macOS/Linux,
  `db compare` fails fast with a message pointing at that test instead.

([#103](https://github.com/kolatts/foundry-gate/issues/103), deferred from
[#100](https://github.com/kolatts/foundry-gate/issues/100))

The Graph roles go on **both** control-plane identities' service principals —
`id-foundrygate-api-{env}` **and** `id-foundrygate-func-{env}` — as app-role assignments on the
Microsoft Graph service principal (appId `00000003-0000-0000-c000-000000000000`); `az ad app
permission` does not apply to managed identities, and no separate admin-consent step is needed. The
Functions identity needs the same three because the nightly `EntraSyncFunction`
([#151](https://github.com/kolatts/foundry-gate/issues/151)) runs the same reconciliation the API's
`POST /users/sync` and `POST /groups/sync-entra` do, calling Graph as its own identity. Grant them to
the Api identity only and `Entra__Enabled=true` gives you a job that fails every night with
`Authorization_RequestDenied`. Least privilege per the Graph reference for each call:

| Graph app role | Used for |
|---|---|
| `Application.Read.All` | `GET /servicePrincipals(appId='{clientId}')` and `GET /servicePrincipals/{id}/appRoleAssignedTo` — who is assigned to FoundryGate |
| `User.Read.All` | `GET /users?$filter=id in (...)&$select=id,displayName,mail,userPrincipalName,employeeId` and `GET /users/{id}` |
| `GroupMember.ReadBasic.All` | `GET /groups/{id}/members` / `transitiveMembers` with `$select=id` — group sync (#41) and expanding group-principal app-role assignments to their members during user sync (#121). Only `id` is selected, so this stays the least-privileged role the Graph reference lists for both calls; `GroupMember.Read.All` is not needed |

Verification runbook and a PowerShell grant snippet:
[#120](https://github.com/kolatts/foundry-gate/issues/120). Locally the Azure CLI login is
used instead, so the developer's own delegated Graph access applies.

## Pre-deployment script

`FoundryGate.Database/Scripts/PreDeployment.sql` is declared `<PreDeploy>` in the `.sqlproj`
(and removed from the SDK's `**/*.sql` `Build` glob, which would otherwise try to parse DML as
schema). DacFx embeds it in the dacpac as `predeploy.sql` and runs it **inside the deployment
transaction, before any schema change** — which is what lets it clear data that a new constraint
would otherwise reject. `db deploy` goes through `DacServices`, so it honours the script exactly as
a `SqlPackage` publish would; nothing extra is wired in the CLI or the workflow.

Two rules for anything added here:

- **Idempotent, and safe at any earlier schema version.** The script runs on *every* deploy, not
  only the one that first needs it — including against a brand-new empty database. Guard each block
  on the objects it touches (`IF OBJECT_ID(...) IS NOT NULL`), and write the change so a second run
  is a no-op.
- **It is data repair, not schema.** Schema belongs in `dbo/Tables/*.sql`, which the EF model is the
  source of truth for.

The one block it currently carries closes duplicate **pending** quota increase requests per
`(UserId, PeriodYear, PeriodMonth)`, keeping the newest, before
`IX_QuotaIncreaseRequests_PendingPerUserPeriod` is created
([#147](https://github.com/kolatts/foundry-gate/issues/147)). Until that filtered unique index
existed the rule was enforced only by a read-then-write check that two concurrent submissions could
both pass, so a fork that has been running may already hold exactly the rows the index forbids —
and because the dacpac step is part of the automatic deploy chain, a failed `CREATE UNIQUE INDEX`
would take the whole deployment down rather than just the index. The losers become `Rejected` with
`ReviewNotes = 'Superseded by a later request (pre-deploy dedupe)'` and no reviewer, the same shape
a lapsed request gets from the [#159](https://github.com/kolatts/foundry-gate/issues/159) sweep.

Verified against SQL Server 2022 in docker: a database seeded with two pending rows for one user and
period and the index dropped deploys cleanly, printing
`Pre-deploy: closed 1 duplicate pending quota increase request(s) …` and then creating the index; a
third deploy is silent, because there is nothing left to close.

**One operational note about filtered indexes.** A filtered index makes `SET QUOTED_IDENTIFIER ON`
mandatory for DML on that table. EF Core and SqlClient set it by default and DacFx sets it for its own
scripts, so application code and deploys are unaffected — but a hand-run `sqlcmd -Q` against
`QuotaIncreaseRequests` needs it stated explicitly or the write fails with `Msg 1934`.

## What the hosts are told

Set as Container App environment variables and Function App settings; keys are ASP.NET
Core configuration paths (`__` = section separator). None of them is a secret.

This table is the **provenance** view: which Azure resource each value comes from. For what each
setting *means*, which host reads it, and what happens when it is absent, the
[Configuration Reference](/foundry-gate/reference/configuration/#environment-variables--appsettings)
is the single per-host table. A predeployment test (`GatewayInfraBindingTests`) reads
`control-plane.bicep` and fails if the `Gateway__*` names here drift from what the control plane binds.

| Key | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` (API) / `AZURE_FUNCTIONS_ENVIRONMENT` + `DOTNET_ENVIRONMENT` (Functions) | `qa` or `prod` |
| `AZURE_CLIENT_ID` | the host's user-assigned identity client id — selects it in `AppTokenCredential` |
| `Azure__KeyVaultUrl` | `https://kv-fg-{env}-{suffix}.vault.azure.net/` |
| `ConnectionStrings__FoundryGate` | `Server=tcp:<fqdn>,1433;Database=sqldb-foundrygate-{env};Authentication=Active Directory Default;Encrypt=True;...` |
| `AzureAd__Instance` / `TenantId` / `ClientId` / `Audience` | bearer-token validation |
| `Cors__AllowedOrigins__0` (API) | `https://<static web app hostname>` |
| `OpenTelemetry__Enabled` / `OpenTelemetry__ConnectionString` | `true` / App Insights connection string |
| `Gateway__SubscriptionId`, `Gateway__ResourceGroup`, `Gateway__ApimName`, `Gateway__ApimGatewayUrl`, `Gateway__KeyEncryptionKeyUri`, `Gateway__FoundryAccountNames__{i}` | gateway addressing for the APIM key service, Foundry deployment service and reconciliation ([#108](https://github.com/kolatts/foundry-gate/issues/108)) |
| `Gateway__LogAnalyticsWorkspaceId` / `Gateway__LogAnalyticsWorkspaceResourceId` | the workspace **GUID** (`customerId` — what `LogsQueryClient.QueryWorkspaceAsync` and `/v1/workspaces/{id}/query` mean by "workspace id") / the ARM resource id (`QueryResourceAsync`, management plane) |
| `Gateway__Tiers__{i}__ProductId` / `__DisplayName` / `__MonthlyTokenQuota` | the quota tier table, projected from the same `quotaTiers` parameter that creates the APIM products and renders their `llm-token-limit` policies ([#201](https://github.com/kolatts/foundry-gate/issues/201)) |
| `Entra__ApplicationClientId` | the app registration whose service principal carries the developer assignments, on **both** hosts (the Functions worker binds no `AzureAd` section). `Entra__Enabled` is deliberately unset — turning directory sync on is an owner step, because it needs Graph application roles no Bicep can grant |
| `Gateway__ModelAliases__{i}__Tier` / `__Alias` / `__DeploymentName` / `__Provider` | the model alias map, flattened from `productModelAliases` ([#153](https://github.com/kolatts/foundry-gate/issues/153)) |
| `AzureWebJobsStorage__accountName` / `__credential=managedidentity` / `__clientId` (Functions) | identity-based host storage — also where the scheduled jobs take their lock leases, so they needed no setting of their own |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` (Functions) | host telemetry |

Every value in the `Gateway` section is set on **both** hosts from one shared block in the Bicep, so
the API and the Functions host can never be told about different gateways — and since
[#201](https://github.com/kolatts/foundry-gate/issues/201) that includes the quota tier table, which
comes from the very `quotaTiers` parameter the APIM products are created from. A fork that overrides
`quotaTiers` at deploy time therefore has nothing further to edit: the caps the gateway enforces and
the caps the control plane validates budgets against are the same array. (A `local` host, which has
no gateway, reads the table from each project's `appsettings.local.json` instead.)

## Outputs contract

Read with `az deployment sub show --name <deployment> --query properties.outputs`. The
deploy workflows consume these rather than re-deriving names. Control-plane outputs are
empty strings when `deployControlPlane = false`.

| Output | Used by |
|---|---|
| `resourceGroupName` | every workflow |
| `apimGatewayUrl`, `apimName`, `apimPrincipalId`, `anthropicApiUrl`, `openaiApiUrl`, `productIds`, `defaultProductId` | CLI setup docs, control plane |
| `logAnalyticsWorkspaceId` (ARM id), `logAnalyticsWorkspaceCustomerId` (GUID), `logAnalyticsWorkspaceName`, `appInsightsConnectionString` | monitoring, reconciliation |
| `foundryAccountNames` | control plane |
| `controlPlaneDeployed`, `containerAppIsBootstrapImage` | workflow branching (the api-deploy workflow's first push replaces the placeholder) |
| `sqlServerName`, `sqlServerFqdn`, `sqlDatabaseName`, `sqlEntraConnectionString`, `sqlAdminGroupName` | `_deploy-database.yml` (`sql-server-name`, `sql-database-name`, `sql-resource-group` → CLI `ip setup` / `ip cleanup` / `db grant-identities`) |
| `keyVaultName`, `keyVaultUri`, `keyEncryptionKeyUri` | out-of-band secret management |
| `containerRegistryName`, `containerRegistryLoginServer` | api-deploy (`docker push`, image tag) |
| `containerAppsEnvironmentName`, `containerAppName`, `containerAppFqdn` | api-deploy (`az containerapp update`), postdeployment tests |
| `functionAppName`, `functionAppHostname`, `functionsStorageAccountName` | functions-deploy |
| `staticWebAppName`, `staticWebAppHostname` | ui-deploy (deployment token lookup), CORS, Entra redirect URI |
| `apiIdentityName` / `ClientId` / `PrincipalId`, `functionsIdentityName` / `ClientId` / `PrincipalId` | `_deploy-database.yml` (`api-identity-name` / `functions-identity-name`, and **required**: `api-identity-client-id` / `functions-identity-client-id` → CLI `db grant-identities`, which creates the contained users `WITH SID` — see [Why the client ids](#why-the-client-ids-not-from-external-provider)), Graph permission grants |
| `modelAliasRows`, `quotaTierRows` | what the control plane was actually handed as `Gateway__ModelAliases__*` / `Gateway__Tiers__*` — readable after a deploy without opening the app settings blade, so a fork that overrode `productModelAliases` or `quotaTiers` can confirm the override landed |

## Health probes and serverless auto-pause

The API exposes `/health` (hermetic liveness) and `/health/ready` (adds an
`AppDbContext` connectivity check). The Container App wires them as:

| Probe | Path | Why |
|---|---|---|
| Startup | `/health/ready` (30 × 5 s) | a wrong connection string or identity fails the deploy right there; the window covers a paused serverless database resuming |
| Liveness | `/health` (30 s) | restart only on a hung process |
| Readiness | `/health` (15 s) | **deliberately not** `/health/ready`: with `minReplicas: 1` a DB-touching readiness probe would open a SQL connection every 15 s and the dev serverless database would never reach its 60-minute auto-pause |

The trade-off is explicit: for a single-replica admin API, "database down" surfacing as
500s instead of 503s is not worth an always-on vCore in dev. The dev budget line on
[Cost & Capacity](/foundry-gate/reference/cost-and-capacity/) (serverless, auto-pause)
depends on this stance — and on periodic Functions jobs keeping their cadence above the
pause delay. In the bootstrap-image mode all three probes use `/health` on port 80.

## Firewall model for Azure SQL

Public endpoint, Entra-only authentication, and two kinds of firewall rule:

- `AllowAllWindowsAzureIps` (`0.0.0.0`–`0.0.0.0`) — declared in Bicep; lets Container Apps
  and Functions connect without a VNet.
- Runner and developer IP rules — created at deploy time by `foundrygate ip setup --env
  {env}` ([#96](https://github.com/kolatts/foundry-gate/issues/96)) against
  `sql-foundrygate-{env}-{suffix}` in `rg-foundrygate-{env}`. Deliberately **not** declared
  in Bicep: an incremental deployment leaves undeclared child resources alone, so a re-run
  never wipes a rule the pipeline just added.

`ip setup` detects the caller's public IPv4 address (api.ipify.org, then ifconfig.me;
`--ip` overrides), finds the server by listing `rg-foundrygate-{env}` for the single
`sql-foundrygate-{env}-*` server (`--server` / `--resource-group` override — the pipeline
passes both from its inputs), and creates or updates a single-address rule. It is
idempotent: a rule that already allows the address is left alone. Rule names tell you who
made them:

| Rule | Made by | Lifetime |
|---|---|---|
| `AllowAllWindowsAzureIps` | Bicep | permanent |
| `gha-{run id}-{yyyyMMddHHmm}` | a GitHub Actions runner (`GITHUB_ACTIONS=true`); the UTC minute is in the name because ARM keeps no creation time on a firewall rule | removed by `foundrygate ip cleanup --env {env} --older-than {hours}`, which `_deploy-database.yml` runs `if: always()` at the end: this run's own `gha-{run id}-*` rules go unconditionally, other `gha-*` rules once they are older than the threshold (default 2 h) or carry no timestamp. Developer and hand-made rules are never candidates. `--dry-run` previews. |
| `fg-dev-{machine}-{user}` | a developer running `foundrygate ip setup --env dev` locally (Azure CLI credential) | until removed by hand |

Both commands authenticate with the same `AppTokenCredential` chain as the API (Azure CLI
locally; on a runner the `azure/login@v2` session, with `AZURE_SUBSCRIPTION_ID` selecting
the subscription), and a firewall-rule write is a plain ARM operation — SQL Server
Contributor on the resource group is enough; SQL Entra admin membership is only needed to
*connect* to the database afterwards. Names such as `production` are accepted for `--env`
and mapped to the Bicep `environmentName` (`prod`) before any resource name is derived.

Private endpoints (spec §11 for prod) are a later hardening step and would replace the
first rule, not the second.
