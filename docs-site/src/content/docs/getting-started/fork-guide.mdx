---
title: Fork Guide
description: Deploy FoundryGate to your own Azure tenant from scratch.
sidebar:
  order: 1
---

import { Steps, Aside, Code } from '@astrojs/starlight/components';

This guide takes you from a blank Azure subscription to a running FoundryGate deployment. Budget two hours if you're comfortable with Azure; longer if this is your first time with Bicep or APIM.

## Prerequisites

- Azure CLI (`az`) ≥ 2.60
- Bicep CLI (`az bicep install`)
- .NET 10 SDK
- GitHub CLI (`gh`) ≥ 2.40
- An existing **Azure API Management** instance
- An existing **Azure AI Foundry** account with at least one model deployment

---

<Steps>

1. ### Fork and clone the repo

   ```sh
   gh repo fork kolatts/foundry-gate --clone
   cd foundry-gate
   ```

2. ### Register an Entra App

   ```sh
   az ad app create \
     --display-name "FoundryGate" \
     --sign-in-audience AzureADMyOrg
   ```

   Note the `appId` — you'll need it as `entraClientId` in the Bicep params.

   **Add the Admin app role** in the Azure portal under your app registration → App roles → Create:
   - Display name: `FoundryGate Admin`
   - Value: `FoundryGate.Admin`
   - Allowed member types: Users/Groups

   **Add Graph API permissions** (Application, with admin consent):
   - `User.Read.All`
   - `Group.Read.All`
   - `GroupMember.Read.All`

3. ### Configure APIM

   <Aside type="caution">
   This is the most common failure point for forks. Take your time here.
   </Aside>

   In your APIM instance:

   **a. Create a product** named `foundrygate` (or your preferred name — record it as `apimProductId`).

   **b. Add an API** that backends to your Foundry endpoint:
   - Backend URL: `https://{your-foundry-name}.openai.azure.com/`
   - URL scheme: HTTPS
   - Add this API to the `foundrygate` product.

   **c. Apply the token metric policy** on the API (All operations):
   ```xml
   <policies>
     <inbound>
       <base />
       <llm-emit-token-metric
         namespace="foundrygate"
         output-type="SubscriptionId" />
     </inbound>
   </policies>
   ```

   **d. Apply the token limit policy** (optional but recommended for rate safety):
   ```xml
   <azure-openai-token-limit
     tokens-per-minute="500000"
     counter-key="@(context.Subscription.Id)"
     estimate-prompt-tokens="true" />
   ```

   **e. Grant APIM access to Foundry:**
   ```sh
   az role assignment create \
     --role "Cognitive Services User" \
     --assignee <apim-managed-identity-principal-id> \
     --scope <foundry-resource-id>
   ```

4. ### Set Bicep parameters

   Copy `infra/parameters/dev.bicepparam` and fill in your values:

   ```bicep
   param location         = 'eastus2'
   param environmentName  = 'dev'
   param entraClientId    = '<your-app-registration-client-id>'
   param entraTenantId    = '<your-tenant-id>'
   param apimResourceId   = '/subscriptions/.../Microsoft.ApiManagement/service/...'
   param apimProductId    = 'foundrygate'
   param foundryResourceId = '/subscriptions/.../Microsoft.CognitiveServices/accounts/...'
   ```

5. ### Configure GitHub OIDC

   Create a federated credential in the Entra App for each environment:

   ```sh
   # For the dev environment
   az ad app federated-credential create \
     --id <appId> \
     --parameters '{
       "name": "foundrygate-dev",
       "issuer": "https://token.actions.githubusercontent.com",
       "subject": "repo:YOUR_ORG/foundry-gate:environment:dev",
       "audiences": ["api://AzureADTokenExchange"]
     }'
   ```

   Repeat for `environment:production`, `environment:dev-destroy`, `environment:prod-destroy`.

6. ### Add GitHub Actions secrets and variables

   **Secrets** (repo-level):
   | Name | Value |
   |---|---|
   | `SQL_ADMIN_PASSWORD` | A strong password for the SQL admin account |
   | `GRAPH_CLIENT_SECRET` | The Entra App client secret for Graph sync |

   **Variables** (per GitHub Environment — set in repo Settings → Environments):
   | Name | dev | prod |
   |---|---|---|
   | `AZURE_CLIENT_ID` | `<appId>` | `<appId>` |
   | `AZURE_TENANT_ID` | `<tenantId>` | `<tenantId>` |
   | `AZURE_SUBSCRIPTION_ID` | `<subscriptionId>` | `<subscriptionId>` |
   | `RESOURCE_GROUP` | `rg-foundrygate-dev` | `rg-foundrygate-prod` |
   | `LOCATION` | `eastus2` | `eastus2` |

7. ### Deploy infrastructure

   ```sh
   az deployment sub create \
     --template-file infra/main.bicep \
     --parameters infra/parameters/dev.bicepparam \
     --parameters sqlAdminPassword=$SQL_ADMIN_PASSWORD
   ```

   Expected duration: 8–12 minutes.

8. ### Push to main

   ```sh
   git push origin main
   ```

   This triggers `api-deploy.yml`, `functions-deploy.yml`, and `ui-deploy.yml`. Watch the Actions tab. First run takes ~5 minutes as the Docker image builds cold.

9. ### Verify

   - Navigate to the Static Web App URL (from `infra` output or Azure portal).
   - Sign in with an Entra account.
   - You should land on `/me` with a fresh `QuotaAllocation` and a provisioned APIM key.
   - Assign yourself the `FoundryGate.Admin` app role in Entra and refresh — the admin nav appears.

</Steps>

## Troubleshooting

**APIM returns 401 on first AI call**  
Check that the `foundrygate` product is published and your APIM subscription is in `active` state. Run `GET /keys/me` to confirm the key was provisioned.

**Graph sync fails with 403**  
Admin consent has not been granted for the Graph API permissions. Go to Entra → Enterprise Applications → your app → Permissions → Grant admin consent.

**Container App shows "no healthy upstream"**  
The API is still starting or the migration hasn't run. Check the Container App log stream and verify the SQL connection string Key Vault reference resolved correctly.
