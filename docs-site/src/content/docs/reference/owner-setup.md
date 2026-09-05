---
title: Owner Setup Runbook
description: The exact az and gh commands that stand up a FoundryGate environment's Entra identities, RBAC and GitHub Environment variables before the first deploy.
---

Everything a fresh FoundryGate environment needs **before** its first
[Deploy All](/foundry-gate/reference/ci-cd/) run: two application identities for the app
itself, three CI identities for the pipeline, one Entra group for Azure SQL, and the
GitHub Environment variables that point the workflows at all of it.

These were long recorded as "owner actions that cannot be done by an agent". That was
about *privileges*, not tooling — the whole list is CLI-driveable by anyone signed in as
a subscription Owner with Entra application-administration rights. The commands below
are the ones that actually ran for `dev` on 2026-09-05, transcribed verbatim except for
substituting the ids they produced.

:::caution
Nothing here is a secret. Client ids, tenant ids, subscription ids and object ids are
identifiers — they belong in GitHub Environment **variables**, never secrets. There is no
credential to store at all: the pipeline authenticates with OIDC federated credentials.
:::

## 0. Pick your values

```bash
ENV=dev
REPO=kolatts/foundry-gate
SUBSCRIPTION="Imagile Paid"
SUB_ID=$(az account show --subscription "$SUBSCRIPTION" --query id -o tsv)
TENANT_ID=$(az account show --subscription "$SUBSCRIPTION" --query tenantId -o tsv)
```

Every `az` call below should carry `--subscription "$SUBSCRIPTION"` for ARM operations.
Graph operations (`az ad`, `az rest` against `graph.microsoft.com`) are tenant-scoped and
ignore it.

## 1. The API app registration

Exposes `api://{clientId}/access_as_user` and defines the `FoundryGate.Admin` app role
that the API's `AdminOnly` policy checks.

```bash
API_APP_ID=$(az ad app create --display-name "FoundryGate.Api ($ENV)" \
  --sign-in-audience AzureADMyOrg --query appId -o tsv)
API_OBJ_ID=$(az ad app show --id "$API_APP_ID" --query id -o tsv)
```

`az ad app create` cannot express a scope or an app role, so patch Graph directly. Both
`id` fields are GUIDs you generate (`uuidgen`); keep them — the SPA's
`requiredResourceAccess` references the scope id.

```bash
SCOPE_ID=$(uuidgen); ROLE_ID=$(uuidgen)
cat > api-app.json <<JSON
{
  "identifierUris": ["api://$API_APP_ID"],
  "api": {
    "requestedAccessTokenVersion": 2,
    "oauth2PermissionScopes": [{
      "id": "$SCOPE_ID", "value": "access_as_user", "type": "User", "isEnabled": true,
      "adminConsentDisplayName": "Access FoundryGate API as the signed-in user",
      "adminConsentDescription": "Allows the app to call the FoundryGate API on behalf of the signed-in user.",
      "userConsentDisplayName": "Access FoundryGate API on your behalf",
      "userConsentDescription": "Allows the app to call the FoundryGate API on your behalf."
    }]
  },
  "appRoles": [{
    "id": "$ROLE_ID", "value": "FoundryGate.Admin", "displayName": "FoundryGate Admin",
    "description": "Administers FoundryGate: users, groups, quotas, keys, configuration.",
    "allowedMemberTypes": ["User"], "isEnabled": true
  }]
}
JSON
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$API_OBJ_ID" \
  --headers "Content-Type=application/json" --body @api-app.json
```

`requestedAccessTokenVersion: 2` matters: the API validates v2.0 tokens, and a v1 token
carries the wrong `iss` and no `oid` in the short claim form. It also decides the audience —
a v2 token's `aud` is the bare client id, **not** `api://{clientId}`, which is what
`AzureAd__Audience` is set to. The API therefore accepts both forms
([#102](https://github.com/kolatts/foundry-gate/issues/102)); do not narrow it to one.

An app role is only assignable once a **service principal** exists for the app:

```bash
API_SP_ID=$(az ad sp create --id "$API_APP_ID" --query id -o tsv)
```

## 2. The SPA app registration

```bash
WEB_APP_ID=$(az ad app create --display-name "FoundryGate.Web ($ENV)" \
  --sign-in-audience AzureADMyOrg --query appId -o tsv)
WEB_OBJ_ID=$(az ad app show --id "$WEB_APP_ID" --query id -o tsv)
```

Blazor WASM signs in with MSAL.js, so the redirect URIs go on the **`spa`** platform
(not `web`, not `publicClient`) — a `web` redirect URI rejects the PKCE flow with
`AADSTS9002326`. The callback path is fixed by
`RemoteAuthenticatorView` in `src/FoundryGate.Web/Pages/Authentication.razor`
(`@page "/authentication/{action}"`), so it is always
`{origin}/authentication/login-callback`. Local dev ports come from
`Properties/launchSettings.json`.

```bash
cat > web-app.json <<JSON
{
  "spa": { "redirectUris": [
    "http://localhost:5276/authentication/login-callback",
    "https://localhost:7245/authentication/login-callback"
  ]},
  "requiredResourceAccess": [
    { "resourceAppId": "$API_APP_ID",
      "resourceAccess": [{ "id": "$SCOPE_ID", "type": "Scope" }] },
    { "resourceAppId": "00000003-0000-0000-c000-000000000000",
      "resourceAccess": [{ "id": "e1fe6dd8-ba31-4d61-89e7-88639da4683d", "type": "Scope" }] }
  ]
}
JSON
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$WEB_OBJ_ID" \
  --headers "Content-Type=application/json" --body @web-app.json
```

`e1fe6dd8-…` is Graph's delegated `User.Read`. Pre-authorize the SPA on the API so users
never see a second consent prompt for a first-party pair:

```bash
cat > preauth.json <<JSON
{ "api": { "preAuthorizedApplications": [
  { "appId": "$WEB_APP_ID", "delegatedPermissionIds": ["$SCOPE_ID"] } ]}}
JSON
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$API_OBJ_ID" \
  --headers "Content-Type=application/json" --body @preauth.json

az ad sp create --id "$WEB_APP_ID"
az ad app permission admin-consent --id "$WEB_APP_ID"
```

The SWA hostname is not known until the first deploy has created the Static Web App, so
its redirect URI is added afterwards — see [step 8](#8-after-the-first-deploy).

### Grant yourself the admin role

Without this the UI signs in and then 403s on every admin route.

```bash
USER_OID=$(az ad signed-in-user show --query id -o tsv)
az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$API_SP_ID/appRoleAssignedTo" \
  --headers "Content-Type=application/json" \
  --body "{\"principalId\":\"$USER_OID\",\"resourceId\":\"$API_SP_ID\",\"appRoleId\":\"$ROLE_ID\"}"
```

### Nothing gets hardcoded into the repo

`src/FoundryGate.Web/wwwroot/appsettings.json` keeps its zero-GUID placeholders — do not
edit it. `_deploy-ui.yml` rewrites the **published** copy after `dotnet publish`, from
four sources:

| Key it writes | Where the value comes from |
|---|---|
| `AzureAd.Authority` | `vars.AZURE_TENANT_ID` (so this variable is not optional — sign-in breaks without it) |
| `AzureAd.ClientId` | `vars.FG_ENTRA_WEB_CLIENT_ID` |
| `Api.Scopes` | `api://{vars.FG_ENTRA_API_CLIENT_ID}/access_as_user` |
| `Api.BaseUrl` | the infra output `containerAppFqdn` — **not** a variable |

`FG_API_BASE_URL` is a different thing: only the SWA *preview* job reads it, because a
preview makes no subscription-scope call and so cannot resolve the FQDN from deployment
outputs. Unset `FG_ENTRA_*` degrade to zero GUIDs with a `::warning::` rather than
failing the deploy, so the pipeline can be proven before the app registrations exist. One
repo serves every environment, and a fork never inherits someone else's tenant.

## 3. The SQL admin group

Azure SQL is Entra-only (`azureADOnlyAuthentication: true`) — no SQL login exists, so
whoever deploys the dacpac must be inside the server's Entra administrator group.

```bash
SQL_GROUP_ID=$(az ad group create \
  --display-name "SG_FOUNDRYGATE_SQL_ADMINS" \
  --mail-nickname "SG_FOUNDRYGATE_SQL_ADMINS" \
  --description "Azure SQL Entra administrators for FoundryGate $ENV." \
  --query id -o tsv)
az ad group member add --group "$SQL_GROUP_ID" --member-id "$USER_OID"
```

Put its object id and name in `infra/parameters/$ENV.bicepparam`
(`sqlAdminGroupObjectId` / `sqlAdminGroupName`). The CI deploy principal joins it in the
next step.

## 4. The three CI identities

One per trust boundary, deliberately — see
[CI/CD reference](/foundry-gate/reference/ci-cd/) for why the PR-track jobs cannot share
the deploy identity.

| App registration | Federated subjects | RBAC |
|---|---|---|
| `foundrygate-ci-$ENV` | `environment:$ENV`, `environment:$ENV-destroy` | **Owner** on the subscription |
| `foundrygate-ci-$ENV-plan` | `environment:$ENV-plan` | **Reader** — but see the warning below; this identity is currently unused |
| `foundrygate-ci-ui-preview` | `environment:ui-preview` | custom SWA role, granted after the first deploy |

:::caution[The PR-track what-if identity does not currently work — #229]
`az deployment sub what-if` reads as a read-only operation and is documented across this
repo as one, but ARM runs its **full preflight**, which authorizes every resource in the
template *as a write*. A Reader identity fails with one `Authorization failed for template
resource …/write` per resource — thirteen of them for this template. The narrowest
identity that can what-if `main.bicep` is roughly `Contributor`, which is exactly the blast
radius a separate PR-track identity exists to avoid.

So create the registration and its federated credential if you like, but leave
`dev-plan`'s three `AZURE_*` variables **unset**: `_deploy-infra.yml` then skips the job
with a `::notice::` instead of failing every PR. #229 carries the options and is waiting on
a decision.
:::

```bash
for n in ci-$ENV ci-$ENV-plan ci-ui-preview; do
  APP_ID=$(az ad app create --display-name "foundrygate-$n" \
    --sign-in-audience AzureADMyOrg --query appId -o tsv)
  az ad sp create --id "$APP_ID"
  echo "foundrygate-$n $APP_ID"
done
```

A federated credential per GitHub Environment — the subject **is** the Environment name,
so an identity can only be minted a token by a job that declares that Environment:

```bash
az ad app federated-credential create --id "<appObjectId>" --parameters '{
  "name": "foundrygate-<env>",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:kolatts/foundry-gate:environment:<env>",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

`dev` and `dev-destroy` share the deploy identity, which therefore carries two federated
credentials. Then the role assignments — note `--assignee-object-id` with
`--assignee-principal-type ServicePrincipal`, which skips a Graph lookup that
intermittently races a just-created principal:

```bash
az role assignment create --assignee-object-id "<ci-dev SP object id>" \
  --assignee-principal-type ServicePrincipal --role Owner \
  --scope "/subscriptions/$SUB_ID"
az role assignment create --assignee-object-id "<ci-dev-plan SP object id>" \
  --assignee-principal-type ServicePrincipal --role Reader \
  --scope "/subscriptions/$SUB_ID"
```

Owner (rather than Contributor + User Access Administrator) because `main.bicep` is
subscription-scope and writes both role assignments and a custom role definition.

Finally, the deploy identity joins the SQL admin group:

```bash
az ad group member add --group "$SQL_GROUP_ID" --member-id "<ci-dev SP object id>"
```

:::note
`az ad group member list` reads `/members/microsoft.graph.user` and therefore never shows
a service principal member. Verify from the principal's side instead:
`az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/<spObjectId>/memberOf"`.
:::

## 5. GitHub Environment variables

The Environments themselves must already exist (`gh api -X PUT
repos/$REPO/environments/dev`) — a variable POST against a missing Environment 404s. This
repo's six were created earlier; a fork creates its own.

```bash
set-var() { # env name value — create or update, whichever applies
  if gh api "repos/$REPO/environments/$1/variables/$2" >/dev/null 2>&1; then
    gh api -X PATCH "repos/$REPO/environments/$1/variables/$2" -f name="$2" -f value="$3"
  else
    gh api -X POST "repos/$REPO/environments/$1/variables" -f name="$2" -f value="$3"
  fi
}

for e in dev dev-destroy; do
  set-var "$e" AZURE_CLIENT_ID        "<foundrygate-ci-$ENV client id>"
  set-var "$e" AZURE_TENANT_ID        "$TENANT_ID"
  set-var "$e" AZURE_SUBSCRIPTION_ID  "$SUB_ID"
  set-var "$e" FG_ENTRA_API_CLIENT_ID "$API_APP_ID"
  set-var "$e" FG_ENTRA_WEB_CLIENT_ID "$WEB_APP_ID"
done

for v in AZURE_CLIENT_ID:"<ui-preview client id>" AZURE_TENANT_ID:"$TENANT_ID" \
         AZURE_SUBSCRIPTION_ID:"$SUB_ID" FG_STATIC_WEB_APP_NAME:stapp-foundrygate-$ENV \
         FG_RESOURCE_GROUP:rg-foundrygate-$ENV FG_ENTRA_API_CLIENT_ID:"$API_APP_ID" \
         FG_ENTRA_WEB_CLIENT_ID:"$WEB_APP_ID"; do
  set-var ui-preview "${v%%:*}" "${v#*:}"
done
```

`dev-plan` gets **nothing** — see the caution in step 4. `ui-preview` also needs
`FG_API_BASE_URL`, which only exists after the deploy (step 8).

The `if`/`else` is deliberate rather than `&& … || …`: with the short-circuit form, a
PATCH that fails for any reason (rate limit, transient 5xx) falls through to a POST that
then 409s on the variable that already exists.

Leave `dev-plan` and `ui-preview` with **no** required reviewers and **no** branch policy —
both exist to serve PR-track jobs on unprotected branches, and a protection rule on either
silently stops the thing it protects. `dev-destroy` is the opposite case and does need its
gate (required reviewer + 5 minute wait): it carries the same subscription-**Owner**
identity as `dev`, pointed at `infra-destroy.yml`.

Read them back with
`gh api repos/$REPO/environments/dev/variables --jq '.variables[] | "\(.name)=\(.value)"'`.

## 6. Graph app roles for the managed identities

:::note
`id-foundrygate-api-$ENV` and `id-foundrygate-func-$ENV` do not exist until the first
deploy has created them, so this step runs **after step 7**, alongside step 8. It is
written here because it belongs with the other identity work.
:::

Managed identities are service principals with no
application object, so `az ad app permission` does not apply; assign the app roles on the
Graph service principal directly.

```bash
GRAPH_SP=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)
for MI in id-foundrygate-api-$ENV id-foundrygate-func-$ENV; do
  MI_SP=$(az ad sp list --display-name "$MI" --query "[0].id" -o tsv)
  for ROLE in Application.Read.All User.Read.All GroupMember.ReadBasic.All; do
    ROLE_APP_ID=$(az ad sp show --id "$GRAPH_SP" \
      --query "appRoles[?value=='$ROLE' && contains(allowedMemberTypes,'Application')].id | [0]" -o tsv)
    az rest --method POST \
      --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$MI_SP/appRoleAssignments" \
      --headers "Content-Type=application/json" \
      --body "{\"principalId\":\"$MI_SP\",\"resourceId\":\"$GRAPH_SP\",\"appRoleId\":\"$ROLE_APP_ID\"}"
  done
done
```

Both identities, not just the API's: `EntraSyncFunction` calls Graph as the *Functions*
identity, so granting only the API's produces a nightly job that fails every run with
`Authorization_RequestDenied`.

Turning the feature on (`Entra__Enabled=true` on both hosts) stays a deliberate human
decision after the grants land — it starts writing to the directory-derived user roster.

## 7. The day-0 deploy

```bash
gh workflow run deploy-all.yml -f environment=dev \
  -f create-model-deployments=true -f run-seed-test=true
```

`create-model-deployments=true` **once, ever**. Anthropic (Claude) deployments are
create-once under ARM: a re-PUT of an existing one drives it to `Failed`, and a
delete/recreate cycle can wedge the subscription's Marketplace agreement
(`fable-refactor-log.md` E-007). Every later run leaves it `false`.

## 8. After the first deploy

Four things need values only the deploy can produce.

```bash
SWA_HOST=$(az deployment sub show -n "foundrygate-$ENV" \
  --query properties.outputs.staticWebAppHostname.value -o tsv)
API_FQDN=$(az deployment sub show -n "foundrygate-$ENV" \
  --query properties.outputs.containerAppFqdn.value -o tsv)
SWA_ROLE_ID=$(az deployment sub show -n "foundrygate-$ENV" \
  --query properties.outputs.swaPreviewRoleDefinitionId.value -o tsv)
SWA_ROLE_SCOPE=$(az deployment sub show -n "foundrygate-$ENV" \
  --query properties.outputs.swaPreviewRoleAssignableScope.value -o tsv)
```

Spelled out rather than folded into one reusable command string: the compact version
depends on bash word-splitting to glue the `--query` path onto the command, which zsh —
the default shell on macOS — does not do, leaving all four variables silently empty. And
`SWA_ROLE_ID`, not `ROLE_ID`: that name is already the `FoundryGate.Admin` app-role GUID
from step 1, and reusing it breaks the app-role grant for anyone working this page
top-to-bottom in one shell.

1. **SPA redirect URI** for the real hostname — append, do not replace, or local dev
   sign-in breaks:

   ```bash
   az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$WEB_OBJ_ID" \
     --headers "Content-Type=application/json" \
     --body "{\"spa\":{\"redirectUris\":[\"http://localhost:5276/authentication/login-callback\",\"https://localhost:7245/authentication/login-callback\",\"https://$SWA_HOST/authentication/login-callback\"]}}"
   ```

2. **The SWA preview role assignment.** No built-in Azure role grants any
   `Microsoft.Web/staticSites` action, so `infra/modules/swa-preview-role.bicep` defines
   a custom one scoped to exactly that Static Web App:

   ```bash
   az role assignment create \
     --assignee-object-id "<ui-preview SP object id>" \
     --assignee-principal-type ServicePrincipal \
     --role "$SWA_ROLE_ID" --scope "$SWA_ROLE_SCOPE"
   ```

3. **`FG_API_BASE_URL`** on `ui-preview` — `set-var ui-preview FG_API_BASE_URL
   "https://$API_FQDN/api/v1/"`. The preview jobs make no subscription-scope call by
   design, so they cannot read it from the outputs.

4. **The managed identities' Graph app roles** — step 6 above, which could not run before
   the identities existed.

## 9. Calling the deployed API from a terminal

Useful for smoke-testing a deploy, and required by the token-gated half of
`FoundryGate.Tests.Postdeployment` ([#102](https://github.com/kolatts/foundry-gate/issues/102)).
Pre-authorize the Azure CLI's own public client for the API's scope, once per environment,
so no consent prompt stands between you and a token:

```bash
CLI_APP_ID=04b07795-8ddb-461a-bbee-02f9e1bf7b46   # Microsoft Azure CLI, the same in every tenant
az rest --method PATCH \
  --uri "https://graph.microsoft.com/v1.0/applications/$API_OBJ_ID" \
  --headers "Content-Type=application/json" \
  --body "{\"api\":{\"preAuthorizedApplications\":[{\"appId\":\"$WEB_APP_ID\",\"delegatedPermissionIds\":[\"$SCOPE_ID\"]},{\"appId\":\"$CLI_APP_ID\",\"delegatedPermissionIds\":[\"$SCOPE_ID\"]}]}}"
```

`preAuthorizedApplications` is replaced wholesale, so list the SPA alongside the CLI or the
UI loses its consent-free sign-in.

Then:

```bash
TOKEN=$(az account get-access-token \
  --scope "api://$API_APP_ID/access_as_user" --query accessToken -o tsv)
curl -s -H "Authorization: Bearer $TOKEN" "https://$API_FQDN/api/v1/users/me"
```

Use `--scope`, not `--resource`: `--resource api://…` asks the CLI for a token it holds no
refresh token for and fails with `Status_InteractionRequired`, telling you to run
`az login --scope …`. The `--scope` form succeeds against the session you already have.

To run the postdeployment auth tests:

```bash
export FG_API_BASE_URL="https://$API_FQDN"
export FG_ADMIN_TOKEN="$TOKEN"
dotnet test src/FoundryGate.Tests.Postdeployment
```

`FG_NONADMIN_TOKEN` is the same thing for a principal **without** the `FoundryGate.Admin`
app-role assignment — a second tenant account, or the same account with the assignment
temporarily removed. A test whose token is unset reports as *skipped*, never as passed.
