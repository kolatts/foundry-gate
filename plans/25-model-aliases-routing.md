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

One APIM policy fragment (`fg-model-alias`), running *before* `llm-token-limit`:

- Parse `model` from the request body (no `model` — e.g. `GET /openai/v1/models` — is a
  no-op).
- Look up the alias in a per-product named value (`fg-model-map-{productId}`, JSON:
  alias → `{ deployment, backend, provider }`; absent, `null`, or missing any of the
  three fields = blocked).
- Miss/blocked → `return-response` 403 with a `model_not_permitted` error body in the
  API's native error schema.
- **Wrong front door** → 403 as well. A tier's map covers both providers, so `sonnet`
  requested through the OpenAI front door would otherwise be rewritten to a Claude
  deployment and routed at the Anthropic pool, dying as an opaque 404. The entry's
  `provider` is compared against the current API and the refusal names the base path the
  caller should have used. `provider` is a separate field from `pool` deliberately: they
  coincide today, but an Anthropic DataZone or secondary pool would split them.
- Hit → `set-body` with the real deployment name, `set-backend-service` to the mapped
  pool/backend.

The machine-readable code travels in an `x-fg-error` header rather than the body,
because Anthropic's native error envelope (`{type, error:{type, message}}`) has no
`code` field — putting one there would be inventing wire format the SDK does not expect.

**Where the fragment is included — product scope, not API scope.** `{{named-value}}`
tokens resolve by literal name, so a shared fragment cannot compute
`fg-model-map-{productId}` at runtime; only the per-tier product policy (rendered once
per tier by Bicep) can hand the map in, which it does via a `fgModelMap` variable set
immediately before the include. This also satisfies the ordering requirement for free:
APIM evaluates global → product → API, so the allowlist runs ahead of `llm-token-limit`
(itself product-scope, #82) and a blocked model costs the developer no quota.

**One fragment with a schema branch, not two fragments.** Only the ~6 lines that build
the 403 body differ between the two front doors (Anthropic `permission_error` envelope
vs OpenAI `error` object); the parse/lookup/rewrite/route logic is identical. The
fragment branches on `context.Api.Id`, and Bicep substitutes the real Anthropic API name
into the comparison so the branch never depends on a hand-copied magic string. Two
fragments would have reintroduced exactly the duplication this work removed.

The API policies keep a `set-backend-service` *before* their `<base />`, so the pool
(Anthropic) / OpenAI backend remains the default route and the fragment overrides it
per-model — a request the map never touches still routes sensibly.

Bicep: alias maps as a parameter on `infra/main.bicep` (`productModelAliases`, threaded
to `infra/modules/ai-gateway.bicep`), emitted as one named value per product + one
`policyFragments` resource. The control-plane app edits named values (Management API)
when admins retarget an alias — no policy redeploy. A tier with no map entry permits no
models: the allowlist fails loud, by design.

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

- [x] `infra/modules/ai-gateway.bicep` — per-tier named values, the `fg-model-alias`
      policy fragment, product policy wiring
- [x] `infra/policies/model-alias-fragment.xml` — the fragment
- [x] `infra/main.bicep` — `productModelAliases` param (`deployment` / `pool` /
      `provider` per alias); Claude models moved into the pooled deployment set so every
      alias exists in every pool member
- [x] `infra/policies/product-policy.xml` — includes the fragment ahead of
      `llm-token-limit` (API policies keep only the default `set-backend-service`)
- [x] `infra/policies/anthropic-api.xml`, `infra/policies/openai-api.xml` — static
      `set-backend-service` demoted to a pre-`<base />` default so the alias map wins
- [ ] `docs-site/src/content/docs/getting-started/cli-setup.mdx` — document alias names
      as the values for `ANTHROPIC_DEFAULT_*_MODEL` / Codex `model`. **Deliberately not
      done yet**: CLAUDE.md requires cli-setup to contain only empirically verified
      configuration, and aliases have not been exercised against a live gateway.
- [x] Control plane: admin endpoints to edit alias maps via the Management API — `GET/PUT
      /api/v1/gateway/tiers/{tier}/models` plus the `/models` admin page (#225). Validation
      refuses a map the gateway would answer with a 404 rather than an honest 403 (alias
      grammar, deployment existence, and existence in *every* pool member for an
      `anthropic`-pool alias). The named-value round trip against real APIM is #226.

## Verification

Static verification (no live APIM exists; run against this branch):

- [x] `pwsh ./scripts/validate-policies.ps1` — the fragment is well-formed XML after
      Bicep's token substitution, and the `include-fragment` in the product policy
      resolves to it
- [x] `az deployment group what-if` on `modules/ai-gateway.bicep` — each
      `fg-model-map-{tier}` named value renders the expected alias JSON with the logical
      pool resolved to a real backend id and a `provider` on every entry (`unlimited`
      alone carries `opus`), and the ARM-rendered fragment re-parses as well-formed XML
      with the API id and both front-door paths substituted in
- [x] `az deployment sub validate` / `group validate` — the `policyFragments` and
      `namedValues` resources pass ARM preflight

Live verification (all **pending next live deploy**):

- [ ] `sonnet` alias resolves and completes through the Anthropic front door
- [ ] Alias absent from product map → 403 `model_not_permitted` (native error schema on
      each front door)
- [ ] A Claude alias sent to the OpenAI front door (and vice versa) → 403 naming the
      correct base path, instead of a 404 from the wrong pool
- [ ] Named-value JSON survives `{{...}}` substitution into the `set-variable` attribute
      (embedded quotes) — the one mechanism here with no offline proof
- [ ] Retargeting an alias via named-value update takes effect without policy redeploy
- [ ] `llm-token-limit` still counts tokens correctly after `set-body` rewrite
- [ ] Pool selection follows the alias map (verified via `ApiManagementGatewayLogs`)
