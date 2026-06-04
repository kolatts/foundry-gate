# Bicep infrastructure modules and parameter files

> GitHub: #13  
> Milestone: v0.3 — Infrastructure  
> Labels: epic, infra

## Overview
This epic defines all Azure infrastructure for FoundryGate as Bicep modules, producing a fully repeatable deployment from a single `az deployment sub create` command. The modules cover: Azure SQL (the single shared database), Container Apps (for the API), Static Web Apps (for the Blazor WASM frontend), Key Vault (secrets and certificates), and the role assignments that wire Managed Identity to APIM and Application Insights. Parameter files for `dev` and `prod` environments are included so the same modules deploy correctly in both.

## Approach

### Write Bicep modules: SQL, Container Apps, Static Web Apps, Key Vault, and role assignments (#43)
Create a `infra/modules/` directory with one `.bicep` file per resource type. `sql.bicep` provisions an Azure SQL server and database with the connection string output as a secure Key Vault secret. `containerApp.bicep` provisions a Container App with system-assigned Managed Identity, environment variables sourced from Key Vault references, and ingress rules. `staticWebApp.bicep` provisions a Static Web App in the Standard tier with a linked custom domain output. `keyVault.bicep` provisions Key Vault with RBAC access model (not vault access policies). `roleAssignments.bicep` grants the Container App's Managed Identity the `API Management Service Contributor` role on the APIM instance and the `Monitoring Reader` role on the Log Analytics workspace. Each module outputs the resource IDs and names needed by the orchestrator.

Files expected to be created or modified:
- `infra/modules/sql.bicep`
- `infra/modules/containerApp.bicep`
- `infra/modules/staticWebApp.bicep`
- `infra/modules/keyVault.bicep`
- `infra/modules/roleAssignments.bicep`

### Write root main.bicep orchestrator and dev/prod parameter files (#44)
Create `infra/main.bicep` as the subscription-scoped orchestrator that creates the resource group and calls each module. Accept top-level parameters: `environmentName`, `location`, `sqlAdminPassword` (secure string), `entraClientId`, `apimServiceName`, `appInsightsWorkspaceId`. Wire module outputs together (e.g., pass the SQL Key Vault secret reference into the Container App environment). Create `infra/parameters/dev.bicepparam` and `infra/parameters/prod.bicepparam` with environment-appropriate values (SKUs, replica counts, retention policies). Include a `infra/README.md` with the one-liner deployment command.

Files expected to be created or modified:
- `infra/main.bicep`
- `infra/parameters/dev.bicepparam`
- `infra/parameters/prod.bicepparam`
- `infra/README.md`

## Verification
- [ ] `az bicep build --file infra/main.bicep` compiles with no errors
- [ ] `az deployment sub what-if` against a dev subscription shows expected resource changes
- [ ] Container App environment variables resolve to Key Vault references correctly
- [ ] Managed Identity role assignments are present after deployment
- [ ] `prod.bicepparam` uses production-grade SKUs (P1 SQL, Standard SWA)
