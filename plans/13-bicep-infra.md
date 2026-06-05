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

## Verification
- [ ] `az bicep build --file infra/main.bicep` compiles with no errors
- [ ] `az deployment sub what-if` against a dev subscription shows expected resource changes
- [ ] Container App and Function App environment variables resolve to Key Vault references
- [ ] All Managed Identity role assignments are present after deployment
- [ ] Function App has scale-to-zero configured (Flex Consumption plan)
- [ ] `prod.bicepparam` uses production-grade SQL SKU
