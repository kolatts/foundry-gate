# Database tooling — SQL Server project, schema comparison, and CLI

> GitHub: #76  
> Milestone: v0.1 — Foundation  
> Labels: epic, backend

## Overview
FoundryGate uses a hybrid schema management approach borrowed from imagile-app: EF Core migrations are the developer-facing workflow (fast iteration, `dotnet ef migrations add`), but the canonical schema artifact is a `.sqlproj` file that DacFx can build into a dacpac. A `FoundryGate.Cli` dotnet tool ties it together — `foundrygate db compare` runs a DacFx schema comparison between the local database (kept current by EF migrations) and the `.sqlproj` files, then publishes any delta back into the project's SQL files. CI builds the dacpac and the CLI deploys it. This gives the precision of dacpac-based deployments without abandoning EF's migration ergonomics.

---

## Project structure additions

```
src/
  FoundryGate.Database/              # .sqlproj — schema source of truth
    FoundryGate.Database.sqlproj     # SDK: Microsoft.Build.Sql/2.0.0
    dbo/Tables/
      Users.sql
      Groups.sql
      GroupMembers.sql
      QuotaAllocations.sql
      QuotaIncreaseRequests.sql
      SystemConfiguration.sql
      AuditLog.sql

  FoundryGate.Cli/                   # dotnet tool — packable
    FoundryGate.Cli.csproj           # PackAsTool: true, ToolCommandName: foundrygate
    Program.cs
    Commands/
      Db/
        DbCommand.cs
        Compare/CompareCommand.cs    # schema diff local DB → .sqlproj
        Deploy/DeployCommand.cs      # dacpac deploy to target SQL Server
        Seed/SeedCommand.cs          # run SystemConfiguration + test data seeder
      Local/
        Setup/SetupCommand.cs        # create local DB, migrate, compare, seed
    Helpers/
      SchemaComparisonHelpers.cs     # DacFx SchemaComparison wrapper
      DatabaseHelpers.cs
```

---

## Approach

### Add FoundryGate.Database .sqlproj and populate initial SQL files (#77)
Create `src/FoundryGate.Database/FoundryGate.Database.sqlproj` using SDK `Microsoft.Build.Sql/2.0.0` targeting `SqlAzureV12DatabaseSchemaProvider`. Add one `.sql` file per table under `dbo/Tables/` matching the EF entities defined in epic #2 exactly (column names, types, nullability, and indexes). The `.sqlproj` is the schema source of truth for dacpac generation — it must stay in sync with EF migrations via the `foundrygate db compare` workflow, not be edited by hand. Add the project to `FoundryGate.sln`. The dacpac output goes to `artifacts/FoundryGate.Database.dacpac` for the CI pipeline to consume.

Files expected to be created or modified:
- `src/FoundryGate.Database/FoundryGate.Database.sqlproj`
- `src/FoundryGate.Database/dbo/Tables/Users.sql`
- `src/FoundryGate.Database/dbo/Tables/Groups.sql`
- `src/FoundryGate.Database/dbo/Tables/GroupMembers.sql`
- `src/FoundryGate.Database/dbo/Tables/QuotaAllocations.sql`
- `src/FoundryGate.Database/dbo/Tables/QuotaIncreaseRequests.sql`
- `src/FoundryGate.Database/dbo/Tables/SystemConfiguration.sql`
- `src/FoundryGate.Database/dbo/Tables/AuditLog.sql`
- `FoundryGate.sln`

### Add FoundryGate.Cli dotnet tool with db compare, deploy, and seed commands (#78)
Create `src/FoundryGate.Cli/FoundryGate.Cli.csproj` as a packable dotnet tool (`PackAsTool: true`, `ToolCommandName: foundrygate`). Reference `FoundryGate.Data` for DbContext access. Use `Spectre.Console.Cli` for command structure and `Spectre.Console` for output. Key packages: `Microsoft.SqlServer.DacFx`, `LibGit2Sharp`.

**`foundrygate db compare`** (#78-compare)
The core command. Uses DacFx `SchemaComparison` API:
```
source → SchemaCompareDatabaseEndpoint(localConnectionString)
target → SchemaCompareProjectEndpoint(path/to/FoundryGate.Database.sqlproj)
comparison.Compare()
comparison.PublishChangesToProject()
```
After publishing, use `LibGit2Sharp` to check the diff — discard any changes that are purely column reordering with no semantic difference (same pattern as imagile-app `SchemaComparisonHelpers`). Print a summary table via `Spectre.Console` showing each changed object and its type.

> **Windows-only**: DacFx schema comparison is Windows-only due to native SQL Server tooling dependencies. The compare command must check the OS and exit gracefully on Linux/macOS with a clear error message pointing to the Windows requirement.

**`foundrygate db deploy`** (#78-deploy)
Uses `DacServices` to deploy the dacpac to a target connection string:
```
var dacpac = DacPackage.Load("artifacts/FoundryGate.Database.dacpac");
var services = new DacServices(connectionString);
services.Deploy(dacpac, databaseName, upgradeExisting: true, options: new DacDeployOptions {
    BlockOnPossibleDataLoss = true,
    GenerateSmartDefaults = true,
    DropObjectsNotInSource = dropObjects,   // --drop flag
    ExcludeObjectTypes = [ObjectType.Users]
});
```
Supports both SQL auth (connection string with `User ID`) and Entra/Managed Identity (via `DefaultAzureCredential` token provider injected into `DacServices`).

**`foundrygate db seed`** (#78-seed)
Runs `IFoundryGateSeeder` which inserts the eight `SystemConfiguration` rows (idempotent — upsert by key). Accepts `--env` flag: `local` seeds with localhost defaults; `dev`/`prod` seeds placeholder values that operators update via the admin UI.

**`foundrygate local setup`** (#78-setup)
One-command local dev bootstrap:
1. Check Docker is running; start `mcr.microsoft.com/mssql/server:2022-latest` on port 1433 if not present
2. Wait for SQL Server to be ready (retry loop)
3. Run `dotnet ef database update` against `FoundryGate.Data` (applies all migrations)
4. Run `foundrygate db compare` to sync EF migration output → .sqlproj
5. Run `foundrygate db seed --env local`
6. Print connection string for `appsettings.Development.json`

Files expected to be created or modified:
- `src/FoundryGate.Cli/FoundryGate.Cli.csproj`
- `src/FoundryGate.Cli/Program.cs`
- `src/FoundryGate.Cli/Commands/Db/Compare/CompareCommand.cs`
- `src/FoundryGate.Cli/Commands/Db/Deploy/DeployCommand.cs`
- `src/FoundryGate.Cli/Commands/Db/Seed/SeedCommand.cs`
- `src/FoundryGate.Cli/Commands/Local/Setup/SetupCommand.cs`
- `src/FoundryGate.Cli/Helpers/SchemaComparisonHelpers.cs`
- `src/FoundryGate.Cli/Helpers/DatabaseHelpers.cs`
- `FoundryGate.sln`

---

## Developer workflow (day-to-day)

```
# Add a new column to the User entity
dotnet ef migrations add AddUserDisplayNameColumn \
  --project src/FoundryGate.Data \
  --startup-project src/FoundryGate.Api

# Apply to local DB
dotnet ef database update \
  --project src/FoundryGate.Data \
  --startup-project src/FoundryGate.Api

# Sync EF changes back to the .sqlproj
foundrygate db compare

# git diff — review the generated .sql file change
# Commit both the migration file and the .sql file together
git add src/FoundryGate.Data/Migrations/ src/FoundryGate.Database/dbo/Tables/
git commit -m "feat(data): add DisplayName column to Users"
```

---

## CI integration

The `api-deploy.yml` and `infra-destroy.yml` pipelines gain a `build-dacpac` job:

```yaml
build-dacpac:
  runs-on: windows-latest        # DacFx build requires Windows
  steps:
    - uses: actions/checkout@v4
    - name: Install SqlPackage
      run: dotnet tool install --global microsoft.sqlpackage
    - name: Build dacpac
      run: dotnet build src/FoundryGate.Database/FoundryGate.Database.sqlproj -o artifacts
    - uses: actions/upload-artifact@v4
      with:
        name: dacpac
        path: artifacts/FoundryGate.Database.dacpac
```

A separate `db-deploy.yml` workflow (reusable, `workflow_call`) downloads the dacpac artifact and runs `foundrygate db deploy` against the target environment.

---

## Verification
- [ ] `dotnet build src/FoundryGate.Database` produces a `.dacpac` file
- [ ] `foundrygate local setup` creates a local SQL Server container, applies migrations, and seeds
- [ ] `foundrygate db compare` on a clean repo produces no changes (schema already in sync)
- [ ] Adding a new EF migration and running `foundrygate db compare` updates the corresponding `.sql` file
- [ ] `foundrygate db compare` on Linux exits with a clear "Windows-only" error message
- [ ] `foundrygate db deploy` successfully deploys to a local SQL Server instance
- [ ] `foundrygate db deploy` blocks on possible data loss when `--drop` is not specified
