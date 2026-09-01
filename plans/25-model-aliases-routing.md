# Model aliases, access control, and routing posture

> GitHub: #86
> Milestone: v0.5 — GenAI gateway
> Labels: epic, backend, infra

## Overview

September 2026 investigation into classifiers and routers as a FoundryGate feature add
(full findings and sources in `fable-refactor-log.md`). Conclusion: **classification-based
routing is a chat-tier feature and is wrong for agentic CLI traffic** — every serious
gateway (LiteLLM, OpenRouter provider routing, Portkey, Microsoft's AI Hub Gateway
accelerator) builds its core on aliases + explicit conditions + health/priority failover,
not prompt classification. Claude Code already self-classifies (its haiku lane), pins
model names per request, and depends on prompt-cache prefix continuity; silently swapping
models breaks cache economics, capability envelopes, and context-window guarantees.

What FoundryGate adopts instead:

1. **Model alias map** — the gateway exposes stable virtual model names (`sonnet`,
   `haiku`, `opus`, `gpt-codex`); an inbound policy rewrites them to real deployment
   names and selects the matching backend pool. Deployments/versions rotate underneath
   via Bicep without any developer changing env vars.
2. **Per-product model access control** — a product's alias map is also its allowlist;
   a model not in the map returns 403 `model_not_permitted` (e.g. opus blocked for an
   intern tier). Deterministic, fails loud, zero added latency.
3. **Failover-only dynamic routing** — backend pools + circuit breakers (#83) remain the
   *only* routing that changes a request's destination, and only within the same
   model+version.

## Explicitly rejected (recorded so it isn't relitigated)

- **Azure Foundry Model Router** for CLI traffic: OpenAI-schema only (unreachable from
  Claude Code's Anthropic Messages path), context window bounded by smallest pool model,
  nondeterministic model per request destroys prompt-cache continuity, drops sampling
  params on o-series, ~$0.14/M input markup. May later be exposed as an opt-in
  `auto-chat` alias for non-agentic apps (a config row under this design, not a feature).
- **APIM semantic caching** (`llm-semantic-cache-lookup/store`): needs Managed Redis +
  RediSearch + a per-request embeddings hop; agentic conversations have near-zero
  cross-request semantic duplication, and a false-positive cache hit injects a stale
  answer into a tool-use loop. Anthropic prompt caching (rewarded by Foundry's
  cache-aware ITPM limits) already provides the economics.
- **Gateway-side prompt classifiers** (cheap-model downgrade, task-type routing): a
  serial LLM hop taxes every streamed request and violates the harness contract that
  the responding model equals the pinned model.

## Approach

### Alias map + allowlist policy fragment

One APIM policy fragment, included in both front doors' inbound section *before*
`llm-token-limit`:

- Parse `model` from the request body.
- Look up the alias in a per-product named value (`fg-model-map-{productId}`, JSON:
  alias → `{ deployment, pool }`; `null` = blocked).
- Miss/blocked → `return-response` 403 with a `model_not_permitted` error body in the
  API's native error schema.
- Hit → `set-body` with the real deployment name, `set-backend-service` to the mapped
  pool.

Bicep: alias maps as a parameter on `infra/modules/ai-gateway.bicep`, emitted as one
named value per product + one `policyFragments` resource. The control-plane app edits
named values (Management API) when admins retarget an alias — no policy redeploy.

### Optional content safety (separate toggle, default off)

`llm-content-safety` with `shield-prompt="true"` is available as an opt-in per-product
fragment for less-trusted populations (Bicep-conditional Content Safety resource +
managed-identity role assignment). Default off for internal developer products.
Streaming caveat documented: response-side enforcement silently stops the stream (no
403) — CLI tenants should set `enforce-on-completions="false"`.

### Watchlist (no build)

- APIM **Unified Model API** (preview): alias mapping + OpenAI↔Anthropic schema
  translation at the gateway. Revisit at GA; today it is OpenAI-facing only and adds a
  translation layer Claude Code doesn't need.
- `llm-emit-token-metric` extended dimensions (cached/reasoning tokens, GA Build 2026)
  are adopted under #84, not here.

## Files expected to be created or modified

- `infra/modules/ai-gateway.bicep` — named values, policy fragment, product policy wiring
- `infra/policies/model-alias-fragment.xml` — the fragment
- `infra/policies/anthropic-api.xml`, `infra/policies/openai-api.xml` — include fragment
- `docs-site/src/content/docs/getting-started/cli-setup.mdx` — document alias names as
  the values for `ANTHROPIC_DEFAULT_*_MODEL` / Codex `model`
- Control plane (later, with #82): admin endpoint to edit alias maps via Management API

## Verification

- [ ] `sonnet` alias resolves and completes through the Anthropic front door
- [ ] Alias absent from product map → 403 `model_not_permitted` (native error schema)
- [ ] Retargeting an alias via named-value update takes effect without policy redeploy
- [ ] `llm-token-limit` still counts tokens correctly after `set-body` rewrite
- [ ] Pool selection follows the alias map (verified via `ApiManagementGatewayLogs`)
