---
title: System Overview
description: How FoundryGate's components fit together — quota resolution, APIM enforcement, and usage sync.
---

import { Aside } from '@astrojs/starlight/components';

## Component diagram

```
┌─────────────────────────────────────────────────────────────┐
│                        Developer                            │
│          Claude Code / Codex / OpenAI SDK                   │
└───────────────────────────┬─────────────────────────────────┘
                            │ api-key header
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              Azure API Management (existing)                │
│  • foundrygate product (subscription key auth)              │
│  • llm-emit-token-metric → Application Insights             │
│  • llm-token-limit (rate safety ceiling)                    │
│  • Backend: Azure AI Foundry                                │
└───────────┬──────────────────────────┬──────────────────────┘
            │ forwards request         │ emits token metrics
            ▼                          ▼
┌──────────────────────┐   ┌───────────────────────────────┐
│ Azure AI Foundry     │   │ Application Insights / Log    │
│ (model deployments)  │   │ Analytics Workspace           │
└──────────────────────┘   └───────────────┬───────────────┘
                                           │ Azure Monitor Query
                                           │ (every 15 min)
                            ┌──────────────▼───────────────┐
                            │   FoundryGate.Functions       │
                            │   • UsageSyncFunction        │
                            │   • MonthlyResetFunction     │
                            └──────────────┬───────────────┘
                                           │ EF Core
┌─────────────────────────────────────────▼───────────────────┐
│                        Azure SQL                            │
│  Users · Groups · QuotaAllocations · AuditLog · ...        │
└─────────────────────────────────────────┬───────────────────┘
                                          │ EF Core
┌─────────────────────────────────────────▼───────────────────┐
│                  FoundryGate.Api (Container App)            │
│  REST API · Entra auth · IQuotaResolutionService           │
│  IUserLifecycleService · IApimKeyService · IAuditService   │
└───────────────────────────┬─────────────────────────────────┘
                            │ Blazor WASM HTTP calls
                            ▼
┌─────────────────────────────────────────────────────────────┐
│           FoundryGate.Web (Static Web Apps)                 │
│   /me · /requests · /users · /groups · /foundry · /audit   │
└─────────────────────────────────────────────────────────────┘
```

---

## Five-level quota resolution

When a developer's allocation is created or updated, `IQuotaResolutionService` walks this chain — highest priority wins:

```
1. User.IsUnlimited = true          → unlimited (skip all)
2. User.MonthlyTokenQuota is set    → use that value
3. Any group has IsUnlimited = true → unlimited
4. Max(Group.MonthlyTokenQuota)     → use the highest group quota
5. SystemConfiguration["DefaultMonthlyTokenQuota"]  → system default
```

The resolved value is written to `QuotaAllocation` for the current period **and** pushed to the APIM subscription's cache key so the `azure-openai-token-limit` policy has a per-user ceiling.

---

## APIM enforcement model

FoundryGate does not rely on a DB flag poll to block traffic. The enforcement chain is:

```
UsageSyncFunction (every 15 min)
  └── reads TokensUsed from Log Analytics (period totals by subscription ID)
  └── updates QuotaAllocation.TokensUsed in SQL
  └── if TokensUsed >= AllocatedTokens:
        └── APIM Management: PATCH subscription → state: suspended
        └── QuotaAllocation.IsHardStopped = true

  Result: APIM returns 401 immediately — no gateway traffic passes
```

At monthly reset:
```
MonthlyResetFunction (00:01 UTC, 1st of month)
  └── for each active user:
        └── resolve quota for new period → INSERT QuotaAllocation
        └── if subscription was suspended → PATCH → state: active
        └── IsHardStopped = false
  └── write audit log: quota.monthly-reset
```

---

## APIM key lifecycle

```
                    ┌─────────────────┐
                    │   No key yet    │
                    └────────┬────────┘
                             │ first login / admin provision
                             ▼
                    ┌─────────────────┐
                    │     Active      │◄──── monthly reset re-enables
                    └────┬───────┬───┘
                         │       │
           quota hit     │       │ admin deactivate
                         ▼       ▼
               ┌──────────┐  ┌──────────┐
               │ Suspended│  │ Deleted  │
               │(quota)   │  │(inactive)│
               └──────────┘  └──────────┘
                             re-activate → full provision
                             (new key, new allocation)
```

**Suspend** (quota exhaustion): APIM subscription state → `suspended`. Key exists, can be re-enabled.  
**Delete** (admin deactivation / Entra departure): APIM subscription deleted. Re-activation requires a new `POST /keys/{userId}/provision`.

---

## Solution projects

| Project | Role |
|---|---|
| `FoundryGate.Api` | REST API, served by Azure Container App |
| `FoundryGate.Data` | EF Core entities, DbContext, migrations |
| `FoundryGate.Domain` | Shared DTOs and enums — no framework deps |
| `FoundryGate.Functions` | Timer-triggered Azure Functions (reset + sync) |
| `FoundryGate.Web` | Blazor WASM frontend (MudBlazor) |
| `FoundryGate.Database` | SQL Server project, builds dacpac |
| `FoundryGate.Cli` | `foundrygate` dotnet tool (compare, deploy, seed) |
