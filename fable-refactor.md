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
| 2 | Research: Foundry provisioning (Bicep), Claude/GPT quotas, spillover, cost mgmt, Claude Code/Codex support | ✅ done (findings below) |
| 3 | Feasibility assessment written as docs page (`architecture/feasibility.mdx`) | ✅ done |
| 4 | Update architecture overview + rate-limits reference for gateway-centric direction | ✅ done (rate-limits page rewritten with Sep 2026 facts; also fixed broken Aside imports by renaming three `.md` pages to `.mdx`) |
| 5 | Update `foundrygate-spec.md` + `PLANS.md` with the strategic shift | ✅ done (amendment note + §5.4/§9.1 amendments; v0.5 milestone) |
| 6 | New plan file `plans/24-apim-genai-gateway.md` | ✅ done |
| 7 | GitHub issues: new gateway epic + sub-issues; comments on affected epics (#7, #10, #13, #60) | ✅ done (epic #81, sub-issues #82–#85, 4 comments) |
| 8 | Docs site builds clean locally (`npm run build`) | ✅ 11 pages, asides verified rendering |
| 9 | Commit + push to designated branch (mirrored to main) | ✅ done |
| 10 | Follow-ups (not started): CLI-setup rewrite ripples into `/me` panel spec (#85); `estimate-prompt-tokens` PoC; Bicep implementation per #13 comment | ⬜ future work |

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

### 2. Foundry provisioning / harness support (verified against Microsoft Learn + Anthropic docs, Sep 2026)

- **Claude is GA on Microsoft Foundry** (~July 2026) and **eligibility has broadened**:
  any paid Azure subscription with an active pay-as-you-go billing method qualifies
  (CSP, free trial, student, credit-only subscriptions excluded). The docs-site claim
  "EA/MCA-E only, PAYG quota 0" is stale. PAYG defaults: sonnet-5 40 RPM / 40K ITPM /
  8K OTPM; Enterprise/MCA-E 2,000 RPM / 2M ITPM / 400K OTPM. Limits are now
  **cache-aware**: rate limits count *uncached input* TPM + output TPM separately;
  cache reads are free against ITPM (huge for Claude Code, which caches heavily).
- **Bicep provisioning fully supported**: `Microsoft.CognitiveServices/accounts`
  (kind AIServices) + `accounts/deployments` with `model.format: 'Anthropic'` or
  `'OpenAI'` in one account (32 deployments/resource cap). Claude deployments carry a
  `modelProviderData` attestation block that auto-accepts the Azure Marketplace offer —
  no portal click-through. `sku.capacity` = thousands of TPM; deployment capacity is
  PATCH-able at runtime (programmatic rebalancing works); quota *increases* are
  form-only. Canonical Bicep in Azure-Samples/claude starter kit.
- **Claude hosting variants**: version `1` = hosted on Anthropic infra (eastus2,
  swedencentral), version `2` = hosted on Azure (GA, ~9 US/EU regions + DataZone
  Standard US). No PTU for Claude; spillover / priority processing / batch are
  OpenAI-only. Claude overflow handling must live in the APIM gateway (pools).
- **Claude Code officially supports Foundry**: `CLAUDE_CODE_USE_FOUNDRY=1`,
  `ANTHROPIC_FOUNDRY_BASE_URL` (accepts an arbitrary URL → pointing it at APIM is the
  documented pattern, incl. an official MS blog), `ANTHROPIC_FOUNDRY_API_KEY` /
  `_AUTH_TOKEN` / DefaultAzureCredential, and `ANTHROPIC_DEFAULT_{OPUS,SONNET,HAIKU}_MODEL`
  pinned to deployment names. Foundry does **not** return `anthropic-ratelimit-*`
  headers — client-side pacing can't rely on them.
- **Codex CLI officially supports Azure** via `~/.codex/config.toml`
  `[model_providers.azure]` (`base_url`, `env_key`, `wire_api = "responses"`).
  API-key auth only (no Entra). Codex models on Azure: gpt-5-codex through
  gpt-5.3-codex / 5.1-codex-max / 5.1-codex-mini.
- **Cost attribution**: Claude bills as a single aggregated Marketplace meter in
  "Claude Consumption Units" (CCU) — **Azure Cost Management cannot break down Claude
  cost per deployment or user**. Per-developer cost must be computed from gateway
  token telemetry × published token rates. OpenAI models bill as first-party meters
  with a `deployment` billing tag (per-deployment attribution works).
- Flagged unverified: whether the 300-concurrent cap still applies to Claude
  deployments (Claude docs no longer state it); `modelCapacities` API with
  `modelFormat=Anthropic`; exact codex default TPM values (volatile).

## Design decisions taken from the research

1. **Enforcement moves into APIM policy** (`llm-token-limit`: per-dev TPM + monthly
   token-quota keyed on subscription ID). Suspension via Management API remains only
   for deactivation/offboarding. The usage-sync Function becomes *reconciliation*
   (reads `ApiManagementGatewayLlmLog`, updates dashboards/DB, catches drift).
2. **Quota tiers as APIM products** (e.g. Standard / Power / Unlimited), each with its
   policy-defined monthly quota; per-user arbitrary values only if `token-quota`
   accepts policy expressions (PoC question) — otherwise tier assignment = product
   assignment, which also simplifies the five-level resolution to "resolve to a tier".
3. **APIM v2 tier required** (Basic v2 minimum) for Anthropic Messages schema support.
   Classic/Consumption not viable.
4. **Two API front doors on one gateway**: `/anthropic/*` (Anthropic Messages
   pass-through for Claude Code) and `/openai/*` (Responses/chat completions for
   Codex CLI); subscription-key header configured as `api-key` so both CLIs work
   with their native key env vars unchanged.
5. **Multi-deployment backend pools** per model family with priority groups +
   circuit breakers (`acceptRetryAfter`) as the transient-429 answer; multi-region
   pools are the Claude substitute for spillover/PTU.
6. **Cost page in FoundryGate** computes $ from token telemetry × rate card
   (system-config table), because Cost Management can't attribute Claude spend.

## Feasibility verdict

**Feasible, and better-timed than the original spec assumed.** Every pillar of the
"one-stop shop" now has a first-party Azure mechanism:

| Requirement | Mechanism | Grade |
|---|---|---|
| Provisioning (drop-in Bicep) | AIServices account + Anthropic/OpenAI deployments + APIM v2, incl. Marketplace attestation inline | ✅ Fully supported |
| Monthly quotas per developer | `llm-token-limit` `token-quota-period="Monthly"` per subscription — real-time 403 | ✅ Native, GA |
| Transient TPM/RPM smoothing | Per-dev TPM policy caps + backend pools + circuit breakers honoring Retry-After + CLI client backoff | ✅ Gateway-native (no queueing primitive; acceptable for CLIs) |
| Metrics per developer | `llm-emit-token-metric` (dashboards) + `ApiManagementGatewayLlmLog` (billing-grade) | ✅ Native |
| Cost management | Token telemetry × rate card (Claude CCU meter is aggregate-only) | ⚠️ Buildable, not native |
| Claude at large scale | No PTU; multi-region pools mitigate; possible 300-concurrent cap | ⚠️ Structural ceiling remains |
| True "drop-in" | One Bicep deploy + ~30-min tenant prep (app registration, admin consent, billing eligibility) | ⚠️ Near, not absolute |

Honest caveats: (1) gateway token counts for Anthropic traffic may diverge from
Foundry's cache-aware ITPM accounting — treat gateway numbers as enforcement +
estimate, Log Analytics as reconciliation; (2) `llm-token-limit` counters are
per-gateway-instance (fine on a single Basic v2 unit); (3) per-user *arbitrary*
quota values need a PoC on expression support — tiers-as-products is the fallback;
(4) quota increases from Microsoft remain form-based, not programmatic.

## Issue changes made

- **#81 [Epic] APIM GenAI gateway — real-time token quotas, 429 smoothing, and
  multi-deployment backend pools** (new), with sub-issues:
  - **#82** llm-token-limit policies via tiered APIM products (+ expression PoC)
  - **#83** backend pools, priority groups, 429 circuit breakers (`acceptRetryAfter`)
  - **#84** token metrics + `ApiManagementGatewayLlmLog` reconciliation + cost rate card
  - **#85** Anthropic/OpenAI front doors + Claude Code & Codex CLI onboarding docs
- Direction-update comments on **#7** (quota epic: tier-products supersede
  cache-store-value), **#10** (background services: sync→reconciliation, reset
  simplifies), **#13** (Bicep: APIM v2 + Foundry deployments now created by IaC),
  **#60** (Foundry provisioning: capacity semantics, resize, attestation, catalog).

## Docs site changes made

- New `architecture/feasibility.mdx` — the strategic assessment (verdict table,
  platform changes, control/data plane split, transient-limit mitigation, caveats,
  honest "drop-in" checklist).
- `reference/azure-rate-limits` rewritten: Claude GA + broadened eligibility,
  cache-aware RPM/ITPM/OTPM model with Sep 2026 defaults, hosting variants/regions,
  300-concurrent flagged unverified, Codex model defaults (flagged volatile), stale
  GPT-4.1/4o tier tables removed, gateway mitigation section.
- `architecture/overview.mdx`: enforcement section rewritten (real-time policy
  enforcement; usage sync = reconciliation + cost computation; reset simplification;
  key lifecycle now 403-exhaustion / suspend-deactivation / delete-offboarding).
- `getting-started/why-foundrygate.mdx`: comparison table + enforcement paragraph +
  scale caveat updated.
- `getting-started/cli-setup.mdx`: full rewrite with **verified** configs —
  Claude Code `CLAUDE_CODE_USE_FOUNDRY` env vars, Codex `config.toml`
  `[model_providers.azure]`, dual smoke-test curls, budget-header table,
  429/403/latency triage. (Previous page showed a nonexistent
  `claude config set apiBaseUrl` flow.)
- Fixed pre-existing rendering bug: `.md` pages using MDX imports (asides silently
  not rendering, import line shown as text) renamed to `.mdx`
  (azure-rate-limits, configuration, contributing).

## Issue changes made

_None yet._
