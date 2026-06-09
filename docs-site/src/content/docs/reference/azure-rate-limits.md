---
title: Azure AI Foundry Rate Limits
description: How Azure AI Foundry's deployment-level TPM/RPM limits cap the number of concurrent developers FoundryGate can serve — and what to do about it.
---

import { Aside } from '@astrojs/starlight/components';

<Aside type="caution">
**Scale blocker**: Azure AI Foundry enforces hard rate limits at the deployment level, shared across all callers. These limits cap how many developers can actively use FoundryGate simultaneously, regardless of per-developer token budgets. For organisations with more than 50–100 active developers, these limits are likely the binding constraint — not FoundryGate's quota system.
</Aside>

## What the limits are

Azure AI Foundry imposes two limits on every model deployment in the **standard (pay-as-you-go)** tier:

- **Tokens per minute (TPM)** — the total input + output tokens that can be processed per rolling minute, across all callers.
- **Requests per minute (RPM)** — the number of API calls allowed per rolling minute, across all callers.

These limits apply to the **deployment**, not to an individual APIM subscription or developer. Every request FoundryGate routes to Foundry counts against the same pool.

### Default limits by model

These are the standard defaults for new deployments in major regions (East US, West Europe). Actual defaults vary by region and subscription history; limits can be raised via the Azure quota request process.

| Model | Default TPM | Default RPM | Max requestable TPM |
|---|---|---|---|
| GPT-4o | 450,000 | 2,700 | ~2,000,000+ (region-dependent) |
| GPT-4o mini | 2,000,000 | 12,000 | ~10,000,000+ |
| GPT-4 Turbo | 150,000 | 900 | ~600,000 |
| GPT-3.5 Turbo | 1,000,000 | 6,000 | ~4,000,000 |
| **Claude 3.5 Sonnet** | **200,000** | **1,000** | **~400,000** |
| **Claude 3 Sonnet** | **200,000** | **1,000** | **~400,000** |
| **Claude 3.5 Haiku** | **400,000** | **2,000** | **~800,000** |

<Aside type="note">
Claude models on Azure AI Foundry are provisioned through the Azure AI Model Catalog (Anthropic as a marketplace partner). Their TPM limits are significantly lower than equivalent OpenAI models and lower than what you would get calling Anthropic's API directly. Microsoft and Anthropic have not published a formal quota increase path for Claude on Azure equivalent to the Azure OpenAI quota request portal — increases typically require opening a support ticket.
</Aside>

---

## How many concurrent developers can one deployment support?

This depends on how actively developers are generating tokens. The key variable is **requests per active user per minute** — which varies widely between occasional chat usage and a continuously streaming AI code editor.

### Assumptions

| Variable | Light usage | Heavy usage |
|---|---|---|
| Avg tokens per request (input + output) | 2,000 | 4,000 |
| Requests per minute per active user | 1 | 6 |
| % of org active simultaneously | 15% | 30% |

### Concurrent developer estimates

#### GPT-4o at default 450K TPM

| Usage pattern | Requests/min capacity | Concurrent active users | Total org size supported |
|---|---|---|---|
| Light (2K tokens, 1 req/min) | 225 | 225 | ~1,500 |
| Moderate (2K tokens, 3 req/min) | 225 | 75 | ~250–500 |
| Heavy (4K tokens, 6 req/min) | 112 | 19 | ~65–130 |

#### Claude 3.5 Sonnet at default 200K TPM

| Usage pattern | Requests/min capacity | Concurrent active users | Total org size supported |
|---|---|---|---|
| Light (2K tokens, 1 req/min) | 100 | 100 | ~650 |
| Moderate (2K tokens, 3 req/min) | 100 | 33 | ~110–220 |
| Heavy (4K tokens, 6 req/min) | 50 | 8 | ~25–55 |

#### Claude 3.5 Sonnet at max quota ~400K TPM

| Usage pattern | Requests/min capacity | Concurrent active users | Total org size supported |
|---|---|---|---|
| Light (2K tokens, 1 req/min) | 200 | 200 | ~1,300 |
| Moderate (2K tokens, 3 req/min) | 200 | 67 | ~220–450 |
| Heavy (4K tokens, 6 req/min) | 100 | 17 | ~55–115 |

<Aside type="caution">
**Real-world expectation**: In practice, AI-assisted development workflows (code completion, inline suggestions, agentic loops) generate traffic closer to the **heavy** end of this table. An organisation with 200 developers expecting AI-assisted coding from Claude 3.5 Sonnet is likely to hit the default 200K TPM ceiling within minutes of a busy morning standup.
</Aside>

---

## Why FoundryGate's per-developer quotas don't solve this

FoundryGate's monthly token budgets and APIM suspension mechanism operate at a different layer than Azure's deployment-level TPM limits:

- **FoundryGate** controls *who gets access* and *how much they consume over a month*. It's a governance and fairness tool.
- **Azure Foundry TPM limits** control *how fast* the deployment can process requests *right now*, regardless of who is asking.

A developer within their monthly quota can still trigger a `429 Too Many Requests` from Foundry if the deployment is saturated by other users at that moment. FoundryGate currently surfaces this as a pass-through error — it does not retry, queue, or prioritise requests.

This means the effective scale ceiling for FoundryGate is determined by Azure's rate limits, not by how many users you provision or how large their monthly quotas are.

---

## Mitigations

### 1. Request quota increases (Azure OpenAI)

For OpenAI models, Azure provides a [quota increase request form](https://customervoice.microsoft.com/Pages/ResponsePage.aspx?id=v4j5cvGGr0GRqy180BHbR4xPXO648sJKt4GoXAed-0pUMFE2Rk5GSllYRDRMQVZZWlU5Q0tGUEJRRCQlQCN0PWcu) in the Azure portal. Increases to 2M–10M TPM for GPT-4o are achievable for enterprise subscriptions but typically require business justification and can take days to weeks.

For Claude models on Azure, quota increases are not self-service. Open a Microsoft Azure support ticket with your use case and expected peak TPM.

### 2. Provisioned Throughput Units (PTU) — OpenAI models only

Azure OpenAI's **Provisioned** tier pre-allocates model capacity to your deployment. PTU provides **dedicated, consistent throughput** rather than a shared rate limit:

- GPT-4o: approximately 2,500–6,000 TPM per PTU (varies by context and workload)
- Commitment: monthly or annual, billed per PTU regardless of utilisation
- Minimum purchase: typically 50–100 PTUs depending on the model

At 100 PTUs of GPT-4o (~250K–600K sustained TPM), the concurrent user ceiling shifts from Azure's shared limits to your own provisioned capacity. PTU is the correct architecture for more than ~100 active developers on GPT-4o.

Claude models on Azure **do not support PTU**. They are available only in the standard consumption-based tier, making quota limits the hard ceiling for Claude at scale on Azure.

### 3. Multiple Foundry deployments or regions

FoundryGate currently routes to a single Foundry endpoint via a single APIM backend. Deploying additional Foundry accounts or models in additional Azure regions and load-balancing across them at the APIM layer multiplies the effective TPM ceiling. This requires APIM backend pool configuration outside of FoundryGate's scope.

### 4. Reduce token consumption per request

- Lower `max_tokens` / `max_completion_tokens` defaults in requests sent through APIM.
- Apply APIM policies to strip unnecessarily large system prompts or enforce response length limits.
- Route lightweight tasks (embeddings, short completions) to cheaper models with higher default TPM.

### 5. Use gpt-4o-mini or GPT-3.5 Turbo as overflow

Both models have dramatically higher default TPM limits (2M and 1M respectively). Routing non-latency-sensitive or lower-stakes tasks to these models through a separate APIM backend frees GPT-4o and Claude capacity for tasks where model quality matters.

---

## Planning for your deployment

Use this rough sizing guide when deploying FoundryGate:

| Team size | Recommended approach |
|---|---|
| < 50 developers | Standard tier, single deployment. Default limits are sufficient for most usage patterns. |
| 50–150 developers | Request a quota increase to 1M+ TPM. Monitor for 429s in Application Insights. |
| 150–500 developers | PTU for GPT-4o (if primary model). Multiple Claude deployments across regions if using Claude. Budget for support tickets. |
| 500+ developers | Multi-region APIM backend pools. PTU is mandatory for OpenAI models. Claude at this scale on Azure requires a Anthropic enterprise agreement routed directly, not through Azure AI Foundry. |

<Aside type="tip">
FoundryGate's per-developer usage data (visible in the admin dashboard) is your best tool for right-sizing Foundry capacity. Export a week of usage, identify your 90th-percentile concurrent-request minutes, and use that as input to the quota planning formula above.
</Aside>
