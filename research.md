# Platform Research — APIM GenAI Gateway & Microsoft Foundry (September 2026)

> Condensed findings from two research passes over live Microsoft Learn / Anthropic
> documentation, 2026-09-01, feeding the gateway-centric direction in
> `fable-refactor.md`, `plans/24-apim-genai-gateway.md`, and epic #81.
> Naming: Azure AI Foundry is now branded **Microsoft Foundry**.

## 1. APIM LLM policies

### `llm-token-limit` — GA, all tiers except Consumption
[Policy doc](https://learn.microsoft.com/en-us/azure/api-management/llm-token-limit-policy)

- Supported payload schemas: OpenAI Chat Completions / Responses, **Anthropic Messages
  API (v2 tiers only)**, Google Vertex.
- `tokens-per-minute` — rolling TPM per `counter-key` → `429` + `Retry-After`
  (header/variable names customizable).
- **`token-quota` + `token-quota-period="Hourly|Daily|Weekly|Monthly|Yearly"`** —
  fixed UTC-truncated calendar windows → **`403`** on exhaustion. Both may be combined
  in one policy; policy may be applied multiple times with different counter keys
  (per-dev + global caps).
- `counter-key` accepts expressions (`@(context.Subscription.Id)`).
- Headers: `remaining-tokens…`, `remaining-quota-tokens…` (estimate — tightens near
  the limit), `tokens-consumed…`.
- `estimate-prompt-tokens="true"` pre-estimates and can reject pre-backend; `false`
  uses actual `usage` (one request can slip past the limit, then blocks).
- **Streaming always estimates** both prompt and completion tokens. Near-concurrent
  requests can briefly exceed limits (response-driven accounting).
- **Counters are per gateway instance** — not aggregated across units/regions.
- Counts prompt + completion only — Anthropic cache-read/creation token handling
  **unverified**; needs PoC (#82) vs Claude's `usage` block and Foundry ITPM accounting.

### `llm-emit-token-metric` — GA (extended token categories in preview)
[Policy doc](https://learn.microsoft.com/en-us/azure/api-management/llm-emit-token-metric-policy)

- Custom metrics → App Insights; built-in dimensions incl. **Subscription ID, Product
  ID, User ID**, API ID, Backend ID.
- Caps: ≤5 custom dimensions; **≤100 unique values per dimension and ≤1,000 active
  series per namespace — silently discarded beyond**. Dashboards only; not billing.

### `llm-content-safety` — GA, non-Consumption
[Policy doc](https://learn.microsoft.com/en-us/azure/api-management/llm-content-safety-policy)
Requires a separate AI Content Safety resource. Relevant because Foundry provides no
built-in content filtering for Claude models.

## 2. Backends, pools, circuit breakers, retry
[Backends doc](https://learn.microsoft.com/en-us/azure/api-management/backends) ·
[Multi-backend gateway guide](https://learn.microsoft.com/en-us/azure/architecture/ai-ml/guide/azure-openai-gateway-multi-backend)

- Pools: ≤30 backends; round-robin, **weighted**, **priority groups** (lower priority
  used only while all higher-priority breakers are tripped); optional session affinity.
- Circuit breaker: trip rules on status ranges (e.g. 429/5xx) over an interval;
  **`acceptRetryAfter: true` honors the backend's `Retry-After`** (AI 429s can carry
  very long values). One rule per backend; not in Consumption; per-instance state.
  All tripped → client gets 503.
- `retry` policy: condition on `context.Response.StatusCode == 429`; the documented
  shape is **retry onto a different backend**, not wait-and-retry; no built-in
  honor-Retry-After attribute (that lives in the breaker).
- **No queueing primitive in APIM** — CLI client backoff is the queue.
- Reference material: [AI gateway capabilities](https://learn.microsoft.com/en-us/azure/api-management/genai-gateway-capabilities),
  [Azure-Samples/AI-Gateway labs](https://github.com/Azure-Samples/AI-Gateway),
  [AI Hub Gateway accelerator](https://github.com/Azure-Samples/ai-hub-gateway-solution-accelerator)
  (Event Hub → Cosmos → Power BI chargeback pattern for large scale).

## 3. APIM tiers
[v2 tiers overview](https://learn.microsoft.com/en-us/azure/api-management/v2-service-tiers-overview)

- Anthropic schema in LLM policies and the Foundry portal AI-gateway integration:
  **v2 tiers only** (Basic v2 / Standard v2 / Premium v2 — all GA).
- Consumption tier: no token policies, no circuit breaker — not viable.
- **Cheapest viable tier: Basic v2** (~$210/unit/mo per third-party trackers —
  unverified, confirm via pricing calculator). v2 gaps: no multi-region, no
  self-hosted gateway.
- Foundry portal "AI gateway" integration (preview) enforces TPM/quota **per Foundry
  project**, not per developer — validates FoundryGate's niche.
  [Doc](https://learn.microsoft.com/en-us/azure/ai-foundry/configuration/enable-ai-api-management-gateway-portal)

## 4. Claude on Microsoft Foundry
[Claude models concept](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/claude-models) ·
[Partner models](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-from-partners) ·
[Anthropic doc](https://platform.claude.com/docs/en/build-with-claude/claude-in-microsoft-foundry)

- **GA ~July 2026.** Eligibility broadened: any **paid subscription with active
  pay-as-you-go billing method** (excluded: CSP, free trial/student/credit-only,
  EA South Korea). Deploying principal needs Marketplace/SaaS permissions.
- API: **Anthropic Messages only** — `POST https://{resource}.services.ai.azure.com/anthropic/v1/messages`
  (+ `count_tokens`), `anthropic-version: 2023-06-01`; key or Entra bearer auth.
  No Batches/Models/Admin API; **no `anthropic-ratelimit-*` response headers**.
- Models (GA, Azure-hosted "v2"): claude-opus-5, claude-opus-4-8, claude-sonnet-5,
  claude-haiku-4-5 (+ sonnet-4-6; legacy sonnet-4-5 et al.; claude-fable-5 preview,
  Anthropic-hosted only). Version field encodes hosting: `1` = Anthropic infra
  (eastus2, swedencentral), `2` = Azure (~9 US/EU regions).
- Deployment types: **Global Standard + Data Zone Standard (US) only. No PTU** →
  no spillover, no priority processing, no batch for Claude.
- **Cache-aware rate limits**, pooled per subscription per model across regions:
  RPM / uncached-input TPM (ITPM) / output TPM (OTPM); cache reads free against ITPM.
  - PAYG: opus-class & sonnet-5: 40 / 40K / 8K; sonnet-4-6, sonnet-4-5, haiku-4-5:
    80 / 80K / 16K.
  - Enterprise/MCA-E: 2,000 / 2M / 400K (4,000 / 4M / 800K for the second group).
- 300-concurrent-request cap: still in the generic partner-model table, absent from
  Claude's own docs — **unverified whether it applies to Claude today**.
- Quota increases: [form-based only](https://aka.ms/oai/stuquotarequest); Claude is
  the only partner family eligible; not programmatic.
- **Billing**: Marketplace metering in **Claude Consumption Units (CCU)** — Azure Cost
  Management shows a **single aggregate line**, no per-deployment/per-user breakdown.
  Per-developer cost must be computed from token telemetry × published rates.
  [CCU billing](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/claude-models-billing)

## 5. Bicep provisioning
[Deploy Claude via Bicep/Terraform](https://learn.microsoft.com/en-us/azure/developer/ai/how-to/deploy-claude-foundry) ·
[Azure-Samples/claude starter kit](https://github.com/Azure-Samples/claude)

- `Microsoft.CognitiveServices/accounts` kind `AIServices` sku `S0`
  (`customSubDomainName` required; `allowProjectManagement: true`) + child
  `accounts/deployments`; api-version `2025-10-01-preview` (starter kit) /
  `2026-01-15-preview` (latest).
- Claude: `model.format: 'Anthropic'` + **`modelProviderData`** (organizationName,
  countryCode, industry) — **auto-accepts the Marketplace offer**, no portal
  click-through. OpenAI models: `format: 'OpenAI'`. Both coexist in one account.
- **`sku.capacity` = thousands of TPM** (25 → 25K TPM). Deployment capacity is
  **PATCH-able at runtime** ([Deployments Update](https://learn.microsoft.com/en-us/rest/api/aiservices/accountmanagement/deployments/update?view=rest-aiservices-accountmanagement-2024-10-01)).
- Limits: 100 Foundry resources/region/sub; **32 deployments/resource**; creates
  serialize (concurrent → 409, chain `dependsOn`); soft-deleted accounts hold quota
  ≤48h (purge to reclaim).
- Quota reads: `locations/{loc}/usages` (currentValue vs limit);
  `modelCapacities` list (Anthropic format **unverified**).

## 6. Harness support

### Claude Code — official Foundry support
[code.claude.com/docs/en/microsoft-foundry](https://code.claude.com/docs/en/microsoft-foundry) ·
[MS mirror](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/configure-claude-code) ·
[APIM + Claude clients blog](https://techcommunity.microsoft.com/blog/azure-ai-foundry-blog/connecting-claude-clients-with-azure-api-management-and-claude-models-in-microso/4525212)

- `CLAUDE_CODE_USE_FOUNDRY=1`; `ANTHROPIC_FOUNDRY_BASE_URL` (**arbitrary URL — APIM
  front door is the documented pattern**) or `ANTHROPIC_FOUNDRY_RESOURCE`.
- Auth precedence: `ANTHROPIC_FOUNDRY_AUTH_TOKEN` (≥ v2.1.203) >
  `ANTHROPIC_FOUNDRY_API_KEY` > DefaultAzureCredential.
- Pin `ANTHROPIC_DEFAULT_{OPUS,SONNET,HAIKU}_MODEL` to deployment names (unpinned
  aliases resolve to defaults that may not exist as deployments).
- Prompt caching auto-on (`ENABLE_PROMPT_CACHING_1H=1` for 1h TTL).

### Codex CLI — official Azure support
[Codex with Azure OpenAI](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/codex)

- `~/.codex/config.toml`: `model = "{deployment}"`, `model_provider = "azure"`,
  `[model_providers.azure]` with `base_url = "https://…/openai/v1"`,
  `env_key = "…"` (env var name, not a literal), `wire_api = "responses"`.
- **API-key auth only — no Entra support.**
- Codex models on Azure: gpt-5-codex, gpt-5.1-codex/-mini/-max, gpt-5.2-codex,
  gpt-5.3-codex (+ gpt-5/5.1+ chat). Default TPMs volatile (~15K–150K Global
  Standard reported; community reports of unannounced cuts) — read
  `az cognitiveservices usage list` rather than trusting tables.

## 7. OpenAI-only traffic features (for contrast)

| Feature | Status | Claude? |
|---|---|---|
| Spillover (provisioned→standard) | GA (Aug 2025) | No (no PTU) |
| Priority processing | Live (GA label unverified) | No |
| Dynamic quota | Preview | No |
| Global Batch | GA | No |
| APIM AI gateway policies | GA (Anthropic schema on v2) | **Yes** |

## 8. Metrics & usage reporting

- Authoritative per-request tokens: diagnostic setting "Logs related to generative AI
  gateway" → **`ApiManagementGatewayLlmLog`** (prompt/completion/total, model,
  optional payloads) joined to `ApiManagementGatewayLogs` on `CorrelationId` for the
  subscription ID. [LLM logs doc](https://learn.microsoft.com/en-us/azure/api-management/api-management-howto-llm-logs)
- Near-real-time dashboards: `llm-emit-token-metric` (cardinality caps above).
- Interrupted streams → missing/inaccurate token counts in both paths.

## Open items to verify by PoC

1. Anthropic cache-read/creation token handling in `llm-token-limit` (vs `usage`).
2. Whether `token-quota`/`tokens-per-minute` accept policy expressions (per-user
   values vs tier-products design) — #82.
3. 300-concurrent cap applicability to Claude.
4. `modelCapacities` API with `modelFormat=Anthropic`.
5. Exact APIM v2 unit pricing (calculator).
6. Codex default TPM values at deploy time.
