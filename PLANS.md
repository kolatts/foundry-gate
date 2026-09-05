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
| **v0.6 — Live validation & demo** | First live `dev` deploy, live-validation children, Claude end-to-end, the spin-up / test / spin-down demo cycle (tracked on [GitHub Project #3](https://github.com/users/kolatts/projects/3)) |
| **Backlog** | Open decisions, deferred engineering debt, unscheduled features |

v0.1–v0.5 are closed (all issues done or moved); every open issue lives on v0.6 or Backlog.

## Issue conventions (adopted 2026-09-05)

The backlog was consolidated on 2026-09-05 from 26 flat issues into **8 parents with sub-issues**. The rules that produced it, so the next agent keeps the shape:

### Fewer, larger issues

- Work is filed as a **parent issue with GitHub sub-issues**, not as a flat list of siblings. A parent is a coherent goal someone would actually schedule ("Live validation of the dev environment"), not a theme.
- Target roughly **6–8 open parents**. If a new item does not belong under an existing parent, that is a signal to check whether it is really its own goal or a sub-issue of one.
- Sub-issues are attached natively: `gh api -X POST repos/kolatts/foundry-gate/issues/<parent>/sub_issues -F sub_issue_id=<child database id>`, where the database id comes from `gh api repos/kolatts/foundry-gate/issues/N --jq .id` (the `id` from `gh issue view --json id` is a **node** id and will not work).
- The old "one plan file per epic issue" convention is unchanged — plans still live in `plans/{nn}-{kebab}.md`, one per parent.

### Parent body format

```markdown
## Why            — 2-4 sentences: what this delivers and why it is grouped
## Sub-issues     — the GitHub sub-issue list renders above, but ALSO keep
                    a plain `- [ ] #N — title` checklist so the body reads as markdown
## Human attention — bullets naming exactly which steps need a person, and why
## Done when      — the closing condition for the parent
```

Children keep their own bodies. They gain one line at the top — `> Parent: #N — <title>` — and nothing else, except to correct facts that have gone stale.

### Labels — every open issue carries exactly one execution label

| Label | Meaning |
|---|---|
| `automated-ok` | An agent can execute this end to end |
| `needs-human` | Requires a person: owner credentials, tenant privilege, approvals, a decision, an Azure support plan |
| `do-not-automate` | Unwise to run unattended: cost, irreversible, or Anthropic create-once churn (E-007) |

Two are allowed **only** when a human step gates an automatable remainder (e.g. #120: Graph app-role grants are `needs-human`, the checklist afterwards is `automated-ok`) — and the body must say which half is which.

Orthogonal labels stack on top: `live-validation` (needs a deployed environment), `demo` (the spin-up / test / spin-down path), plus the existing `epic`, `backend`, `frontend`, `infra`, `docs`, `blocked`, `question`, `enhancement`, `bug`.

Two rules worth stating plainly, because both were wrong in the old backlog:

1. **`needs-human` is about privilege, not tooling.** An agent has the Azure and GitHub CLIs and can run every `az ad` / `az role assignment` / `gh variable` command in #109. What it lacks is Privileged Role Administrator, subscription Owner, and the standing to consent. Do not write "an agent cannot do this" when what you mean is "an agent is not allowed to".
2. **`do-not-automate` is about the failure mode.** If the recovery from a failed attempt is "try again", it is automatable. If the recovery is "open a support ticket" (Anthropic deployment creates, the Marketplace attestation PUT, a capacity PATCH on a live Claude deployment), it is not.

### Milestones (v0.6 onward)

- **`v0.6 — Live validation & demo`** — the first live deploy, everything that depends on it, the Claude end-to-end path, and the demo cycle.
- **`Backlog`** — open decisions, deferred engineering debt, and features with no scheduled release.
- `v0.1`–`v0.5` are **closed**; every straggler was moved before closing them.

### Unchanged

Everything is still a GitHub issue — no inline TODOs, no PR-body follow-up notes, no plan-file side remarks. A PR that discovers new work files the issue in the same breath, under the right parent, and closes its own with `Closes #N`.

### Project board

Everything open is on [GitHub Project #3 — FoundryGate](https://github.com/users/kolatts/projects/3) (Kanban; auto-add for new issues and sub-issues, auto-close, PR-merged workflows are on). Labels are the attention signal on each card. Note `gh project` needs the `project` token scope (`gh auth refresh -s project,read:project`).

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
