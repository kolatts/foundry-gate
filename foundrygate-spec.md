# FoundryGate — Claude Code Handoff Specification

> **FoundryGate** is an open-source token budget management system for Azure AI Foundry and
> Azure API Management. It provides per-developer and per-group monthly token quotas, Entra ID
> user sync, quota increase approvals, APIM subscription key lifecycle management, and a
> monthly hard reset. Designed to be forked and configured against any Azure tenant.

---

## 1. Repository Structure

```
foundrygate/
├── .github/
│   └── workflows/
│       ├── api.yml                  # Build + deploy API to Container Apps
│       ├── ui.yml                   # Build + deploy UI to Static Web Apps
│       └── infra.yml                # Bicep what-if + deploy
├── infra/
│   ├── main.bicep                   # Root orchestrator
│   ├── modules/
│   │   ├── sql.bicep
│   │   ├── containerapp.bicep
│   │   ├── staticwebapp.bicep
│   │   ├── apim.bicep
│   │   └── foundry.bicep
│   └── parameters/
│       ├── dev.bicepparam
│       └── prod.bicepparam
├── src/
│   ├── FoundryGate.Api/             # .NET 9 Web API (ASP.NET Core)
│   ├── FoundryGate.Data/            # EF Core + Azure SQL
│   ├── FoundryGate.Domain/          # DTOs, enums shared between API and UI
│   └── FoundryGate.Web/             # Blazor WASM frontend
├── docs/
│   ├── architecture.md
│   ├── configuration.md
│   └── fork-guide.md
├── foundrygate.sln
└── README.md
```

---

## 2. Technology Stack

| Layer | Technology |
|---|---|
| Frontend | Blazor WebAssembly (.NET 10), hosted on Azure Static Web Apps |
| UI Components | MudBlazor |
| API | ASP.NET Core 10 Web API, hosted on Azure Container Apps |
| Database | Azure SQL, accessed via Entity Framework Core 10 |
| Auth | Microsoft Entra ID (MSAL, Microsoft.Identity.Web) |
| Background jobs | Azure Functions (.NET 10 isolated worker, Flex Consumption) |
| Docs site | Astro + Starlight, deployed to GitHub Pages |
| Infra-as-Code | Bicep + GitHub Actions (OIDC, no long-lived secrets) |
| APIM integration | Azure SDK for .NET (`Azure.ResourceManager.ApiManagement`) |
| Foundry provisioning | Azure SDK for .NET (`Azure.ResourceManager.CognitiveServices`) |
| Entra sync | Microsoft Graph SDK (`Microsoft.Graph`) |

---

## 3. Domain Model

### 3.1 Entities (EF Core)

```
User
  Id                    guid PK
  EntraObjectId         string UNIQUE NOT NULL   -- Entra object ID
  EmployeeId            string NULL              -- HR employee ID (from Entra)
  DisplayName           string NOT NULL
  Email                 string NOT NULL
  IsActive              bool NOT NULL DEFAULT true
  IsUnlimited           bool NOT NULL DEFAULT false
  MonthlyTokenQuota     long NULL                -- NULL = use system default; ignored if IsUnlimited
  ApimSubscriptionId    string NULL              -- APIM subscription resource ID
  ApimSubscriptionKey   string NULL              -- encrypted at rest
  CreatedAt             datetimeoffset NOT NULL
  LastSyncedAt          datetimeoffset NULL
  Groups                ICollection<GroupMember>

Group
  Id                    guid PK
  Name                  string NOT NULL
  Description           string NULL
  EntraGroupId          string NULL              -- if linked to an Entra group
  IsEntraSynced         bool NOT NULL DEFAULT false
  MonthlyTokenQuota     long NULL                -- overrides system default for members
  IsUnlimited           bool NOT NULL DEFAULT false
  CreatedAt             datetimeoffset NOT NULL
  Members               ICollection<GroupMember>

GroupMember
  GroupId               guid FK
  UserId                guid FK
  AddedAt               datetimeoffset NOT NULL
  AddedBy               guid FK -> User          -- admin who added

QuotaAllocation                                  -- resolved quota per user per month
  Id                    guid PK
  UserId                guid FK NOT NULL
  PeriodYear            int NOT NULL
  PeriodMonth           int NOT NULL             -- 1-12
  AllocatedTokens       long NULL                -- NULL = unlimited
  TokensUsed            long NOT NULL DEFAULT 0
  IsHardStopped         bool NOT NULL DEFAULT false
  ResetAt               datetimeoffset NULL      -- when last reset
  UNIQUE (UserId, PeriodYear, PeriodMonth)

QuotaIncreaseRequest
  Id                    guid PK
  UserId                guid FK NOT NULL
  RequestedBy           guid FK -> User          -- may be the user themselves or an admin
  PeriodYear            int NOT NULL
  PeriodMonth           int NOT NULL
  CurrentQuota          long NULL
  RequestedQuota        long NULL                -- NULL = requesting unlimited
  Justification         string NOT NULL
  Status                enum: Pending | Approved | Rejected
  ReviewedBy            guid FK -> User NULL
  ReviewedAt            datetimeoffset NULL
  ReviewNotes           string NULL
  CreatedAt             datetimeoffset NOT NULL

SystemConfiguration
  Key                   string PK
  Value                 string NOT NULL
  UpdatedAt             datetimeoffset NOT NULL
  UpdatedBy             guid FK -> User

  -- Seeded keys:
  -- "DefaultMonthlyTokenQuota"   e.g. "1000000"
  -- "ApimResourceId"             Azure resource ID of the APIM instance
  -- "FoundryResourceId"          Azure resource ID of the Foundry account
  -- "EntraTenantId"
  -- "EntraGroupSyncEnabled"      "true" | "false"
  -- "ResetDayOfMonth"            "1" (always 1 for hard zero)

AuditLog
  Id                    guid PK
  ActorUserId           guid FK NULL
  Action                string NOT NULL          -- e.g. "quota.approved", "key.rotated"
  TargetType            string NULL              -- "User" | "Group" | "Request"
  TargetId              string NULL
  Details               string NULL              -- JSON blob
  OccurredAt            datetimeoffset NOT NULL
```

### 3.2 Quota Resolution Logic

Resolved in this precedence order (highest wins):

1. `User.IsUnlimited = true` → unlimited, skip everything
2. `User.MonthlyTokenQuota` is set → use that value
3. User belongs to a Group where `Group.IsUnlimited = true` → unlimited
4. User belongs to Groups with `MonthlyTokenQuota` set → use the **highest** group quota
5. Fall back to `SystemConfiguration["DefaultMonthlyTokenQuota"]`

This resolution runs at the start of each month reset and when an admin changes a quota.
The result is written to `QuotaAllocation` for the current period.

---

## 4. API Surface

Base path: `/api/v1`

Authentication: all endpoints require a valid Entra ID bearer token.
Authorization: `[Authorize(Roles = "FoundryGate.Admin")]` on admin-only endpoints.
The admin role is an Entra App Role defined in the app registration.

### 4.1 Users

```
GET    /users                        Admin: list all users (paged)
GET    /users/me                     Any: get own profile, quota, key info
GET    /users/{id}                   Admin: get user detail
PUT    /users/{id}/quota             Admin: set user quota or unlimited flag
PUT    /users/{id}/activate          Admin: activate user
PUT    /users/{id}/deactivate        Admin: deactivate user (revokes APIM key)
POST   /users/sync                   Admin: trigger Entra pull sync
```

### 4.2 Groups

```
GET    /groups                       Admin: list all groups
POST   /groups                       Admin: create group
GET    /groups/{id}                  Admin: get group detail + members
PUT    /groups/{id}                  Admin: update group name/description/quota
DELETE /groups/{id}                  Admin: delete group
POST   /groups/{id}/members          Admin: add user to group
DELETE /groups/{id}/members/{userId} Admin: remove user from group
POST   /groups/sync-entra            Admin: sync members from linked Entra group
```

### 4.3 Quota

```
GET    /quota/allocations            Admin: list current period allocations (paged)
GET    /quota/allocations/me         Any: get own current allocation
GET    /quota/allocations/{userId}   Admin: get specific user current allocation
POST   /quota/reset                  Admin: manually trigger monthly reset (idempotent)
```

### 4.4 Quota Increase Requests

```
GET    /requests                     Admin: list all requests; Any: list own requests
POST   /requests                     Any: submit a quota increase request
GET    /requests/{id}                Admin or request owner
PUT    /requests/{id}/approve        Admin only
PUT    /requests/{id}/reject         Admin only
```

### 4.5 Keys

```
GET    /keys/me                      Any: get own APIM key (masked except last 4)
POST   /keys/me/rotate               Any: rotate own APIM subscription key
POST   /keys/{userId}/rotate         Admin: rotate key for any user
POST   /keys/{userId}/provision      Admin: provision a new APIM key for a user
DELETE /keys/{userId}                Admin: revoke APIM key (hard deactivate)
```

### 4.6 Admin / Configuration

```
GET    /config                       Admin: get all system configuration keys
PUT    /config/{key}                 Admin: update a configuration value
GET    /audit                        Admin: list audit log (paged, filterable)
GET    /dashboard                    Admin: summary stats (total users, active, top consumers)
```

---

## 5. APIM Integration

The API talks directly to the Azure APIM Management plane using the Azure SDK with a
Managed Identity credential (Container App system-assigned identity).

### 5.1 Key provisioning flow

```
1. Admin activates a user or user auto-provisions on first login
2. API calls APIM Management: POST /subscriptions
   - Name: "foundrygate-{userId}"
   - Scope: /products/{productId}  (a single APIM Product covering all Foundry routes)
   - DisplayName: user's email
3. APIM returns primary + secondary keys
4. API encrypts the primary key with Azure Key Vault and stores in User.ApimSubscriptionKey
5. User.ApimSubscriptionId is set to the APIM subscription resource ID
```

### 5.2 Key rotation flow

```
1. User or admin calls POST /keys/{userId}/rotate
2. API calls APIM Management: POST /subscriptions/{sid}/regeneratePrimaryKey
3. API fetches new primary key, re-encrypts, updates DB
4. Old key is immediately invalidated by APIM
5. Audit log entry written
```

### 5.3 Key revocation flow

```
1. Admin deactivates user or DELETE /keys/{userId}
2. API calls APIM Management: DELETE /subscriptions/{sid}
3. User.ApimSubscriptionId and User.ApimSubscriptionKey set to NULL
4. User.IsActive set to false
5. QuotaAllocation.IsHardStopped set to true for current period
```

### 5.4 Monthly token usage sync

The APIM `llm-emit-token-metric` policy emits token counts to Application Insights.
The API exposes a scheduled endpoint (triggered by a Container App cron job or Timer):

```
POST /internal/sync-usage           Internal only (header key auth, not Entra)
```

This reads from the Application Insights REST API (or Azure Monitor Logs / Log Analytics)
and updates `QuotaAllocation.TokensUsed` for each user by matching the APIM subscription ID.

---

## 6. Monthly Reset

A scheduled job runs at **00:01 UTC on the 1st of each month**.
Implemented as a .NET `IHostedService` / `BackgroundService` in the API container.

Reset logic:

```
1. For each active User:
   a. Resolve quota for the new period using quota resolution logic (Section 3.2)
   b. INSERT QuotaAllocation for (UserId, newYear, newMonth) with TokensUsed = 0
   c. Set IsHardStopped = false
   d. Update APIM token-limit counter-key via APIM policy cache clear
      (APIM monthly quota auto-resets on calendar month boundary — verify this
       matches UTC month start; if not, use APIM Management API to reset the counter)
2. Write audit log entry: "quota.monthly-reset" with count of users reset
```

---

## 7. Entra ID Sync

Pull-only. Uses Microsoft Graph SDK with app-only credentials (client credentials flow,
stored in Key Vault, not in config files).

### 7.1 Auto-provision on first login

```
1. User hits the Blazor UI, MSAL acquires token
2. UI calls GET /users/me
3. API checks DB for User with matching EntraObjectId
4. If not found:
   a. Call Graph GET /users/{oid} to fetch DisplayName, email, employeeId
   b. INSERT User with SystemConfiguration["DefaultMonthlyTokenQuota"]
   c. Provision APIM key (Section 5.1)
   d. INSERT QuotaAllocation for current period
   e. Return new user profile
5. If found: update DisplayName/email if changed, update LastSyncedAt
```

### 7.2 Bulk sync (admin-triggered)

```
POST /users/sync

1. Call Graph GET /users with $select=id,displayName,mail,employeeId
2. For each Entra user:
   a. If exists in DB: update display fields, set LastSyncedAt
   b. If not exists: INSERT with default quota (does NOT auto-provision APIM key —
      key is only provisioned on first actual login or explicit admin action)
3. Users in DB not found in Entra: flag as IsActive=false (do not delete)
4. Return sync summary: { added, updated, deactivated }
```

### 7.3 Entra group sync

```
POST /groups/sync-entra (for a specific group with EntraGroupId set)

1. Call Graph GET /groups/{entraGroupId}/members
2. Resolve to FoundryGate User records by EntraObjectId
3. Add new members, remove departed members from GroupMember table
4. Update group quota allocation for affected users
```

---

## 8. Blazor WASM Frontend

Authentication: MSAL.js via `Microsoft.Authentication.WebAssembly.Msal`.
Routing: role-aware — admin pages hidden from non-admins.

### 8.1 Pages / Routes

```
/                           → redirect to /dashboard (admin) or /me (developer)
/me                         Developer home: quota gauge, key display/rotate, request history
/me/request                 Submit quota increase request
/dashboard                  Admin: summary cards, top consumers, pending requests badge
/users                      Admin: searchable user table
/users/{id}                 Admin: user detail, quota override, key management
/users/sync                 Admin: trigger + view Entra sync results
/groups                     Admin: group list
/groups/new                 Admin: create group
/groups/{id}                Admin: group detail, member list, quota setting
/requests                   Admin: all pending/reviewed requests; Developer: own requests
/requests/{id}              Admin: approve/reject with notes; Developer: view status
/config                     Admin: system configuration key-value editor
/audit                      Admin: audit log viewer with filters
```

### 8.2 Developer "My Account" page (`/me`)

- Quota gauge: tokens used / allocated this month (progress bar, % label)
- "Unlimited" badge if applicable
- APIM subscription key: shown masked (last 4 visible), "Reveal" button (one-time fetch),
  "Rotate Key" button with confirmation modal
- Current period dates (month start / end)
- Request history table: date, requested amount, status, reviewer notes
- "Request Quota Increase" CTA button

### 8.3 Key Components

```
<QuotaGauge>          Radial or linear progress, color-coded (green/amber/red)
<KeyDisplay>          Masked key with reveal + rotate actions
<UserTable>           Sortable, filterable, paged; columns: name, email, quota, used, status
<GroupTable>          Groups with member count, quota, Entra link indicator
<RequestTable>        Pending/approved/rejected with approve/reject inline actions (admin)
<ConfigEditor>        Key-value pairs with edit-in-place
<AuditLogViewer>      Filterable by actor, action, date range
```

---

## 9. Infrastructure (Bicep)

All resources in a single Azure resource group. Parameterised for dev/prod.

### 9.1 Resources

```
Microsoft.Sql/servers                           Azure SQL Server
Microsoft.Sql/servers/databases                 foundrygate-db (General Purpose, S2 or serverless)
Microsoft.App/managedEnvironments               Container Apps environment
Microsoft.App/containerApps                     foundrygate-api
  - system-assigned managed identity
  - min replicas: 1, max: 3
  - ingress: external, port 8080
  - env vars: SQL connection string (Key Vault ref), APIM resource ID, etc.
Microsoft.Web/staticSites                       foundrygate-ui (Static Web Apps, Free tier)
Microsoft.ApiManagement/service                 (existing — param input, not created here)
Microsoft.CognitiveServices/accounts            (existing — param input, not created here)
Microsoft.KeyVault/vaults                       foundrygate-kv
  - secrets: SqlConnectionString, GraphClientSecret, ApimMgmtKey
Microsoft.Insights/components                   Application Insights (shared with APIM)
Microsoft.Authorization/roleAssignments
  - Container App identity → Key Vault Secrets User
  - Container App identity → APIM Contributor (scoped to APIM resource)
  - Container App identity → Monitoring Reader (for App Insights usage sync)
```

### 9.2 Parameters (fork configuration)

```bicep
// infra/parameters/prod.bicepparam
param location = 'eastus2'
param environmentName = 'prod'
param sqlAdminLogin = 'foundrygate-admin'
param apimResourceId = '/subscriptions/.../resourceGroups/.../providers/Microsoft.ApiManagement/service/...'
param foundryResourceId = '/subscriptions/.../resourceGroups/.../providers/Microsoft.CognitiveServices/accounts/...'
param entraClientId = '...'           // App registration client ID
param entraTenantId = '...'           // Azure tenant ID
param apimProductId = 'foundrygate'   // APIM product name scoping the Foundry APIs
```

Secrets (SQL password, Graph client secret) are passed via GitHub Actions secrets,
never in param files.

---

## 10. GitHub Actions Workflows

### 10.1 `infra.yml` — Bicep deployment

```yaml
trigger: push to main, paths: infra/**
permissions: id-token: write, contents: read
steps:
  - azure/login@v2 (OIDC)
  - az deployment group what-if
  - az deployment group create
```

### 10.2 `api.yml` — API deployment

```yaml
trigger: push to main, paths: src/FoundryGate.Api/**, src/FoundryGate.Data/**
steps:
  - dotnet build + test
  - docker build + push to Azure Container Registry
  - az containerapp update --image
```

### 10.3 `ui.yml` — UI deployment

```yaml
trigger: push to main, paths: src/FoundryGate.Web/**
steps:
  - dotnet publish (Blazor WASM release)
  - Azure/static-web-apps-deploy@v1
```

---

## 11. Security Considerations

- **No secrets in repo**: all secrets in Key Vault, referenced via Container App secret bindings
- **APIM keys encrypted**: stored encrypted in SQL using Azure Key Vault key wrapping;
  never returned in full via API (reveal action fetches directly, not stored in browser)
- **Graph credentials**: app-only client credentials in Key Vault; never in appsettings
- **CORS**: Static Web App domain allowlisted in Container App ingress policy
- **SQL**: private endpoint in prod; firewall rule for Container Apps environment IP range
- **Audit log**: all admin actions, key rotations, approvals written to `AuditLog` table
- **Role separation**: `FoundryGate.Admin` app role in Entra; regular users have no role,
  access is scoped to `/me` and `/requests` (own only)

---

## 12. Configuration for Forks

A fork operator needs to supply:

1. **Azure App Registration** with:
   - Redirect URI: Static Web App URL
   - App roles: `FoundryGate.Admin`
   - API permissions: `User.Read.All`, `Group.Read.All`, `GroupMember.Read.All` (Graph, admin consent)
2. **Bicep parameters** for their APIM and Foundry resource IDs, tenant ID, client ID
3. **GitHub secrets**:
   - `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (OIDC)
   - `SQL_ADMIN_PASSWORD`
   - `GRAPH_CLIENT_SECRET`
4. **APIM product** named `foundrygate` (or configured name) covering the Foundry API routes

Full setup guide lives in `docs/fork-guide.md`.

---

## 13. Out of Scope (v1)

- Email / Teams notifications on quota events
- Configurable multi-stage approval workflows (v1: single admin-approval mode only)
- Webhook integrations
- Per-model quota breakdown (all models share one monthly pool per user)
- Self-service group creation by developers
- Billing/chargeback export (audit log can be queried manually)
- Mobile app

---

## 14. Build Order for Claude Code

Implement in this sequence to maintain a working state at each step:

```
Step 1  — Repo scaffold: solution, projects, folder structure, .gitignore, README stub
Step 2  — FoundryGate.Data: EF models, DbContext, migrations, seed data (SystemConfiguration defaults)
Step 3  — FoundryGate.Domain: DTOs, enums, request/response models
Step 4  — FoundryGate.Api: project setup, Entra auth, EF wiring, health endpoint
Step 5  — API: Users endpoints + Entra auto-provision on /users/me
Step 6  — API: Groups endpoints
Step 7  — API: Quota resolution logic + QuotaAllocation endpoints
Step 8  — API: QuotaIncreaseRequest endpoints (submit + approve/reject)
Step 9  — API: APIM key provisioning, rotation, revocation (Azure SDK)
Step 10 — API: Monthly reset background service
Step 11 — API: Entra bulk sync (Graph SDK)
Step 12 — API: Usage sync from Application Insights / Azure Monitor
Step 13 — API: Audit log writes on all mutating actions
Step 14 — Infra: Bicep modules + GitHub Actions workflows
Step 15 — FoundryGate.Web: Blazor WASM scaffold, MSAL auth, routing
Step 16 — UI: /me page (quota gauge, key display/rotate, request history)
Step 17 — UI: /requests pages (submit + status for developers)
Step 18 — UI: Admin pages (users, groups, requests, config, audit)
Step 19 — UI: /dashboard admin summary
Step 20 — Docs: fork-guide.md, architecture.md, configuration.md
```
