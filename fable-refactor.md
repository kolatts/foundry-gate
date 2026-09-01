# Fable Refactor — Strategic Direction & Work Plan

> Working document for the September 2026 strategic pass over FoundryGate.
> Tracks the full plan and is updated as work completes.
> Branch: `claude/azure-foundry-cost-mgmt-0d5dtc`

## Goal (restated from request)

Assess feasibility of FoundryGate as a **drop-in Azure tenant solution** ("one-stop shop")
that provisions Azure AI Foundry models for agentic harnesses (Claude Code, Codex CLI —
CLIs only) and provides:

- **Cost management** — per-developer/per-group budgets, visibility, chargeback inputs
- **Quotas** — monthly token budgets with approval workflow (existing design)
- **Rate limiting** — solve *transient* TPM/RPM/429 pain at the gateway, not just monthly budgets
- **Metrics** — per-developer usage, dashboards, Log Analytics

Constraints: best-fit Azure technology, Bicep provisioning included, forkable into any tenant.

## Strategic hypothesis under evaluation

The current spec enforces quotas via a 15-minute Log Analytics sync + APIM subscription
suspension. That leaves the *transient* problem (TPM/RPM 429s, burst windows, shared
deployment saturation) unsolved — the docs call it a "scale blocker."

Hypothesis: make **APIM's GenAI gateway capabilities** the core enforcement plane:

1. `llm-token-limit` policy keyed on subscription ID → real-time per-developer TPM caps,
   possibly long-window token quotas (verify current capability).
2. `llm-emit-token-metric` → per-developer metrics dimensioned by subscription.
3. **Backend pools with priority/weights + circuit breakers** across multiple Foundry
   deployments (multi-region) → multiplies TPM/RPM headroom, absorbs 429s via failover.
4. **Retry-on-429 honoring Retry-After** in policy → smooths transient bursts.
5. FoundryGate app becomes the *control plane* (users, keys, budgets, approvals, Bicep
   provisioning of deployments); APIM is the *data plane* (enforcement, smoothing, metrics).

Research agents are validating this against current (Sep 2026) Azure capabilities before
any docs/issues are rewritten.

## Work plan

| # | Task | Status |
|---|------|--------|
| 1 | Research: APIM GenAI gateway policies, backend pools, 429 handling, tiers/pricing | 🔄 in progress (agent) |
| 2 | Research: Foundry provisioning (Bicep), Claude/GPT quotas, spillover, cost mgmt, Claude Code/Codex support | 🔄 in progress (agent) |
| 3 | Feasibility assessment written as docs page (`architecture/feasibility.mdx`) | ⬜ pending research |
| 4 | Update architecture overview + rate-limits reference for gateway-centric direction | ⬜ pending |
| 5 | Update `foundrygate-spec.md` + `PLANS.md` with the strategic shift | ⬜ pending |
| 6 | New plan file `plans/24-apim-genai-gateway.md` | ⬜ pending |
| 7 | GitHub issues: new gateway epic + sub-issues; comments on affected epics (#7, #10, #13, #60) | ⬜ pending |
| 8 | Docs site builds clean locally (`npm run build`) | ✅ baseline build passes; re-verify after edits |
| 9 | Commit + push to designated branch | ⬜ pending |

## Research findings

_To be filled in when the research agents report back. Nothing below this line is final
until then._

## Feasibility verdict

_Pending research._

## Issue changes made

_None yet._
