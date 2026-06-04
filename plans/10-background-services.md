# Background services — monthly reset and usage sync (Azure Functions)

> GitHub: #10  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
Both scheduled jobs — monthly quota reset and token usage sync — live in a dedicated `FoundryGate.Functions` project using the .NET 10 isolated worker model. Moving them out of the API container means the Container App has no background noise, the jobs scale and restart independently, and the Flex Consumption plan means they cost nothing when idle. Both functions share `FoundryGate.Data` for EF Core access and use the Function App's system-assigned Managed Identity for Key Vault, APIM, and Azure Monitor access. The API no longer exposes a `/internal/sync-usage` endpoint.

## Approach

### Implement monthly quota reset as an Azure Function Timer Trigger (#38)
Create `FoundryGate.Functions` as a .NET 10 isolated-process Azure Functions project. Register `FoundryGateDbContext` and `IQuotaResolutionService` in `Program.cs` using the Functions host builder (same DI pattern as ASP.NET Core). Create `MonthlyQuotaResetFunction` with a `TimerTrigger` cron expression `0 1 1 * *` (00:01 UTC on the 1st of each month). In the function body, fetch all active users, call `IQuotaResolutionService.ResolveAsync` for the new period for each user, INSERT the resulting `QuotaAllocation` rows (idempotent — `ON CONFLICT DO NOTHING` or EF Core upsert), and write a single `quota.monthly-reset` audit log entry with the processed user count. Use `ILogger<MonthlyQuotaResetFunction>` for structured logging visible in Application Insights.

Files expected to be created or modified:
- `src/FoundryGate.Functions/FoundryGate.Functions.csproj`
- `src/FoundryGate.Functions/Program.cs`
- `src/FoundryGate.Functions/MonthlyQuotaResetFunction.cs`
- `FoundryGate.sln` (add new project)

### Implement token usage sync as an Azure Function Timer Trigger (#39)
Create `UsageSyncFunction` with a `TimerTrigger` cron expression `0 */15 * * * *` (every 15 minutes). The function queries Azure Monitor Logs via `Azure.Monitor.Query` SDK using a KQL query that aggregates `customMetrics` (emitted by the APIM `llm-emit-token-metric` policy) by APIM subscription ID for the current billing period. For each row returned, match the subscription ID to a `User.ApimSubscriptionId`, find the current-period `QuotaAllocation`, and set `TokensUsed` to the aggregated value (overwrite, not accumulate — the query always returns totals). Log the count of allocations updated and any subscription IDs that had no matching user. The Managed Identity requires `Monitoring Reader` on the Log Analytics workspace (already assigned in `roleAssignments.bicep`).

Files expected to be created or modified:
- `src/FoundryGate.Functions/UsageSyncFunction.cs`
- `src/FoundryGate.Functions/Services/IUsageSyncService.cs`
- `src/FoundryGate.Functions/Services/UsageSyncService.cs`
- `src/FoundryGate.Functions/FoundryGate.Functions.csproj` (Azure.Monitor.Query, Azure.Identity packages)

## Verification
- [ ] `dotnet build` passes for `FoundryGate.Functions`
- [ ] Monthly reset creates new `QuotaAllocation` rows idempotently (running twice produces no duplicates)
- [ ] Reset does not touch `TokensUsed` on the still-running current period
- [ ] Usage sync function updates `TokensUsed` correctly from a mocked Log Analytics response
- [ ] Both functions appear in the Azure portal under the deployed Function App
- [ ] Structured logs from both functions are visible in Application Insights
