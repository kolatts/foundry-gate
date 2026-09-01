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

Quota tiers are APIM **products** (Standard / Power / Unlimited), one rendered policy
per tier from `infra/policies/product-policy.xml` via the `quotaTiers` param. Each
product policy:

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

Quota exhaustion → 403 in real time; TPM burst → 429 + Retry-After.

**PoC answered (live, 2026-09-01): `token-quota` REJECTS policy expressions** —
"Expression return type 'System.Int32' is not allowed". Per-user arbitrary quotas in a
single policy are impossible, so **tiers-as-products is the design**: the five-level
quota resolution (#32) resolves to a **tier**, and "set user quota" = issue/move the
developer's APIM subscription against another tier product. Still to verify live:
Anthropic-path counting against Claude's `usage` block (cache-token divergence).

**Scope split (implemented).** APIM evaluates inbound as global → product → API (an API
policy's `<base />` expands the product policy), which fixes where each concern must go:

| Scope | Owns | Why |
|---|---|---|
| Product (`product-policy.xml`, rendered per tier) | model allowlist (#86 fragment), `tokens-per-minute`, `token-quota` | Entitlement. `token-quota` is a literal, so it must be rendered per tier; and everything that has to run *before* token counting must live in the outer scope. |
| API (`anthropic-api.xml`, `openai-api.xml`) | credential stripping + MI backend auth, default backend, retry/streaming, token metrics | Mechanism. Identical for every tier, so it renders once per API. |

`llm-token-limit` is declared in **exactly one** scope (the product). Declaring it at
both scopes would count every request twice; `scripts/validate-policies.ps1` asserts
this statically.

**The bypass that split creates, and its guard.** If every meter lives at product scope,
a subscription with no product context never runs any of it — APIM's built-in all-access
("master") subscription being the obvious case, plus any API-scoped or fork-created
subscription. The `fg-require-product` fragment is included in both API policies *before*
their `<base />`, i.e. ahead of where product scope would have run, and returns 403 when
`context.Product == null`.

Deactivating the built-in subscription in Bicep was considered and **rejected**: it would
mean PUTing `Microsoft.ApiManagement/service/subscriptions/master`, whose required
`scope` value for a built-in subscription this template cannot set with confidence —
guessing it risks rewriting a live built-in subscription's scope on every deploy. The
policy guard also covers strictly more cases (subscriptions created outside a tier
product later), so it is the enforcement mechanism, not a stopgap. Revisit if a live
deploy confirms a safe resource shape.

### Backend pools + circuit breakers (#83)

One pool per model family. The Anthropic pool is generated from `foundryRegions`: the
**first region is priority 1**, every later region **priority 2** — spillover, not
round-robin, so normal traffic stays in-region (latency + Claude prompt-cache affinity)
and the other regions are standing headroom. Circuit breaker per backend: trip on
429/5xx, `acceptRetryAfter: true`. `retry` in the backend policy section retries onto
the pool (across backends, same model+version only). While priority-1 breakers are
tripped, traffic drains to priority 2; 503 only when the whole pool is down. Weight is
only meaningful *within* a priority group; both are overridable per account.

What multi-region pooling does **not** buy: Claude GlobalStandard quota is pooled
per-subscription per-model across regions, so a second region multiplies *availability*
against deployment-level throttling, not the subscription's token budget. Extra
*subscriptions* are what multiply Claude headroom (see D-009).

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

Two pass-through APIs, each accepting the subscription key in the header its CLI
actually sends (wire-verified 2026-09-01): `x-api-key` for the Anthropic front door
(Claude Code), `api-key` for the OpenAI front door:

- `/anthropic/*` → `https://{foundry}.services.ai.azure.com/anthropic` (Anthropic
  Messages, for Claude Code: `CLAUDE_CODE_USE_FOUNDRY=1`,
  `ANTHROPIC_FOUNDRY_BASE_URL=https://{gateway}/anthropic`,
  `ANTHROPIC_FOUNDRY_API_KEY={subscription key}`, model env vars pinned to deployment
  names).
- `/openai/v1` → Azure OpenAI v1 path (for Codex CLI: `model_provider = "azure"`,
  `base_url = "https://{gateway}/openai/v1"`, `wire_api = "responses"`, and
  `env_http_headers = { "api-key" = "<ENV>" }` — `env_key` alone sends
  `Authorization: Bearer` and gets 401; API-key auth only, Codex has no Entra
  support).

Rewrite `docs-site/.../getting-started/cli-setup.mdx` (current Claude Code instructions
are wrong) and spec the `/me` "Configure your CLI" panel to emit these snippets.

## Files

- [x] `infra/main.bicep` — `quotaTiers` + `productModelAliases` params, pool
      priority/weight derived from the `foundryRegions` index, tier product outputs
- [x] `infra/modules/ai-gateway.bicep` — tier products + per-tier product policies,
      priority-grouped pool, policy fragments, per-tier alias named values
- [x] `infra/policies/product-policy.xml` — per-tier enforcement template (new)
- [x] `infra/policies/anthropic-api.xml`, `infra/policies/openai-api.xml` — reduced to
      API mechanics; shared preamble folded into fragments
- [x] `infra/policies/backend-auth-fragment.xml`,
      `infra/policies/token-metrics-fragment.xml` — shared fragments (new)
- [x] `infra/policies/require-product-fragment.xml` — refuses subscriptions with no tier
      product, closing the product-scope-only enforcement bypass (new)
- [x] `infra/parameters/test.bicepparam` — test-sized tiers
- [x] `scripts/validate-policies.ps1` — offline policy-XML validation (new)

## Verification

Static verification (no live APIM exists; run against this branch):

- [x] `az bicep build --file infra/main.bicep` compiles clean (only the expected BCP081
      warnings for the 2026-07-01 CognitiveServices api-version)
- [x] `pwsh ./scripts/validate-policies.ps1` — all seven policy documents are well-formed
      XML under **both** render variants (quota-configured and the unlimited tier's empty
      `__QUOTA_ATTRS__`), no placeholder survives, no unknown placeholder exists, every
      `include-fragment` resolves to a fragment file, `llm-token-limit` is declared in
      exactly one scope, and the `token-quota` attribute is present in exactly the
      variant that should carry it
- [x] `az deployment sub validate` against `infra/parameters/test.bicepparam`
      (`createModelDeployments=false`) — Succeeded
- [x] `az deployment group validate` + `what-if` of `modules/ai-gateway.bicep` — all 31
      APIM child resources pass ARM preflight; the ARM-rendered product policies, API
      policies and fragments re-parse as well-formed XML, the pool renders
      priority 1 / priority 2, and each `fg-model-map-{tier}` named value renders the
      expected alias JSON

Live verification (all **pending next live deploy**):

- [ ] Monthly quota 403 fires at the gateway with no sync lag (both API paths) —
      *proved on the pre-tier, API-scope policy (T5); needs re-proof now that
      `llm-token-limit` moved to product scope*
- [ ] TPM cap 429 + Retry-After fires per developer; other developers unaffected —
      *same: proved at API scope (T4), re-prove at product scope*
- [ ] Each tier product enforces its own literal quota, and moving a subscription
      between tier products changes the enforced budget
- [ ] A call with APIM's built-in all-access ("master") subscription key is refused 403
      by `fg-require-product` on both front doors, in each API's native error schema
- [ ] Pool failover: saturate priority-1 deployment, traffic continues via priority-2
- [ ] `llm-emit-token-metric` accepts the `Product ID` dimension and it appears in
      customMetrics
- [ ] KQL rollup matches (±streaming estimate error) the tokens reported by model `usage`
- [ ] Claude Code and Codex CLI complete real sessions through the gateway — *Codex done
      (T11); Claude Code blocked on a working Claude deployment (#88)*
- [x] Expression-support PoC documented; tier vs per-user decision recorded here
