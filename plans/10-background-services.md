# Background services — monthly reset and usage sync (Azure Functions)

> GitHub: #10  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
Both scheduled jobs — monthly quota reset and token usage sync — live in a dedicated `FoundryGate.Functions` project using the .NET 10 isolated worker model. Moving them out of the API container means the Container App has no background noise, the jobs scale and restart independently, and the Flex Consumption plan means they cost nothing when idle. Both functions share `FoundryGate.Data` for EF Core access and `FoundryGate.Core` for quota resolution and the reset itself (#119), and use the Function App's **user-assigned** managed identity for Key Vault, storage and Azure Monitor access. Neither talks to APIM. The API no longer exposes a `/internal/sync-usage` endpoint.

> **Amended by the #10 direction update and delivered in the #38/#39/#84/#119 wave.** The
> original approach below assumed enforcement lived in this host (suspend/re-enable APIM
> subscriptions) and that usage came from App Insights `customMetrics`. Neither is true:
> enforcement is real-time at the gateway, and usage comes from `ApiManagementGatewayLlmLog`.
> What actually shipped is described here.

## Approach

### Monthly quota reset (#38, #165)
`FoundryGate.Functions` is a .NET 10 isolated worker. `Program.cs` binds and validates its own
`Configuration/AppSettings.cs`, resolves `@KeyVault()` references, registers an
`AppTokenCredential`, and calls `AddFoundryGateData` + `AddFoundryGateFunctionsServices`
(which calls Core's `AddQuotaCore()`).

`Functions/MonthlyQuotaResetFunction.cs` is a `TimerTrigger("0 1 0 * * *")` — **daily** at
00:01 UTC, not monthly, so `SystemConfiguration[ResetDayOfMonth]` is honoured (#165, D-015).
It delegates to `Services/Quota/MonthlyResetJob`, which:

1. reads `ResetDayOfMonth` (default and fallback: the 1st) and stops on any other day;
2. takes a blob lease on the Functions storage account (`IResetLock` → `BlobResetLock`,
   identity-based; `NullResetLock` where there is no storage account) so two replicas cannot
   both run — belt to the Timer trigger's own singleton braces;
3. calls Core's `IQuotaResetService.ResetAsync(QuotaResetTrigger.Scheduled())`, which upserts
   every active user's allocation, **preserves `TokensUsed`**, clears `IsHardStopped`, stamps
   `ResetDate`, adds one `quota.monthly-reset` audit row and saves once.

No APIM call at all: the gateway's `llm-token-limit` monthly window is a UTC calendar month
that resets itself, and subscription state is reserved for offboarding.

### Usage reconciliation (#39, #84)
`Functions/UsageSyncFunction.cs` is a `TimerTrigger("0 */15 * * * *")` delegating to
`Services/Usage/UsageSyncJob`. `IUsageQueryClient` (`LogAnalyticsUsageQueryClient` over
`Azure.Monitor.Query`'s `LogsQueryClient`, authenticated as the Function App identity) runs the
checked-in `Kql/UsageBySubscription.kql` — `ApiManagementGatewayLlmLog` joined to
`ApiManagementGatewayLogs` on `CorrelationId`, summarised per `ApimSubscriptionId` — against the
workspace GUID in `Gateway:LogAnalyticsWorkspaceId`, with the billing period passed as the
query's time range rather than baked into the text.

The job maps subscription names back to users through `ApimSubscriptionNames.TryGetUserId`,
**overwrites** `TokensUsed` (period totals, so a re-run converges), counts unknown
subscriptions, and records over-budget usage as *drift* — a Warning with the delta and a count
in the audit row. It never sets `IsHardStopped` and never touches APIM: enforcement is the
gateway's 403.

### Shared services (#119)
Quota resolution, the tier map and the reset itself live in `FoundryGate.Core` so the Api and
this host run one implementation — see D-014.

## Verification
- [x] `dotnet build` passes for `FoundryGate.Functions` (zero warnings, `TreatWarningsAsErrors`)
- [x] Monthly reset creates new `QuotaAllocation` rows idempotently (running twice produces no duplicates)
- [x] Reset does not touch `TokensUsed` on the still-running current period
- [x] Reset acts only on `SystemConfiguration[ResetDayOfMonth]`, and an unusable value falls back to the 1st rather than never resetting (#165)
- [x] A replica that loses the lock writes nothing at all
- [x] Usage sync updates `TokensUsed` from a fake Log Analytics response, converges on re-run, counts unknown subscriptions, and reports drift without hard-stopping anyone
- [x] The KQL is a checked-in file the query client loads, and names both tables and the join key
- [ ] Both functions appear in the Azure portal under the deployed Function App — [#178](https://github.com/kolatts/foundry-gate/issues/178)
- [ ] Structured logs from both functions are visible in Application Insights — [#178](https://github.com/kolatts/foundry-gate/issues/178)
- [ ] The KQL returns matching rows against a real gateway, and `ApimSubscriptionId` renders as the subscription name — [#178](https://github.com/kolatts/foundry-gate/issues/178)
