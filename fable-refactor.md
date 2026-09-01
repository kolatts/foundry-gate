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
| 1 | Research: APIM GenAI gateway policies, backend pools, 429 handling, tiers/pricing | ✅ done (findings below) |
| 2 | Research: Foundry provisioning (Bicep), Claude/GPT quotas, spillover, cost mgmt, Claude Code/Codex support | 🔄 in progress (agent) |
| 3 | Feasibility assessment written as docs page (`architecture/feasibility.mdx`) | ⬜ pending research |
| 4 | Update architecture overview + rate-limits reference for gateway-centric direction | ⬜ pending |
| 5 | Update `foundrygate-spec.md` + `PLANS.md` with the strategic shift | ⬜ pending |
| 6 | New plan file `plans/24-apim-genai-gateway.md` | ⬜ pending |
| 7 | GitHub issues: new gateway epic + sub-issues; comments on affected epics (#7, #10, #13, #60) | ⬜ pending |
| 8 | Docs site builds clean locally (`npm run build`) | ✅ baseline build passes; re-verify after edits |
| 9 | Commit + push to designated branch | ⬜ pending |

## Research findings

### 1. APIM GenAI gateway (verified against Microsoft Learn, Sep 2026)

**The strategic hypothesis is validated — and stronger than expected:**

- **`llm-token-limit` natively supports long-window token quotas**: `token-quota` +
  `token-quota-period="Hourly|Daily|Weekly|Monthly|Yearly"` per `counter-key`
  (e.g. `context.Subscription.Id`). Monthly per-developer budgets are enforceable
  **in real time at the gateway** (403 on quota exhaustion, 429 + Retry-After on TPM).
  The 15-minute Log Analytics sync loop is no longer the enforcement mechanism —
  it demotes to reconciliation/telemetry. GA on all tiers except Consumption.
- **Anthropic Messages API schema is supported** by `llm-token-limit` and
  `llm-emit-token-metric` **on APIM v2 tiers** — token counting works on Claude
  `/v1/messages` traffic, not just OpenAI-format payloads.
- **Backend pools + circuit breakers**: up to 30 backends/pool, priority groups +
  weights, circuit breaker rules tripping on 429/5xx with `acceptRetryAfter: true`
  (honors backend Retry-After, recommended for AI 429s). Priority-2 backends absorb
  traffic while priority-1 breakers are tripped → multi-region spillover for Claude
  without PTU. `retry` policy retries across backends (retry-onto-different-backend
  is the documented shape; no built-in wait-on-Retry-After attribute in `retry` itself).
- **No queueing primitive in APIM** — smoothing = token-bucket TPM limits + retry
  across pool + client backoff (agentic CLIs already honor 429/Retry-After).
- **Metrics**: `llm-emit-token-metric` has built-in Subscription ID / Product ID /
  User ID dimensions → App Insights. **Caveat: 100 unique values per dimension,
  silent discard beyond** — fine for dashboards, not billing. Billing-grade per-dev
  usage comes from the **`ApiManagementGatewayLlmLog`** Log Analytics table (per-request
  prompt/completion/total tokens) joined to `ApiManagementGatewayLogs` (subscription id)
  on `CorrelationId`. This is exactly the reconciliation feed FoundryGate's usage-sync
  Function should read (replacing App Insights REST assumptions in the spec).
- **Claude on Foundry** (from gateway docs; agent 2 confirming): Anthropic-format
  endpoint `https://<resource>.services.ai.azure.com/anthropic/v1/messages`; models
  incl. claude-opus-5 / sonnet-5 / haiku-4-5 (GA); **Global Standard + Data Zone
  Standard (US)** deployment types; **no PTU → no native spillover for Claude**;
  rate limits now expressed as RPM + *uncached input* TPM + *output* TPM, pooled per
  subscription per model across regions (e.g. sonnet-5 Enterprise: 2,000 RPM / 2M
  ITPM / 400K OTPM; pay-as-you-go: 40 RPM / 40K ITPM / 8K OTPM — note PAYG is
  nonzero now, revising the "EA-only, quota 0" claim in the current rate-limits doc).
- **Foundry-portal native "AI gateway" (preview)** does TPM/quota **per project**,
  not per developer — validates FoundryGate's niche; requires v2-tier APIM.
- **Tiering**: cheapest viable tier = **Basic v2** (token policies + Anthropic schema
  + pools + circuit breaker + subscriptions). Consumption tier not viable. Pricing
  ~$210/unit/mo (third-party figure, needs calculator confirmation).
- **Open caveats to PoC**: how `llm-token-limit` counts Anthropic cache-read/creation
  tokens (Claude Code uses heavy prompt caching; gateway counts may diverge from
  Foundry ITPM accounting); estimator accuracy for Anthropic tokenization; streaming
  always estimates.

### 2. Foundry provisioning / harness support

_Agent still running._

## Feasibility verdict

_Pending research._

## Issue changes made

_None yet._
