# Bicep infrastructure modules and parameter files

> GitHub: #13  
> Milestone: v0.3 — Infrastructure  
> Labels: epic, infra

## Overview
This epic defines all Azure infrastructure for Foundry Gate as Bicep modules, producing a fully repeatable deployment from a single `az deployment sub create` command. Modules cover: Azure SQL, Container Apps (API), Static Web Apps (Blazor UI), Azure Functions Flex Consumption (background jobs), Key Vault, and the role assignments that wire each Managed Identity to the resources it needs. Parameter files for `dev` and `prod` environments are included so the same modules deploy correctly in both.

## Approach

### Write Bicep modules: SQL, Container Apps, Static Web Apps, Functions, Key Vault, and role assignments (#43)
Create `infra/modules/` with one `.bicep` file per resource type:

- `sql.bicep` — Azure SQL server + database; outputs connection string stored in Key Vault as a secret.
- `containerApp.bicep` — Container Apps environment + the API app with system-assigned Managed Identity, Key Vault secret references for the SQL connection string and Entra Graph client secret, and ingress on port 8080.
- `staticWebApp.bicep` — Static Web Apps (Standard tier for custom domain support).
- `functionApp.bicep` — Azure Storage Account (required by Functions runtime), a Flex Consumption Function App with system-assigned Managed Identity, `WEBSITE_RUN_FROM_PACKAGE=1`, and Key Vault references for the SQL connection string and App Insights workspace ID. Flex Consumption provides scale-to-zero with no always-on cost.
- `keyVault.bicep` — Key Vault with RBAC access model (not vault access policies); secrets: `SqlConnectionString`, `GraphClientSecret`.
- `roleAssignments.bicep` — Grants:
  - Container App identity → `Key Vault Secrets User` on Key Vault
  - Container App identity → `API Management Service Contributor` on the APIM resource
  - Container App identity → `Cognitive Services Contributor` on the Foundry resource (for model deployment provisioning — see epic #20)
  - Function App identity → `Key Vault Secrets User` on Key Vault
  - Function App identity → `Monitoring Reader` on the Log Analytics workspace (for usage sync)

Files expected to be created or modified:
- `infra/modules/sql.bicep`
- `infra/modules/containerApp.bicep`
- `infra/modules/staticWebApp.bicep`
- `infra/modules/functionApp.bicep`
- `infra/modules/keyVault.bicep`
- `infra/modules/roleAssignments.bicep`

### Write root main.bicep orchestrator and dev/prod parameter files (#44)
`infra/main.bicep` is subscription-scoped: it creates the resource group then calls each module in dependency order. Accept top-level parameters: `environmentName`, `location`, `sqlAdminPassword` (secure string), `entraClientId`, `entraTenantId`, `apimResourceId`, `foundryResourceId`, `appInsightsWorkspaceId`. Wire module outputs together (SQL secret → Container App and Function App Key Vault refs; Function App name → output for CI/CD). Create `infra/parameters/dev.bicepparam` and `infra/parameters/prod.bicepparam` with environment-appropriate SKUs (serverless SQL for dev, General Purpose for prod; Functions Flex Consumption for both). Include `infra/README.md` with the one-liner deployment command.

Files expected to be created or modified:
- `infra/main.bicep`
- `infra/parameters/dev.bicepparam`
- `infra/parameters/prod.bicepparam`
- `infra/README.md`

## Implementation notes (2026-09-01, #43/#44)

Direction updates that supersede parts of the approach above (CONVENTIONS.md wins over the
issue bodies; the #13 direction comment supersedes the "APIM/Foundry passed in as resource
ids" wording):

- APIM and the Foundry accounts are **created by `infra/main.bicep`** (gateway data plane,
  PR #87/#93); the control plane attaches to them by name. No `apimResourceId` /
  `foundryResourceId` params.
- **No SQL admin password.** Azure SQL is Entra-only (`azureADOnlyAuthentication: true`,
  admin = Entra security group via `sqlAdminGroupObjectId`/`sqlAdminGroupName`); the app
  connects with `Authentication=Active Directory Default`. The connection string is
  therefore not a secret and is set as a plain env var, not a Key Vault reference.
- **User-assigned** identities (not system-assigned) so AcrPull / storage roles exist before
  the Container App and Function App are created; `AZURE_CLIENT_ID` is set on both hosts.
- **No Key Vault secrets in Bicep** (`SqlConnectionString` unnecessary; `GraphClientSecret`
  replaced by Graph app permissions on the API identity — #110). The vault holds the optional
  `fg-apim-key-encryption` RSA key for #95 instead.
- Leaf modules are kebab-case (`sql.bicep`, `container-app.bicep`, `static-web-app.bicep`,
  `function-app.bicep`, `key-vault.bicep`, `control-plane-rbac.bicep`, plus
  `managed-identities.bicep`, `container-registry.bicep`, `storage-account.bicep`),
  orchestrated by `modules/control-plane.bicep` behind `deployControlPlane` in main.bicep.
  `infra/README.md` was not added — the reference lives in
  `docs-site/src/content/docs/reference/infrastructure.md` (one source of truth on the
  public site).
- Review fix pass (PR #111): role GUIDs re-verified against `az role definition list`;
  bootstrap placeholder image (`k8se/quickstart`, :80, `/health` only — verified under
  docker) switches the Container App to port 80 + `/health`-only probes; readiness probe
  is `/health` (hermetic) so serverless auto-pause stays real, startup probe is
  `/health/ready`; `FG_API_IMAGE` (both) and `FG_SQL_ADMIN_GROUP_*` (prod) are required
  env vars with no default; serverless is derived from the SQL SKU name;
  `Gateway__LogAnalyticsWorkspaceId` is the workspace GUID (`customerId`), the ARM id is
  `...WorkspaceResourceId`. Prod-grade knobs not yet plumbed: #134.

## Verification
- [x] `az bicep build --file infra/main.bicep` compiles with no errors (and `az bicep lint`
  clean apart from the pre-existing BCP081 for the `2026-07-01` CognitiveServices API
  version; `az bicep build-params` clean for test/dev/prod)
- [x] `az deployment sub what-if` against a dev subscription shows expected resource changes
  (2026-09-01, Imagile Paid, throwaway `environmentName=whatif`, `createModelDeployments=false`:
  status Succeeded, 59 Creates — full gateway + SQL server/db/firewall, Container Apps
  env + app, ACR, Key Vault + key, two identities, storage + deployment container, Flex plan +
  Function App, Static Web App; role assignments are not surfaced by what-if because their
  principal ids resolve at deploy time)
- [ ] Container App and Function App environment variables resolve — verified on a live
  deploy (#105). Note: SQL is Entra-auth so no Key Vault reference is involved; the API
  resolves `@KeyVault()` tokens itself via `Azure__KeyVaultUrl`.
- [ ] All Managed Identity role assignments are present after deployment (#105)
- [x] Function App has scale-to-zero configured (Flex Consumption plan: `FC1`,
  `functionAppConfig.scaleAndConcurrency`, no always-on) — verified in template/what-if;
  runtime behaviour on live deploy (#105)
- [x] `prod.bicepparam` uses production-grade SQL SKU (`GP_Gen5_2`, provisioned, Geo backups)
- [ ] Contained DB users for the two identities created post-dacpac (#106)
- [ ] CI principal in the SQL Entra admin group; real Entra client id in `FG_ENTRA_API_CLIENT_ID` (#109)
