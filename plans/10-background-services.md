# Background services — monthly reset and usage sync

> GitHub: #10  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
This epic adds two hosted background services to the API: a monthly quota reset job that fires at 00:01 UTC on the first of each month and re-resolves every user's allocation for the new period, and a usage sync endpoint (`POST /internal/sync-usage`) that pulls token consumption data from Application Insights and writes it back to `QuotaAllocation.TokensUsed`. Together these two services keep the quota ledger current without requiring any external scheduler beyond the Container App's always-on runtime.

## Approach

### Implement monthly quota reset BackgroundService firing at 00:01 UTC on the 1st (#38)
Create a `MonthlyQuotaResetService` that implements `BackgroundService`. In `ExecuteAsync`, calculate the next 00:01 UTC on the first of the upcoming month, use `Task.Delay` to sleep until then, then for each active user call `IQuotaResolutionService.ResolveAsync` for the new period to create a fresh `QuotaAllocation` row. Use a scoped `IServiceProvider` factory (the standard pattern for scoped services inside hosted services) so EF Core contexts are properly scoped. Log the start and completion of each reset run, including the count of users processed. Write a single `QuotaResetRun` audit log entry per month summarising the batch.

Files expected to be created or modified:
- `src/FoundryGate.Api/BackgroundServices/MonthlyQuotaResetService.cs`
- `src/FoundryGate.Api/Program.cs` (register the hosted service)

### Implement POST /internal/sync-usage to pull token counts from Application Insights (#39)
Create an `InternalController` (not exposed in public OpenAPI, protected by a shared secret header or IP allow-list) with `POST /internal/sync-usage`. The endpoint queries Application Insights via the REST query API (or the `Azure.Monitor.Query` SDK) using a KQL query that aggregates token usage per APIM subscription key for the current billing period. It then matches each subscription key to a `QuotaAllocation` row and updates `TokensUsed`. The endpoint is designed to be called by a Container App scheduled job or a GitHub Actions workflow on a regular cadence (e.g. every 15 minutes). Return a summary of rows updated.

Files expected to be created or modified:
- `src/FoundryGate.Api/Controllers/InternalController.cs`
- `src/FoundryGate.Api/Services/IUsageSyncService.cs`
- `src/FoundryGate.Api/Services/UsageSyncService.cs`
- `src/FoundryGate.Api/FoundryGate.Api.csproj` (Azure.Monitor.Query package)
- `src/FoundryGate.Api/appsettings.json` (AppInsights workspace ID)

## Verification
- [ ] `dotnet build` passes
- [ ] Monthly reset creates new `QuotaAllocation` rows for the next period
- [ ] Reset does not overwrite `TokensUsed` on the current (in-progress) period
- [ ] Usage sync endpoint updates `TokensUsed` correctly from mocked App Insights data
- [ ] Internal endpoint returns `401` when the shared secret header is missing
