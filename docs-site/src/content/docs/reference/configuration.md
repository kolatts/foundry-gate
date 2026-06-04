---
title: Configuration Reference
description: Every SystemConfiguration key and environment variable consumed by FoundryGate.
---

import { Aside } from '@astrojs/starlight/components';

## SystemConfiguration keys

These are stored in the `SystemConfiguration` database table and editable by admins at `/config`. All values are strings.

| Key | Default | Description |
|---|---|---|
| `DefaultMonthlyTokenQuota` | `"1000000"` | Per-user monthly token budget when no user or group override applies. |
| `ApimResourceId` | `""` | ARM resource ID of the APIM instance (`/subscriptions/.../Microsoft.ApiManagement/service/...`). Used by `IApimKeyService` to target Management plane calls. |
| `ApimGatewayUrl` | `""` | APIM gateway base URL shown to developers on `/me` for CLI configuration (e.g., `https://my-apim.azure-api.net`). |
| `ApimProductId` | `"foundrygate"` | APIM product name that all Foundry API routes belong to. New APIM subscriptions are scoped to this product. |
| `FoundryResourceId` | `""` | ARM resource ID of the Azure AI Foundry account. Used by `IFoundryDeploymentService` for model deployment management. |
| `EntraTenantId` | `""` | Azure AD tenant ID used by the Microsoft Graph SDK for bulk user sync. |
| `EntraGroupSyncEnabled` | `"false"` | Set to `"true"` to enable automatic Entra group member sync. When `false`, group sync must be triggered manually from the admin UI. |
| `ResetDayOfMonth` | `"1"` | Day the monthly quota reset fires. Always `"1"` in v1 — changing this is unsupported and reserved for a future release. |

<Aside type="caution">
Blank values (`""`) are deliberate placeholders. The system will not function correctly until `ApimResourceId`, `ApimGatewayUrl`, `ApimProductId`, and `FoundryResourceId` are set to real values.
</Aside>

---

## API environment variables / appsettings

These are set as Container App environment variables (sourced from Key Vault references for secrets).

| Key | Source | Description |
|---|---|---|
| `ConnectionStrings__FoundryGate` | Key Vault secret | Azure SQL connection string |
| `AzureAd__TenantId` | Config / env var | Entra tenant ID for bearer token validation |
| `AzureAd__ClientId` | Config / env var | Entra App Registration client ID |
| `AzureAd__Audience` | Config / env var | Token audience (usually `api://{clientId}`) |
| `Azure__ApimResourceGroup` | Config / env var | Resource group containing the APIM instance |
| `Azure__ApimServiceName` | Config / env var | APIM service name (short name, not full resource ID) |
| `Azure__FoundrySubscriptionId` | Config / env var | Azure subscription ID containing Foundry |
| `Azure__FoundryResourceGroup` | Config / env var | Resource group containing the Foundry account |
| `Azure__FoundryAccountName` | Config / env var | Foundry account name (short name) |
| `Azure__KeyVaultUri` | Config / env var | Key Vault URI for key wrapping (APIM key encryption) |
| `Graph__ClientId` | Config / env var | App registration client ID for Graph SDK (may differ from AzureAd__ClientId) |
| `Graph__ClientSecret` | Key Vault secret | Client secret for Graph app-only credentials |

---

## Function App settings

Azure Functions shares the SQL connection string and Azure identity settings but adds:

| Key | Description |
|---|---|
| `Azure__MonitorWorkspaceId` | Log Analytics workspace ID used by `UsageSyncFunction` to query token metrics |
| `AzureWebJobsStorage` | Storage account connection string (required by Functions runtime) |
