// The AI gateway layer: APIM backends per Foundry account, a priority-grouped pool with
// circuit breakers (#83), the two developer-facing APIs (Anthropic Messages, OpenAI v1),
// the quota-tier products and their enforcement policies (#82), and the model
// alias/allowlist layer (#86).
//
// Developer keys are APIM subscriptions scoped to a TIER PRODUCT. The subscription key
// header is set to the header each CLI already sends (x-api-key for Claude Code,
// api-key for Codex/Azure clients), so no client-side custom-header configuration is
// needed — the developer just pastes their FoundryGate key into the normal env var.
//
// Policy scopes (see infra/policies/product-policy.xml for the long form):
//   product = entitlement (model allowlist, TPM cap, monthly token quota)
//   API     = mechanism  (credential stripping, MI backend auth, default backend,
//                         retry/streaming, token metrics)
param apimName string

@description('Foundry accounts to front. [{ name, endpoint, priority?, weight? }] — priority/weight are optional per-account overrides for the Anthropic pool.')
param foundryAccounts array

@description('''Quota tiers, one APIM product each (#82). Each tier needs:
  name              product id (lowercase, url-safe) — also the alias-map key
  displayName       shown in the developer portal
  description       optional
  monthlyTokenQuota tokens per calendar month; 0 = no native quota (TPM cap still applies)
  tpm               per-developer tokens-per-minute cap
`token-quota` accepts LITERALS ONLY (expressions rejected, validated live 2026-09-01),
which is exactly why each tier gets its own rendered product policy instead of one
policy reading a per-user value.''')
param quotaTiers array

@description('''Per-tier model alias maps (#86): { <tier name>: { <alias>: { deployment, pool } } }.
`deployment` is the real Foundry deployment name; `pool` is 'anthropic' (the multi-region
pool) or 'openai' (the primary-account OpenAI backend). The map IS the allowlist — an
alias missing here returns 403 model_not_permitted, and a tier with no entry at all
permits nothing (fail loud, by design). Values become the `fg-model-map-{tier}` named
values, which the control plane may edit through the Management API without redeploying
any policy.''')
param productModelAliases object

// Backend/pool ids are referenced from the alias maps and from the API policies, so they
// are computed as plain strings rather than read back off the resources.
var anthropicPoolName = 'foundry-anthropic-pool'
var openaiBackendName = 'foundry-openai-${foundryAccounts[0].name}'
var anthropicApiName = 'foundrygate-anthropic'
var openaiApiName = 'foundrygate-openai'

// One rendered alias map per tier, with the logical pool name resolved to the real
// backend id. A tier absent from productModelAliases gets `{}` — every model blocked.
var tierAliasMapJson = [
  for tier in quotaTiers: string(toObject(
    items(productModelAliases[?tier.name] ?? {}),
    entry => entry.key,
    entry => {
      deployment: entry.value.deployment
      backend: entry.value.pool == 'openai' ? openaiBackendName : anthropicPoolName
    }
  ))
]

// token-quota attributes are injected only when the tier defines a monthly budget; the
// "unlimited" shape (0) renders no quota attributes at all, leaving TPM smoothing only.
var tierQuotaAttrs = [
  for tier in quotaTiers: tier.monthlyTokenQuota > 0
    ? 'token-quota="${tier.monthlyTokenQuota}" token-quota-period="Monthly" remaining-quota-tokens-header-name="x-fg-remaining-quota"'
    : ''
]

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' existing = {
  name: apimName
}

// ---- Backends: one Anthropic-path backend per Foundry account ------------------
// Auth to Foundry uses APIM's managed identity (authentication-managed-identity in
// the policies) — backends carry no credentials, so no account key ever lands in
// ARM deployment history or the backend objects.
resource anthropicBackends 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = [
  for (fa, i) in foundryAccounts: {
    parent: apim
    name: 'foundry-anthropic-${fa.name}'
    properties: {
      protocol: 'http'
      url: '${fa.endpoint}anthropic'
      circuitBreaker: {
        rules: [
          {
            name: 'break-on-429'
            failureCondition: {
              count: 3
              interval: 'PT30S'
              statusCodeRanges: [
                { min: 429, max: 429 }
                { min: 500, max: 599 }
              ]
            }
            tripDuration: 'PT30S'
            acceptRetryAfter: true
          }
        ]
      }
    }
  }
]

// ---- Priority-grouped pool across regions (#83) --------------------------------
// Priority 1 = the first foundryRegions entry (the primary account, co-located with
// APIM); every later region is priority 2 and only takes traffic when priority 1 is
// exhausted — APIM drains to the next priority group when a member's breaker is tripped
// or it 429s. That is spillover shape, not round-robin: normal traffic stays in-region
// (latency, and Claude prompt-cache affinity), and the other regions are standing
// headroom. Note what it does NOT buy: Claude GlobalStandard quota is pooled
// per-subscription per-model across regions, so a second region multiplies AVAILABILITY
// against deployment-level throttling, not the subscription's token budget. Weight only
// matters within a priority group (equal weights = even spread across same-priority
// members).
resource anthropicPool 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = {
  parent: apim
  name: anthropicPoolName
  properties: {
    // Pool backends must not set url/protocol — ARM rejects both.
    type: 'Pool'
    pool: {
      services: [
        for (fa, i) in foundryAccounts: {
          id: anthropicBackends[i].id
          priority: fa.?priority ?? (i == 0 ? 1 : 2)
          weight: fa.?weight ?? 1
        }
      ]
    }
  }
}

// ---- OpenAI-path backend (primary account only) --------------------------------
resource openaiBackend 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = {
  parent: apim
  name: openaiBackendName
  properties: {
    protocol: 'http'
    url: '${foundryAccounts[0].endpoint}openai/v1'
  }
}

// ---- Shared policy fragments ---------------------------------------------------
// fg-backend-auth and fg-token-metrics fold up what the two API policies had verbatim
// in common; fg-model-alias is the #86 alias/allowlist layer, included from the tier
// product policies (it needs the per-product named value, which only product scope can
// name).
resource backendAuthFragment 'Microsoft.ApiManagement/service/policyFragments@2024-06-01-preview' = {
  parent: apim
  name: 'fg-backend-auth'
  properties: {
    description: 'Strip client credentials, authenticate to Foundry with the gateway managed identity.'
    format: 'rawxml'
    value: loadTextContent('../policies/backend-auth-fragment.xml')
  }
}

resource tokenMetricsFragment 'Microsoft.ApiManagement/service/policyFragments@2024-06-01-preview' = {
  parent: apim
  name: 'fg-token-metrics'
  properties: {
    description: 'llm-emit-token-metric dimensions for the FoundryGate dashboards.'
    format: 'rawxml'
    value: loadTextContent('../policies/token-metrics-fragment.xml')
  }
}

resource modelAliasFragment 'Microsoft.ApiManagement/service/policyFragments@2024-06-01-preview' = {
  parent: apim
  name: 'fg-model-alias'
  properties: {
    description: 'Resolve a virtual model alias to a real deployment + pool, or 403 model_not_permitted.'
    format: 'rawxml'
    // The API name is substituted in so the fragment can pick the caller's native error
    // schema without hard-coding a magic string.
    value: replace(
      loadTextContent('../policies/model-alias-fragment.xml'),
      '__ANTHROPIC_API_ID__',
      anthropicApiName
    )
  }
}

// ---- Anthropic Messages API (Claude Code) --------------------------------------
resource anthropicApi 'Microsoft.ApiManagement/service/apis@2024-06-01-preview' = {
  parent: apim
  name: anthropicApiName
  properties: {
    displayName: 'FoundryGate — Anthropic Messages'
    path: 'anthropic'
    protocols: ['https']
    subscriptionRequired: true
    // Claude Code (Foundry mode) sends the key as x-api-key — verified by wire capture
    // 2026-09-01 (claude-cli 2.1.251). NOT api-key, NOT Authorization: Bearer.
    subscriptionKeyParameterNames: {
      header: 'x-api-key'
      query: 'subscription-key'
    }
    serviceUrl: '${foundryAccounts[0].endpoint}anthropic'
  }
}

resource anthropicOps 'Microsoft.ApiManagement/service/apis/operations@2024-06-01-preview' = {
  parent: anthropicApi
  name: 'messages'
  properties: {
    displayName: 'Messages (all)'
    method: 'POST'
    urlTemplate: '/v1/messages'
  }
}

resource anthropicCountOps 'Microsoft.ApiManagement/service/apis/operations@2024-06-01-preview' = {
  parent: anthropicApi
  name: 'count-tokens'
  properties: {
    displayName: 'Count tokens'
    method: 'POST'
    urlTemplate: '/v1/messages/count_tokens'
  }
}

resource anthropicPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-06-01-preview' = {
  parent: anthropicApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(
      loadTextContent('../policies/anthropic-api.xml'),
      '__ANTHROPIC_POOL_ID__',
      anthropicPool.name
    )
  }
  dependsOn: [backendAuthFragment, tokenMetricsFragment]
}

// ---- OpenAI v1 API (Codex CLI and OpenAI-compatible clients) -------------------
resource openaiApi 'Microsoft.ApiManagement/service/apis@2024-06-01-preview' = {
  parent: apim
  name: openaiApiName
  properties: {
    displayName: 'FoundryGate — OpenAI v1'
    path: 'openai/v1'
    protocols: ['https']
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'api-key'
      query: 'subscription-key'
    }
    serviceUrl: '${foundryAccounts[0].endpoint}openai/v1'
  }
}

resource openaiOps 'Microsoft.ApiManagement/service/apis/operations@2024-06-01-preview' = {
  parent: openaiApi
  name: 'all-post'
  properties: {
    displayName: 'All POST (chat, responses, embeddings)'
    method: 'POST'
    urlTemplate: '/*'
  }
}

resource openaiGetOps 'Microsoft.ApiManagement/service/apis/operations@2024-06-01-preview' = {
  parent: openaiApi
  name: 'all-get'
  properties: {
    displayName: 'All GET (models)'
    method: 'GET'
    urlTemplate: '/*'
  }
}

resource openaiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-06-01-preview' = {
  parent: openaiApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(
      loadTextContent('../policies/openai-api.xml'),
      '__OPENAI_BACKEND_ID__',
      openaiBackend.name
    )
  }
  dependsOn: [backendAuthFragment, tokenMetricsFragment]
}

// ---- Alias maps as per-tier named values (#86) ---------------------------------
// Editable at runtime by the control plane (PUT namedValues/{name}) — retargeting
// `sonnet` at a newer deployment, or granting a tier a model, needs no policy redeploy.
resource modelMapNamedValues 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = [
  for (tier, i) in quotaTiers: {
    parent: apim
    name: 'fg-model-map-${tier.name}'
    properties: {
      // displayName is what `{{...}}` in a policy resolves against — it must match.
      displayName: 'fg-model-map-${tier.name}'
      value: tierAliasMapJson[i]
      secret: false
    }
  }
]

// ---- Quota-tier products: one subscription per developer, scoped to their tier ---
// Replaces the single `foundrygate` product. Moving a developer between tiers is a
// control-plane operation (create their APIM subscription against the target product);
// the quota counter is keyed on the subscription, so a tier change is a new counter.
resource tierProducts 'Microsoft.ApiManagement/service/products@2024-06-01-preview' = [
  for tier in quotaTiers: {
    parent: apim
    name: tier.name
    properties: {
      displayName: tier.displayName
      description: tier.?description ?? 'FoundryGate ${tier.displayName} tier.'
      subscriptionRequired: true
      approvalRequired: false
      state: 'published'
    }
  }
]

resource tierProductAnthropicLinks 'Microsoft.ApiManagement/service/products/apiLinks@2024-06-01-preview' = [
  for (tier, i) in quotaTiers: {
    parent: tierProducts[i]
    name: 'anthropic-link'
    properties: {
      apiId: anthropicApi.id
    }
  }
]

resource tierProductOpenaiLinks 'Microsoft.ApiManagement/service/products/apiLinks@2024-06-01-preview' = [
  for (tier, i) in quotaTiers: {
    parent: tierProducts[i]
    name: 'openai-link'
    properties: {
      apiId: openaiApi.id
    }
  }
]

resource tierProductPolicies 'Microsoft.ApiManagement/service/products/policies@2024-06-01-preview' = [
  for (tier, i) in quotaTiers: {
    parent: tierProducts[i]
    name: 'policy'
    properties: {
      format: 'rawxml'
      value: replace(
        replace(
          replace(
            loadTextContent('../policies/product-policy.xml'),
            '__MODEL_MAP_NAMED_VALUE__',
            'fg-model-map-${tier.name}'
          ),
          '__TIER_TPM__',
          string(tier.tpm)
        ),
        '__QUOTA_ATTRS__',
        tierQuotaAttrs[i]
      )
    }
    dependsOn: [modelAliasFragment, modelMapNamedValues[i]]
  }
]

output anthropicApiUrl string = '${apim.properties.gatewayUrl}/anthropic'
output openaiApiUrl string = '${apim.properties.gatewayUrl}/openai/v1'
output productIds array = [for tier in quotaTiers: tier.name]
output defaultProductId string = quotaTiers[0].name
output anthropicPoolId string = anthropicPoolName
