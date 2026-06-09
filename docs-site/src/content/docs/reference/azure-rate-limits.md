---
title: Azure AI Foundry Rate Limits
description: How Azure AI Foundry's deployment-level TPM/RPM limits cap the number of concurrent developers FoundryGate can serve — and what to do about it.
---

import { Aside } from '@astrojs/starlight/components';

<Aside type="caution">
**Scale blocker**: Azure AI Foundry enforces hard rate limits at the deployment level, shared across all callers. These limits cap how many developers can actively use FoundryGate simultaneously, regardless of per-developer token budgets. For organisations with more than 50–100 actively generating developers, the deployment-level ceiling is likely the binding constraint — not FoundryGate's quota system.
</Aside>

## What the limits are

Azure AI Foundry imposes two limits on every model deployment:

- **Tokens per minute (TPM)** — total input + output tokens processed per rolling minute, across all callers sharing that deployment.
- **Requests per minute (RPM)** — number of API calls allowed per rolling minute, across all callers.

These limits apply to the **deployment**, not to an individual APIM subscription or developer. Every request FoundryGate routes to Foundry counts against the same shared pool.

<Aside type="note">
Rate windows are evaluated over **1-second and 10-second intervals**, not only per-minute rolling windows. A burst of requests can trigger `429 Too Many Requests` even when the per-minute average is well within quota.
</Aside>

---

## Claude models on Azure AI Foundry

### Enterprise subscription requirement

<Aside type="caution">
**Access blocker before rate limits are even relevant**: Claude models on Azure AI Foundry are available only on **Enterprise Agreement (EA) and Microsoft Customer Agreement – Enterprise (MCA-E) subscriptions**. Pay-as-you-go, MSDN, Visual Studio, student, free trial, and CSP subscriptions receive a default quota of **0** — Claude deployments exist in the portal but cannot process any traffic.
</Aside>

If your Azure subscription is not EA or MCA-E, Claude is not a viable model choice through Azure AI Foundry. Consider the direct Anthropic API or waiting for broader subscription eligibility.

### Available Claude models and limits

All Claude deployments are **Global Standard** type only. Two regions are supported: **East US2** and **Sweden Central**.

| Model | Default RPM (non-EA) | Default TPM (non-EA) | Enterprise RPM | Enterprise TPM |
|---|---|---|---|---|
| claude-opus-4-8, 4-7, 4-6, 4-5, 4-1 | **0** | **0** | 2,000 | 2,000,000 |
| claude-sonnet-4-6 | **0** | **0** | 2,000 | 2,000,000 |
| claude-sonnet-4-5 | **0** | **0** | 4,000 | 2,000,000 |
| claude-haiku-4-5 | **0** | **0** | 4,000 | 4,000,000 |

In addition to TPM and RPM, Azure AI Foundry imposes a **300 concurrent request** cap per deployment for Foundry (non-OpenAI) models, including all Claude variants.

Quota above these Enterprise defaults requires submitting a [quota increase request](https://aka.ms/oai/stuquotarequest). There is no published ceiling; increases are evaluated individually. There is no self-service path equivalent to Azure OpenAI's quota portal for Claude — contact Microsoft support.

**Claude does not support Provisioned Throughput Units (PTU).** All Claude capacity is standard consumption-based with shared rate limits. There is no way to purchase dedicated, isolated throughput for Claude on Azure AI Foundry.

---

## Azure OpenAI models (GPT series)

### Tiered quota system

Azure OpenAI uses a 7-tier quota system (Tier 0–6), assigned automatically based on subscription type and historical usage. Enterprise/MCA-E customers start higher; tiers increase automatically as consumption grows.

### GPT-4.1 (current primary GPT-4-class model)

| Tier | Global Standard TPM | Global Standard RPM | Data Zone Standard TPM |
|---|---|---|---|
| Tier 1 | 1,000,000 | 1,000 | 300,000 |
| Tier 2 | 3,000,000 | 3,000 | — |
| Tier 3 | 9,000,000 | 9,000 | — |
| Tier 4 | 18,000,000 | 18,000 | — |
| Tier 5 | 30,000,000 | 30,000 | — |
| Tier 6 | 45,000,000 | 45,000 | — |

### GPT-4o

GPT-4o is not in the new tier tables (those cover gpt-4.1, gpt-5, o4-mini). For reference, GPT-4o Standard deployments historically started at approximately 300,000–450,000 TPM depending on region and subscription type.

### GPT-4o-mini

| Tier | Global Standard TPM | Global Standard RPM | Data Zone Standard TPM |
|---|---|---|---|
| Tier 1 | 2,000,000 | 20,000 | 1,000,000 |
| Tier 2 | 9,000,000 | 90,000 | — |
| Tier 3 | 33,000,000 | 330,000 | — |
| Tier 4 | 78,000,000 | 780,000 | — |
| Tier 5 | 150,000,000 | 1,500,000 | — |
| Tier 6 | 225,000,000 | 2,250,000 | — |

### Provisioned Throughput Units (PTU)

GPT models support PTU: purchased dedicated capacity with no shared rate limits, billed per PTU regardless of utilisation. Minimum purchase is typically 50–100 PTUs. PTU is the correct architecture for latency-sensitive or high-volume GPT workloads. **Claude does not support PTU.**

---

## How many concurrent developers can one deployment support?

Azure does not enforce a per-connection concurrency limit on Standard/Global Standard GPT deployments. TPM is the binding constraint. For Claude, the 300 concurrent request cap is an additional hard ceiling.

### Key variables

| Variable | Light usage | Heavy usage |
|---|---|---|
| Avg tokens per request (input + output) | 1,500 | 4,000 |
| Requests per minute per active user | 1 | 6 |
| % of org actively generating tokens | 15% | 30% |

### Claude Sonnet 4.5/4.6 — Enterprise tier (2M TPM / 2,000 RPM)

| Usage pattern | TPM capacity (req/min) | RPM constraint | Effective req/min | Concurrent active users | Total org size |
|---|---|---|---|---|---|
| Light (1,500 tok, 1 req/min) | 1,333 | 2,000 | 1,333 | 1,333 | ~8,900 |
| Moderate (2,000 tok, 3 req/min) | 1,000 | 667 | **667** (RPM-bound) | 222 | ~740–1,480 |
| Heavy (4,000 tok, 6 req/min) | 500 | 333 | **333** (RPM-bound) | 56 | ~185–370 |
| Heavy + 300-concurrent cap | 500 | 333 | **300** (concurrency-bound) | 50 | ~165–330 |

<Aside type="caution">
**The 300 concurrent request cap is the binding constraint for heavy AI-assisted coding.** When developers use AI coding assistants that pipeline multiple requests (e.g., agentic loops, inline completions, multi-step reasoning), the per-deployment concurrent request ceiling of 300 becomes the first limit hit — regardless of TPM headroom. At 6 requests per minute per active user, 300 concurrent requests supports only about **50 simultaneously active developers**.
</Aside>

### GPT-4.1 — Tier 1 (1M TPM / 1,000 RPM, Global Standard)

| Usage pattern | TPM capacity (req/min) | RPM constraint | Effective req/min | Concurrent active users | Total org size |
|---|---|---|---|---|---|
| Light (1,500 tok, 1 req/min) | 667 | 1,000 | 667 | 667 | ~4,450 |
| Moderate (2,000 tok, 3 req/min) | 500 | 333 | **333** (RPM-bound) | 111 | ~370–740 |
| Heavy (4,000 tok, 6 req/min) | 250 | 167 | **167** (RPM-bound) | 28 | ~93–186 |

At higher tiers (Tier 3: 9M TPM / 9,000 RPM), the heavy-usage total org ceiling rises to ~840–1,680 — more viable for large organisations.

### GPT-4o — ~450K TPM (historical Standard)

| Usage pattern | Effective req/min | Concurrent active users | Total org size |
|---|---|---|---|
| Light (1,500 tok, 1 req/min) | 300 | 300 | ~2,000 |
| Moderate (2,000 tok, 3 req/min) | 75 | 25 | ~83–167 |
| Heavy (4,000 tok, 6 req/min) | 18 | 3 | **~10–20** |

<Aside type="caution">
GPT-4o at its historical Standard default is severely limited for heavy AI-assisted coding — a busy team of 20 can saturate it.
</Aside>

---

## Why FoundryGate's per-developer quotas don't solve this

FoundryGate's monthly token budgets and APIM suspension mechanism operate at a different layer than Azure's deployment-level limits:

- **FoundryGate** controls *who gets access* and *how much they consume over a month*. It's a governance and fairness tool.
- **Azure Foundry TPM / RPM / concurrency limits** control *how fast* the deployment can process requests *right now*, regardless of who is asking.

A developer well within their monthly quota will still receive a `429 Too Many Requests` if the deployment is saturated. FoundryGate currently surfaces this as a pass-through error — it does not retry, queue, or prioritise requests.

The effective scale ceiling for FoundryGate is determined by Azure's rate limits, not by the number of users provisioned or the size of their monthly budgets.

---

## Mitigations

### 1. Verify subscription eligibility for Claude first

Before planning any Claude-based deployment, confirm your Azure subscription type is EA or MCA-E. If not, Claude is unavailable through Azure AI Foundry entirely.

### 2. Use GPT-4.1 Global Standard and auto-tier progression

GPT-4.1 on Global Standard starts at 1M TPM (Tier 1) and auto-scales to 45M TPM at Tier 6 as consumption grows. For new deployments it is a better starting point than legacy GPT-4o or GPT-4 Turbo.

### 3. Move GPT workloads to Provisioned Throughput Units (PTU)

PTU provides dedicated, consistent throughput with no shared rate limits. It is the correct architecture for more than ~100 heavily active developers on GPT models. PTU is not available for Claude.

### 4. Request quota increases

For Claude, open a Microsoft Azure support ticket with your expected peak TPM and use case. For GPT models, use the [Azure OpenAI quota request form](https://aka.ms/oai/stuquotarequest). Priority goes to subscriptions already actively consuming existing quota.

### 5. Route across multiple regions

FoundryGate routes to a single Foundry endpoint. Adding APIM backend pools across multiple Foundry deployments in different regions (East US2 + Sweden Central for Claude; any two regions for GPT) multiplies effective TPM and concurrent request headroom. This requires APIM backend pool configuration outside FoundryGate's scope today.

### 6. Use GPT-4o-mini or Claude Haiku for overflow

GPT-4o-mini starts at 2M TPM at Tier 1. Claude Haiku 4.5 at Enterprise tier allows 4M TPM and 4,000 RPM. Routing lightweight tasks (short Q&A, summarisation, embeddings) to these models frees Sonnet / GPT-4.1 capacity for tasks where quality matters.

### 7. Reduce per-request token consumption

Apply APIM policies to cap `max_tokens` / `max_completion_tokens`, strip oversized system prompts at the gateway, or enforce response length limits. Halving average tokens per request doubles effective concurrent user capacity.

---

## Planning for your deployment

| Team size | Recommended approach |
|---|---|
| < 30 developers | Claude Sonnet or GPT-4o at standard limits. Likely fine for moderate usage patterns. |
| 30–100 developers | GPT-4.1 Global Standard (auto-tiers up). For Claude: verify EA/MCA-E subscription. Add APIM retry-on-429 policy. |
| 100–300 developers | PTU for GPT models. For Claude: request quota increases; plan multi-region APIM backend pool. The 300-concurrent-request cap makes Claude challenging at this scale. |
| 300–500 developers | PTU mandatory for GPT. Claude at this scale requires a direct Anthropic API agreement; Azure AI Foundry's Global Standard + 300-concurrent cap is a structural ceiling. |
| 500+ developers | Multi-region APIM backend pools for GPT (PTU per region). Claude via direct Anthropic API, not Azure AI Foundry. |

<Aside type="tip">
FoundryGate's per-developer usage data (admin dashboard + Log Analytics) is your best right-sizing input. Export a week of usage, identify your 90th-percentile concurrent-request minutes, and use those as input to the concurrent-user calculations above. The data will reveal whether you are TPM-bound, RPM-bound, or concurrency-bound — each has a different mitigation path.
</Aside>
