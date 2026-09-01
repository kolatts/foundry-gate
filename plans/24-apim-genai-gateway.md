# APIM GenAI gateway — real-time token quotas, 429 smoothing, and backend pools

> GitHub: #81
> Milestone: v0.5 — GenAI gateway
> Labels: epic, backend, infra

## Overview

This epic moves quota enforcement and rate smoothing from the application layer into
APIM's GenAI gateway policies, based on September 2026 platform research (full findings
in `fable-refactor.md`). APIM's `llm-token-limit` policy now supports monthly token
quotas per counter key with the Anthropic Messages API schema on v2 tiers — meaning
per-developer budgets are enforced in real time at the gateway (403), and per-developer
TPM caps smooth bursts (429 + Retry-After), for both Claude Code (Anthropic format) and
Codex CLI (OpenAI format) traffic. Backend pools with circuit breakers absorb
deployment-level 429s by failing over across Foundry deployments/regions — the Claude
substitute for spillover and PTU, which are OpenAI-only. FoundryGate's API becomes the
control plane; APIM is the data plane.

## Prerequisites / constraints

- **APIM v2 tier (Basic v2 minimum)** — Anthropic schema support in LLM policies is
  v2-only. Affects #13 (Bicep): APIM is provisioned by FoundryGate, no longer param-input.
- Token counters are per gateway instance — single-unit deployment assumed in v1.
- Quota windows are fixed calendar windows (UTC-truncated) — aligns with FoundryGate's
  monthly reset semantics out of the box.

## Approach

### llm-token-limit policies via tiered products (#82)

Quota tiers are APIM **products** (Standard / Power / Unlimited). Each product policy:

```xml
<llm-token-limit
    counter-key="@(context.Subscription.Id)"
    estimate-prompt-tokens="false"
    token-quota="{tier monthly budget}"
    token-quota-period="Monthly"
    tokens-per-minute="{tier TPM cap}"
    retry-after-header-name="retry-after"
    remaining-quota-tokens-header-name="x-fg-remaining-quota"
    remaining-tokens-header-name="x-fg-remaining-tpm" />
```

Quota exhaustion → 403 in real time; TPM burst → 429 + Retry-After. **PoC task**:
determine whether `token-quota`/`tokens-per-minute` accept policy expressions; if yes,
per-user arbitrary values come from `cache-lookup-value` and tiers collapse into one
product. Until then, the five-level quota resolution (#32) resolves to a **tier**, and
"set user quota" = move subscription between products. Verify Anthropic-path counting
against Claude's `usage` block (cache tokens divergence) and document.

### Backend pools + circuit breakers (#83)

One pool per model family. Primary deployment priority 1; secondary (other region /
DataZone / Anthropic-hosted variant) priority 2. Circuit breaker per backend: trip on
429/5xx, `acceptRetryAfter: true`. `retry` in the backend policy section retries onto
the pool (across backends, same model+version only). While priority-1 breakers are
tripped, traffic drains to priority 2; 503 only when the whole pool is down.

### Metrics + reconciliation (#84)

- `llm-emit-token-metric` (Subscription ID / Product ID dimensions) → App Insights
  dashboards. Caveat: ≤100 unique values per dimension, silent discard — dashboards
  only, not billing.
- Diagnostic setting "Logs related to generative AI gateway" → `ApiManagementGatewayLlmLog`
  joined to `ApiManagementGatewayLogs` on `CorrelationId` = authoritative per-request,
  per-subscription token counts. The usage-sync Function (#39) reads this KQL rollup,
  updates `QuotaAllocation.TokensUsed`, flags drift. Enforcement no longer depends on it.
- Cost attribution: Claude bills as one aggregate Marketplace CCU meter (no per-user
  breakdown possible in Cost Management) → per-developer cost = tokens × rate card
  stored in `SystemConfiguration`. OpenAI models cross-check via the `deployment`
  billing tag.

### API front doors + harness onboarding (#85)

Two pass-through APIs, subscription key header name set to `api-key`:

- `/anthropic/*` → `https://{foundry}.services.ai.azure.com/anthropic` (Anthropic
  Messages, for Claude Code: `CLAUDE_CODE_USE_FOUNDRY=1`,
  `ANTHROPIC_FOUNDRY_BASE_URL=https://{gateway}/anthropic`,
  `ANTHROPIC_FOUNDRY_API_KEY={subscription key}`, model env vars pinned to deployment
  names).
- `/openai/v1` → Azure OpenAI v1 path (for Codex CLI: `model_provider = "azure"`,
  `base_url = "https://{gateway}/openai/v1"`, `env_key`, `wire_api = "responses"`;
  API-key auth only — Codex has no Entra support).

Rewrite `docs-site/.../getting-started/cli-setup.mdx` (current Claude Code instructions
are wrong) and spec the `/me` "Configure your CLI" panel to emit these snippets.

## Verification

- [ ] Monthly quota 403 fires at the gateway with no sync lag (both API paths)
- [ ] TPM cap 429 + Retry-After fires per developer; other developers unaffected
- [ ] Pool failover: saturate priority-1 deployment, traffic continues via priority-2
- [ ] KQL rollup matches (±streaming estimate error) the tokens reported by model `usage`
- [ ] Claude Code and Codex CLI complete real sessions through the gateway
- [ ] Expression-support PoC documented; tier vs per-user decision recorded here
