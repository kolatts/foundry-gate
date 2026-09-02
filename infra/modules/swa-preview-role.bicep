// Custom RBAC role for the `ui-preview` CI identity that publishes Static Web Apps PR
// previews (#155).
//
// WHY A CUSTOM ROLE. The obvious answer — "give it Static Web App Contributor" — does not
// exist. Verified against the live tenant on 2026-09-02:
//   az role definition list --name "Static Web App Contributor"                    -> []
//   az role definition list --query "[?contains(to_string(permissions[].actions[]),
//                                    'staticSites')].roleName"                     -> []
// No built-in role grants ANY `Microsoft.Web/staticSites` action outside a wildcard `*`, so
// the only built-in that works is Contributor — which on a Static Web App also carries
// Write and Delete, and is exactly the "reach for the big role" outcome the #155 design
// exists to avoid. Hence this: the smallest role that can do the job, and nothing else.
//
// WHAT THE PREVIEW JOB ACTUALLY NEEDS (every action string verified against
// `az provider operation show --namespace Microsoft.Web`, including its odd casing —
// `listsecrets` is lowercase in the provider manifest even though the REST call is
// `listSecrets`):
//   staticSites/Read              `az staticwebapp secrets list` resolves the site first
//   staticSites/listsecrets/action  the deployment token itself
//   staticSites/builds/Read       counting existing preview environments before publishing
//                                 (the Free tier allows three — ui-deploy.yml soft-skips at
//                                 the limit instead of failing after the token fetch)
//   staticSites/builds/Delete     tearing a preview environment down when the PR closes
//
// Deliberately NOT included: `staticSites/Write` and `staticSites/Delete` (the app itself is
// Bicep's), `resetapikey/Action` (a compromised run could otherwise lock the real pipeline
// out), `listappsettings`/`listfunctionappsettings` (app settings are configuration, not
// preview machinery), and every `customdomains`/`privateEndpoint*` action.
//
// NOTE ON THE REMAINING BLAST RADIUS: the deployment token this role hands out is
// APP-scoped, not slot-scoped — see the header of .github/workflows/ui-deploy.yml. Narrowing
// the ARM role does not change that; Azure exposes no slot-scoped SWA credential. This role
// is about what the identity can do through ARM.
//
// ASSIGNMENT IS AN OWNER ACTION (#109): the `ui-preview` app registration does not exist in
// Bicep, so this template defines the role and outputs its id; a human assigns it.
targetScope = 'subscription'

@description('Resource id of the Static Web App the role may be assigned on — also its only assignableScope. Empty is a hard error: a role assignable at subscription scope would defeat the point.')
param staticWebAppId string

@description('Environment name, only used to keep the role display name unique within the tenant (roleName is a tenant-wide unique key).')
param environmentName string

// Bicep has no assert/fail primitive, so this is the loud failure: an empty id makes the
// assignableScopes entry an invalid scope string and ARM rejects the deployment with
// "RoleDefinitionAssignableScopesInvalid" naming this module, rather than silently creating a
// subscription-assignable role.
var assignableScope = empty(staticWebAppId)
  ? 'INVALID-staticWebAppId-was-empty-see-infra/modules/swa-preview-role.bicep'
  : staticWebAppId

resource swaPreviewRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  // Deterministic: re-running the template updates the same definition instead of creating a
  // second one with a new GUID (role definitions are not name-addressable).
  name: guid(subscription().id, 'foundrygate-swa-preview-publisher', environmentName)
  properties: {
    roleName: 'FoundryGate SWA Preview Publisher (${environmentName})'
    description: 'Reads a Static Web App and its deployment token, and lists/deletes its preview (staging) environments. For the ui-preview GitHub Environment identity only (#155). Cannot create, modify or delete the Static Web App itself, and cannot reset its API key.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Web/staticSites/Read'
          'Microsoft.Web/staticSites/listsecrets/action'
          'Microsoft.Web/staticSites/builds/Read'
          'Microsoft.Web/staticSites/builds/Delete'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
    assignableScopes: [
      assignableScope
    ]
  }
}

@description('Role definition resource id — what `az role assignment create --role <id>` takes.')
output roleDefinitionId string = swaPreviewRole.id
output roleDefinitionName string = swaPreviewRole.name
output roleName string = swaPreviewRole.properties.roleName
@description('The single scope this role may be assigned at — the dev Static Web App.')
output assignableScope string = assignableScope
