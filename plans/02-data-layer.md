# Data layer — EF Core entities, DbContext, and migrations

> GitHub: #2  
> Milestone: v0.1 — Foundation  
> Labels: epic, backend

## Overview
This epic defines the entire persistence model for Foundry Gate using EF Core 10 against a single Azure SQL database. It covers all entities, the DbContext, the initial EF migration, and seed data for the `SystemConfiguration` defaults. EF migrations are the developer-facing iteration workflow; after applying a migration, run `Foundry Gate db compare` (epic #76) to sync the delta back into the `FoundryGate.Database` `.sqlproj` SQL files. Commit both the migration and the updated `.sql` file together — they must stay in sync.

## Approach

### Define EF Core 10 entities and configure all relationships in DbContext (#22)
Create one entity class per table under `src/FoundryGate.Data/Entities/`. Use data annotations for simple constraints and `IEntityTypeConfiguration<T>` classes (Fluent API) for relationship configuration, index definitions, and column-level settings. Key relationships: User→GroupMembership (many), Group→GroupMembership (many), User→QuotaAllocation (one-to-one per period), Group→QuotaPolicy (optional), User→ApiKey (one-to-many), User→AuditLog (one-to-many). Register all configurations via `modelBuilder.ApplyConfigurationsFromAssembly`. Add the `Foundry GateDbContext` class with a `DbContextOptions` constructor suitable for both runtime DI and design-time factory use.

Files expected to be created or modified:
- `src/FoundryGate.Data/Entities/User.cs`
- `src/FoundryGate.Data/Entities/Group.cs`
- `src/FoundryGate.Data/Entities/GroupMembership.cs`
- `src/FoundryGate.Data/Entities/QuotaPolicy.cs`
- `src/FoundryGate.Data/Entities/QuotaAllocation.cs`
- `src/FoundryGate.Data/Entities/QuotaIncreaseRequest.cs`
- `src/FoundryGate.Data/Entities/ApiKey.cs`
- `src/FoundryGate.Data/Entities/AuditLog.cs`
- `src/FoundryGate.Data/Entities/SystemConfiguration.cs`
- `src/FoundryGate.Data/Configuration/` (one file per entity)
- `src/FoundryGate.Data/Foundry GateDbContext.cs`
- `src/FoundryGate.Data/DesignTimeDbContextFactory.cs`

### Create initial EF migration and seed the eight SystemConfiguration defaults (#23)
Run `dotnet ef migrations add InitialCreate` against `FoundryGate.Data` using the design-time factory. Review the generated migration for correctness (indexes, FK constraints, column types). Add a data seeder (or `modelBuilder.HasData`) to insert these `SystemConfiguration` rows — all values are placeholder strings that fork operators must replace via the admin `/config` page:

| Key | Default | Purpose |
|---|---|---|
| `DefaultMonthlyTokenQuota` | `"1000000"` | Per-user fallback monthly token budget |
| `ApimResourceId` | `""` | ARM resource ID of the APIM instance |
| `ApimGatewayUrl` | `""` | APIM gateway base URL shown to developers on `/me` |
| `ApimProductId` | `"foundrygate"` | APIM product name covering Foundry routes |
| `FoundryResourceId` | `""` | ARM resource ID of the Azure AI Foundry account |
| `EntraTenantId` | `""` | Azure AD tenant ID for Graph sync |
| `EntraGroupSyncEnabled` | `"false"` | Whether Entra group sync is active |
| `ResetDayOfMonth` | `"1"` | Day the monthly reset fires (always 1 for v1) |

Wire the migration and seeder to run on startup in `Program.cs` (development only) or via a separate CLI tool.

Files expected to be created or modified:
- `src/FoundryGate.Data/Migrations/` (generated migration files)
- `src/FoundryGate.Data/Configuration/SystemConfigurationConfiguration.cs`
- `src/FoundryGate.Api/Program.cs` (migration on startup, dev only)

## Verification
- [ ] `dotnet build` passes
- [ ] `dotnet ef migrations list` shows `InitialCreate` as applied
- [ ] All seven SystemConfiguration rows are present after seeding
- [ ] No orphaned FK constraints or missing indexes in the migration output
