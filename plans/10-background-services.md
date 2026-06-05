# Background services — monthly reset and usage sync (Azure Functions)

> GitHub: #10  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
Both scheduled jobs — monthly quota reset and token usage sync — live in a dedicated `FoundryGate.Functions` project using the .NET 10 isolated worker model. Moving them out of the API container means the Container App has no background noise, the jobs scale and restart independently, and the Flex Consumption plan means they cost nothing when idle. Both functions share `FoundryGate.Data` for EF Core access and use the Function App's system-assigned Managed Identity for Key Vault, APIM, and Azure Monitor access. The API no longer exposes a `/internal/sync-usage` endpoint.

## Approach

### Implement monthly quota reset as an Azure Function Timer Trigger (#38)
Create `FoundryGate.Functions` as a .NET 10 isolated-process Azure Functions project. Register `Foundry GateDbContext`, `IQuotaResolutionService`, and `IApimKeyService` in `Program.cs`. Create `MonthlyQuotaResetFunction` with `TimerTrigger` cron `0 1 1 * *` (00:01 UTC on the 1st). For each active user: call `IQuotaResolutionService.ResolveAsync` for the new period (upsert `QuotaAllocation` with `TokensUsed = 0`, `IsHardStopped = false`); then call `IApimKeyService.ReenableSubscriptionAsync` if the user's APIM subscription was suspended for quota exhaustion (PATCH `.../state=active` on the APIM Management plane). Write a single `quota.monthly-reset` audit log entry with the count of users processed and subscriptions re-enabled.

Files expected to be created or modified:
- `src/FoundryGate.Functions/FoundryGate.Functions.csproj`
- `src/FoundryGate.Functions/Program.cs`
- `src/FoundryGate.Functions/MonthlyQuotaResetFunction.cs`
- `FoundryGate.sln` (add new project)

### Implement token usage sync as an Azure Function Timer Trigger (#39)
Create `UsageSyncFunction` with `TimerTrigger` cron `0 */15 * * * *` (every 15 minutes). Query Azure Monitor Logs via `Azure.Monitor.Query` SDK — KQL aggregates `customMetrics` emitted by the APIM `llm-emit-token-metric` policy, grouped by subscription ID for the current billing period. For each row: match subscription ID to `User.ApimSubscriptionId`, find the current-period `QuotaAllocation`, and **overwrite** `TokensUsed` (the query always returns period totals). After updating, check if `TokensUsed >= AllocatedTokens` for any user: if so, call `IApimKeyService.SuspendSubscriptionAsync` (PATCH `.../state=suspended`) and set `QuotaAllocation.IsHardStopped = true` in the DB. Log the count of allocations updated and new suspensions triggered.

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
