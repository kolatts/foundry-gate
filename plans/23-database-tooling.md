# Database tooling — SQL Server project, schema comparison, and CLI

> GitHub: #76  
> Milestone: v0.1 — Foundation  
> Labels: epic, backend

## Status update (2026-09-01, #77/#78/#79 implemented)

CONVENTIONS.md's finalized "Schema pipeline" section (written after this plan) supersedes the
`dotnet ef migrations add` / `db compare` workflow described below — **CONVENTIONS.md wins**. The
actual pipeline implemented: EF entities are the schema source of truth → `local setup` runs
`EnsureCreated` against docker SQL → the `dbo/Tables/*.sql` files are hand-authored to match the EF
model (verified by a regex-level Predeployment parity test, since DacFx schema-compare is
Windows-only native tooling and isn't available in every environment these run in) → `.sqlproj`
builds the dacpac → CI/CD `db deploy`. No EF Core migrations exist or are planned; `foundrygate db
compare` (and the `LibGit2Sharp`/ordering-noise-discarding machinery it implied) was **not**
implemented — there is no live database to compare *from* in this model, only the checked-in .sql
files compared *against* the entity model via the parity test. The CLI also uses
`System.CommandLine` (already pinned in `Directory.Packages.props`), not `Spectre.Console.Cli` as
originally planned below.

Table file names below also use each table's actual (plural) name —
`SystemConfigurations.sql`/`AuditLogs.sql`, not `SystemConfiguration.sql`/`AuditLog.sql` — to match
`AppDbContext`'s `DbSet` names and the file-name-equals-table-name convention the parity test
depends on.

The parity test's documented gaps (no data type/length/precision checking, no composite-FK
support, no index-composition validation beyond the `UNIQUE` flag) and the `db compare` deferral
above are tracked as follow-up work in
[#100](https://github.com/kolatts/foundry-gate/issues/100) rather than left as inline TODOs, per
CLAUDE.md's "everything is a GitHub issue" rule.

## Overview
Foundry Gate uses a hybrid schema management approach borrowed from imagile-app: EF Core migrations are the developer-facing workflow (fast iteration, `dotnet ef migrations add`), but the canonical schema artifact is a `.sqlproj` file that DacFx can build into a dacpac. A `FoundryGate.Cli` dotnet tool ties it together — `Foundry Gate db compare` runs a DacFx schema comparison between the local database (kept current by EF migrations) and the `.sqlproj` files, then publishes any delta back into the project's SQL files. CI builds the dacpac and the CLI deploys it. This gives the precision of dacpac-based deployments without abandoning EF's migration ergonomics.

> **Historical note**: the section above is the original plan and is now superseded by the Status
> update note (no EF migrations, no `db compare`). Kept for context on how the design evolved.

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
    FoundryGate.Cli.csproj           # PackAsTool: true, ToolCommandName: Foundry Gate
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
Create `src/FoundryGate.Database/FoundryGate.Database.sqlproj` using SDK `Microsoft.Build.Sql/2.0.0` targeting `SqlAzureV12DatabaseSchemaProvider`. Add one `.sql` file per table under `dbo/Tables/` matching the EF entities defined in epic #2 exactly (column names, types, nullability, and indexes). The `.sqlproj` is the schema source of truth for dacpac generation — it must stay in sync with EF migrations via the `Foundry Gate db compare` workflow, not be edited by hand. Add the project to `FoundryGate.sln`. The dacpac output goes to `artifacts/FoundryGate.Database.dacpac` for the CI pipeline to consume.

Files actually created (see the Status update note for the `SystemConfigurations`/`AuditLogs` naming correction):
- `src/FoundryGate.Database/FoundryGate.Database.sqlproj` (already scaffolded by #1 at
  `Microsoft.Build.Sql/2.2.0`, not `2.0.0` as originally planned)
- `src/FoundryGate.Database/dbo/Tables/Users.sql`
- `src/FoundryGate.Database/dbo/Tables/Groups.sql`
- `src/FoundryGate.Database/dbo/Tables/GroupMembers.sql`
- `src/FoundryGate.Database/dbo/Tables/QuotaAllocations.sql`
- `src/FoundryGate.Database/dbo/Tables/QuotaIncreaseRequests.sql`
- `src/FoundryGate.Database/dbo/Tables/SystemConfigurations.sql`
- `src/FoundryGate.Database/dbo/Tables/AuditLogs.sql`
- `src/FoundryGate.Tests.Predeployment/Data/Conventions/SchemaParityTests.cs` (new — the drift
  alarm substituting for `db compare`)

`FoundryGate.sln` already referenced the project (scaffolded by #1); no change needed. The SDK
globs `dbo/**/*.sql` by default, so no explicit `<ItemGroup>`/`<Folder>` wiring was needed for the
new files either — verified by `dotnet build src/FoundryGate.Database` picking them all up.

### Add FoundryGate.Cli dotnet tool with db compare, deploy, and seed commands (#78)
Create `src/FoundryGate.Cli/FoundryGate.Cli.csproj` as a packable dotnet tool (`PackAsTool: true`, `ToolCommandName: Foundry Gate`). Reference `FoundryGate.Data` for DbContext access. Use `Spectre.Console.Cli` for command structure and `Spectre.Console` for output. Key packages: `Microsoft.SqlServer.DacFx`, `LibGit2Sharp`.

**`Foundry Gate db compare`** (#78-compare)
The core command. Uses DacFx `SchemaComparison` API:
```
source → SchemaCompareDatabaseEndpoint(localConnectionString)
target → SchemaCompareProjectEndpoint(path/to/FoundryGate.Database.sqlproj)
comparison.Compare()
comparison.PublishChangesToProject()
```
After publishing, use `LibGit2Sharp` to check the diff — discard any changes that are purely column reordering with no semantic difference (same pattern as imagile-app `SchemaComparisonHelpers`). Print a summary table via `Spectre.Console` showing each changed object and its type.

> **Windows-only**: DacFx schema comparison is Windows-only due to native SQL Server tooling dependencies. The compare command must check the OS and exit gracefully on Linux/macOS with a clear error message pointing to the Windows requirement.

**`Foundry Gate db deploy`** (#78-deploy)
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

**`Foundry Gate db seed`** (#78-seed)
Runs `IFoundry GateSeeder` which inserts the eight `SystemConfiguration` rows (idempotent — upsert by key). Accepts `--env` flag: `local` seeds with localhost defaults; `dev`/`prod` seeds placeholder values that operators update via the admin UI.

**`Foundry Gate local setup`** (#78-setup)
One-command local dev bootstrap:
1. Check Docker is running; start `mcr.microsoft.com/mssql/server:2022-latest` on port 1433 if not present
2. Wait for SQL Server to be ready (retry loop)
3. Run `dotnet ef database update` against `FoundryGate.Data` (applies all migrations)
4. Run `Foundry Gate db compare` to sync EF migration output → .sqlproj
5. Run `Foundry Gate db seed --env local`
6. Print connection string for `appsettings.Development.json`

Files actually created (`System.CommandLine`, not `Spectre.Console.Cli`; no `db compare`/`db seed`,
`db seed-reference` + `db seed-test` instead per CONVENTIONS.md's reference-vs-test-data seeding
split; `ip setup` stub added — see the Status update note):
- `src/FoundryGate.Cli/FoundryGate.Cli.csproj` (already scaffolded by #1)
- `src/FoundryGate.Cli/Program.cs` (already scaffolded by #1; wired to the new commands)
- `src/FoundryGate.Cli/Commands/Db/DbCommand.cs`
- `src/FoundryGate.Cli/Commands/Db/Deploy/DeployCommand.cs`
- `src/FoundryGate.Cli/Commands/Db/SeedReference/SeedReferenceCommand.cs`
- `src/FoundryGate.Cli/Commands/Db/SeedTest/SeedTestCommand.cs`
- `src/FoundryGate.Cli/Commands/Local/LocalCommand.cs`
- `src/FoundryGate.Cli/Commands/Local/Setup/SetupCommand.cs`
- `src/FoundryGate.Cli/Commands/Ip/IpCommand.cs`
- `src/FoundryGate.Cli/Commands/Ip/Setup/SetupCommand.cs` (stub — #96)
- `src/FoundryGate.Cli/Helpers/CliDbContextFactory.cs`
- `.github/workflows/_deploy-database.yml` (#79)

`FoundryGate.sln` already referenced the project (scaffolded by #1); no change needed.

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
Foundry Gate db compare

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

A separate `db-deploy.yml` workflow (reusable, `workflow_call`) downloads the dacpac artifact and runs `Foundry Gate db deploy` against the target environment.

> **Naming correction**: implemented as `.github/workflows/_deploy-database.yml` (leading
> underscore, matching imagile-app's convention for `workflow_call`-only files that never trigger
> directly) rather than `db-deploy.yml`. It does not yet have a `build-dacpac`
> job/caller to feed it artifacts — that lands with #67's CI/CD epic — but the file's `inputs`/
> `jobs` shape is standalone-valid today (see its own header comment for the exact artifact names
> it expects: `dacpac` and `foundrygate-cli`).

---

## Verification

- [x] `dotnet build src/FoundryGate.Database` produces a `.dacpac` file — builds clean, 0 warnings
      (`src/FoundryGate.Database/bin/Debug/FoundryGate.Database.dacpac`).
- [x] `dotnet build FoundryGate.sln` — 0 warnings, 0 errors across all 9 projects including the
      dacpac.
- [x] `dotnet test src/FoundryGate.Tests.Predeployment` — 66/66 passing, including the new
      `SchemaParityTests.CheckedInSqlFiles_ShouldMatchEfModel` drift alarm (verified it actually
      catches drift, not just passes vacuously, by injecting a deliberate column-name mismatch and
      confirming the test failed with the expected violation messages before reverting).
- [x] `dotnet format FoundryGate.sln --verify-no-changes` — clean.
- [x] `dotnet run --project src/FoundryGate.Cli -- local setup --test-data` against docker SQL
      (`docker compose up -d`, port 3433) — dropped, `EnsureCreated`, seeded 8
      `SystemConfiguration` rows + Bogus demo data end-to-end. Verified via `sqlcmd` inside the
      container: 7 tables created (`Users`, `Groups`, `GroupMembers`, `QuotaAllocations`,
      `QuotaIncreaseRequests`, `SystemConfigurations`, `AuditLogs`), row counts match
      `TestDataSeeder`'s output (8 users, 1 group, 3 members, 8 allocations, 1 request, 8 config
      keys).
- [x] `dotnet run --project src/FoundryGate.Cli -- db deploy <dacpac> <connection-string>
      --drop-objects` deployed the built dacpac to a **second** local database
      (`FoundryGateDeployTest`) against the same docker SQL Server, confirming `db deploy` works
      independently of `local setup`'s `EnsureCreated` path. Also ran `db seed-reference` and
      `db seed-test` against that same deployed database to exercise the full CLI surface.
- [x] **Bonus**: this `db deploy` run against real SQL Server is the first time the FK
      `DeleteBehavior` graph from #92 (Group→GroupMember, User→QuotaAllocation,
      User→QuotaIncreaseRequest cascade; everything else `NoAction`) has been checked against
      anything other than SQLite in-memory. It deployed clean with **no** "multiple cascade
      paths" error, and `sys.foreign_keys` on the deployed database confirms the exact intended
      `CASCADE`/`NO_ACTION` split — directly resolves #94's ask; see PR body for the query output.
- [x] `docker compose down` — torn back down after verification.
- [ ] `Foundry Gate db compare` — **not implemented** (see the Status update note above): DacFx
      schema-compare is Windows-only and there is no live-database-to-compare-from in the
      EnsureCreated-based pipeline this repo actually uses. The `SchemaParityTests` Predeployment
      test is the substitute drift check, and it runs cross-platform in CI.
- [x] `foundrygate ip setup` is a documented stub (issues #96 tracks the real implementation) that
      prints guidance and exits 1 rather than doing anything; the reusable
      `.github/workflows/_deploy-database.yml` (#79) calls it with `continue-on-error: true` and a
      comment pointing at #96.
