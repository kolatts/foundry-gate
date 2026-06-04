# FoundryGate — Planning Convention

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
| **v0.3 — Infrastructure** | Bicep modules, GitHub Actions CI/CD |
| **v0.4 — Frontend** | Blazor WASM — developer and admin pages |

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
- **Microsoft Entra ID** via `Microsoft.Identity.Web`
- **Azure SDK** for APIM management (`Azure.ResourceManager.ApiManagement`)
- **Microsoft Graph SDK** for Entra sync
- **Bicep** for all IaC
