# Foundry Gate — Planning Convention

All detailed implementation plans live in `/plans/`. GitHub Issues contain the "what" and "why"; plan files contain the "how".

## Directory structure

```
plans/
  {issue-number}-{kebab-title}.md     e.g. plans/02-data-layer.md
```

One plan file per **epic** issue. Sub-issues are covered inside the parent plan under their own `###` section.

## Plan file format

```markdown
# {Issue title}

> GitHub: #{issue-number}  
> Milestone: v0.x — {Milestone name}  
> Labels: epic, {backend|frontend|infra|docs}

## Overview
One paragraph. What this epic delivers and why it matters.

## Approach

### {Sub-issue title} (#{sub-issue-number})
One paragraph explaining the implementation approach, key decisions, and any constraints.

Files expected to be created or modified:
- `src/FoundryGate.Data/Entities/User.cs`
- …

## Verification
- [ ] dotnet build passes
- [ ] All EF migrations run cleanly against a local SQL instance
- [ ] Integration tests green (if applicable)
- [ ] Manual smoke test steps
```

## Phases and milestones

| Milestone | Scope |
|---|---|
| **v0.1 — Foundation** | Solution scaffold, data layer, shared DTOs, API project + Entra auth |
| **v0.2 — Core API** | All backend endpoints: users, groups, quota, requests, APIM keys, background services |
| **v0.3 — Infrastructure** | Bicep modules (incl. APIM v2 gateway + Foundry deployments), GitHub Actions CI/CD |
| **v0.4 — Frontend** | Blazor WASM — developer and admin pages |
| **v0.5 — GenAI gateway** | Epic #81 / `plans/24-apim-genai-gateway.md`: real-time token quotas (`llm-token-limit`), backend pools + circuit breakers, metrics/reconciliation, CLI onboarding |

## Working on an issue

1. Check the GitHub issue for acceptance criteria.
2. Open `plans/{issue-number}-*.md` for the full implementation notes.
3. Work sub-issue by sub-issue; close each child issue as it's done.
4. Close the parent epic once all sub-issues are closed.
5. Commit message convention: `feat(scope): short description` referencing the issue number.

## Tech baseline

- **.NET 10** (all projects)
- **ASP.NET Core 10** Web API
- **EF Core 10** + Azure SQL (single database, no sharding)
- **Blazor WebAssembly** (.NET 10)
- **MudBlazor** component library for all UI
- **Astro + Starlight** for the docs site (GitHub Pages)
- **Microsoft Entra ID** via `Microsoft.Identity.Web`
- **Azure SDK** for APIM management (`Azure.ResourceManager.ApiManagement`)
- **Azure SDK** for AI Foundry model deployment (`Azure.ResourceManager.CognitiveServices`)
- **APIM v2 tier (Basic v2 minimum)** — required for Anthropic Messages schema support in
  `llm-token-limit` / `llm-emit-token-metric`; enforcement is gateway-side per epic #81
- **Microsoft Graph SDK** for Entra sync
- **Azure Functions** (.NET 10 isolated worker) for scheduled background jobs
- **Bicep** for all IaC
