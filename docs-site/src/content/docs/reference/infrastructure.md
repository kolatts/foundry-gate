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
| `sql.bicep` | Entra-only SQL server, `AllowAllWindowsAzureIps` firewall rule, database |
| `container-registry.bicep` | Basic ACR, admin user disabled |
| `storage-account.bicep` | Functions storage, shared keys off, `function-deployments` container |
| `static-web-app.bicep` | SWA (Free/Standard), `provider: Custom` |
| `control-plane-rbac.bicep` | all role assignments below |
| `container-app.bicep` | Container Apps environment (logs via diagnostic setting — no workspace key) + the API app; bootstrap-image detection (port 80, `/health`-only probes) |
| `function-app.bicep` | Flex Consumption plan + Function App, identity-based storage |

## Control-plane parameters

| Parameter | dev | prod | Purpose |
|---|---|---|---|
| `deployControlPlane` | `true` | `true` | default `false` (gateway only) |
| `appEnvironment` | `qa` | `prod` | `ASPNETCORE_ENVIRONMENT` — lowercase `qa`/`prod`; `local` is docker-only |
| `sqlAdminGroupObjectId` / `sqlAdminGroupName` | `SG_IMAGILE_SQL_ADMINS` | `$FG_SQL_ADMIN_GROUP_OBJECT_ID` / `$FG_SQL_ADMIN_GROUP_NAME` (required) | SQL server administrator (Entra-only auth; no SQL login exists) |
| `sqlDatabaseSku` | `GP_S_Gen5` ×1 — serverless, 60-min auto-pause | `GP_Gen5_2`, provisioned | serverless is derived from the SKU name (`GP_S_*`) |
| `sqlBackupStorageRedundancy` | `Local` | `Geo` | |
| `entraTenantId` | tenant | tenant | `AzureAd__TenantId` |
| `entraApiClientId` | `$FG_ENTRA_API_CLIENT_ID` | same | `AzureAd__ClientId`; zero GUID until the app registration exists |
| `entraApiAudience` | `api://{clientId}` | same | `AzureAd__Audience` |
| `apiContainerImage` | `$FG_API_IMAGE` (required) | same | see re-run invariant 2 |
| `containerAppMinReplicas` / `MaxReplicas` | 1 / 2 | 1 / 3 | min 1 — the admin API is the UI's only backend |
| `staticWebAppSku` / `staticWebAppLocation` | `Free` / `eastus2` | `Standard` / `eastus2` | SWA is only offered in a handful of regions |
| `functionsRuntimeVersion` | `10.0` | `10.0` | .NET isolated worker |
| `keyVaultPurgeProtection` | `false` | `true` | irreversible once on |
| `keyVaultSoftDeleteRetentionInDays` | 7 | 90 | |
| `createKeyEncryptionKey` | `true` | `true` | RSA key for wrapping APIM subscription keys at rest |

Nothing in a parameter file is a secret: SQL is Entra-only, storage is identity-based,
registry pulls are identity-based, and the App Insights connection string is a module
output. Entra object ids and client ids are identifiers, not credentials.

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
| Functions | Storage Blob Data Owner | storage | host state + Flex deployment container |
| Functions | Storage Queue Data Contributor | storage | queue triggers/outputs |
| Functions | Storage Table Data Contributor | storage | table bindings |
| APIM (system-assigned) | Cognitive Services User | each Foundry account | gateway → model backends, no account keys |

Not expressible in Bicep. Two of these the deploy pipeline now does itself through the
`foundrygate` CLI; the rest remain operator steps tracked in
[#109](https://github.com/kolatts/foundry-gate/issues/109):

| Step | Who does it |
|---|---|
| **Contained database users** for `id-foundrygate-api-{env}` and `id-foundrygate-func-{env}` — `CREATE USER ... FROM EXTERNAL PROVIDER` + `db_datareader`/`db_datawriter`, never `db_ddladmin` (the dacpac owns the schema) | **Automated**: `foundrygate db grant-identities --env {env}`, run by `_deploy-database.yml` after seeding. Idempotent T-SQL (`IF NOT EXISTS` / `IS_ROLEMEMBER` guards); identity names default from the naming convention (`--api-identity` / `--functions-identity` override). Pass `--api-identity-client-id` / `--functions-identity-client-id` (deployment outputs `apiIdentityClientId` / `functionsIdentityClientId`) to create the users `WITH SID` instead, which needs no Directory Readers permission when the executing principal is itself a service principal. `--dry-run` prints the T-SQL. ([#106](https://github.com/kolatts/foundry-gate/issues/106)) |
| **Runner / developer firewall rules** on the SQL server | **Automated**: `foundrygate ip setup --env {env}` and `ip cleanup` — see [Firewall model](#firewall-model-for-azure-sql). ([#96](https://github.com/kolatts/foundry-gate/issues/96)) |
| The CI/OIDC principal must be a **member of the SQL Entra admin group** or the dacpac deploy, the seeders and `db grant-identities` cannot connect (there is no password fallback by design). It also needs **SQL Server Contributor** (or Contributor) on the resource group for the firewall-rule writes. | Operator (#109) |
| Runtime creation of **Claude** deployments needs Marketplace/SaaS permissions beyond Cognitive Services Contributor | Operator ([#107](https://github.com/kolatts/foundry-gate/issues/107)) |
| Graph application permissions for the Entra sync go on the API identity's service principal — no client secret | Operator ([#110](https://github.com/kolatts/foundry-gate/issues/110)) |

## What the hosts are told

Set as Container App environment variables and Function App settings; keys are ASP.NET
Core configuration paths (`__` = section separator). None of them is a secret.

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
| `AzureWebJobsStorage__accountName` / `__credential=managedidentity` / `__clientId` (Functions) | identity-based host storage |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` (Functions) | host telemetry |

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
| `apiIdentityName` / `ClientId` / `PrincipalId`, `functionsIdentityName` / `ClientId` / `PrincipalId` | `_deploy-database.yml` (`api-identity-name` / `functions-identity-name`, `api-identity-client-id` / `functions-identity-client-id` → CLI `db grant-identities`), Graph permission grants |

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
