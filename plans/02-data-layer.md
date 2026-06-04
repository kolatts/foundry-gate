# Data layer — EF Core entities, DbContext, and migrations

> GitHub: #2  
> Milestone: v0.1 — Foundation  
> Labels: epic, backend

## Overview
This epic defines the entire persistence model for FoundryGate using EF Core 10 against a single Azure SQL database. It covers all entities (User, Group, GroupMembership, QuotaPolicy, QuotaAllocation, QuotaIncreaseRequest, ApiKey, AuditLog, SystemConfiguration), the DbContext with all relationship configurations, the initial migration, and seed data for the seven SystemConfiguration defaults. A solid data layer here means all API epics can start against a real schema rather than building against a moving target.

## Approach

### Define EF Core 10 entities and configure all relationships in DbContext (#22)
Create one entity class per table under `src/FoundryGate.Data/Entities/`. Use data annotations for simple constraints and `IEntityTypeConfiguration<T>` classes (Fluent API) for relationship configuration, index definitions, and column-level settings. Key relationships: User→GroupMembership (many), Group→GroupMembership (many), User→QuotaAllocation (one-to-one per period), Group→QuotaPolicy (optional), User→ApiKey (one-to-many), User→AuditLog (one-to-many). Register all configurations via `modelBuilder.ApplyConfigurationsFromAssembly`. Add the `FoundryGateDbContext` class with a `DbContextOptions` constructor suitable for both runtime DI and design-time factory use.

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
- `src/FoundryGate.Data/FoundryGateDbContext.cs`
- `src/FoundryGate.Data/DesignTimeDbContextFactory.cs`

### Create initial EF migration and seed the seven SystemConfiguration defaults (#23)
Run `dotnet ef migrations add InitialCreate` against `FoundryGate.Data` using the design-time factory. Review the generated migration for correctness (indexes, FK constraints, column types). Add a data seeder (or use `modelBuilder.HasData`) to insert the seven SystemConfiguration rows: `DefaultUserQuota`, `DefaultGroupQuota`, `DefaultTenantQuota`, `QuotaResetDay`, `MaxKeysPerUser`, `ApiVersion`, and `MaintenanceMode`. Wire the migration and seeder to run on startup in `Program.cs` (development only) or via a separate CLI tool.

Files expected to be created or modified:
- `src/FoundryGate.Data/Migrations/` (generated migration files)
- `src/FoundryGate.Data/Configuration/SystemConfigurationConfiguration.cs`
- `src/FoundryGate.Api/Program.cs` (migration on startup, dev only)

## Verification
- [ ] `dotnet build` passes
- [ ] `dotnet ef migrations list` shows `InitialCreate` as applied
- [ ] All seven SystemConfiguration rows are present after seeding
- [ ] No orphaned FK constraints or missing indexes in the migration output
