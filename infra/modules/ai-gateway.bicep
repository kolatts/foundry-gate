// The AI gateway layer: APIM backends per Foundry account, a load-balanced pool with
// circuit breakers, the two developer-facing APIs (Anthropic Messages, OpenAI v1),
// the FoundryGate product, and the enforcement policies.
//
// Developer keys are APIM subscriptions scoped to the product. The subscription key
// header is set to the header each CLI already sends (x-api-key for Claude Code,
// api-key for Codex/Azure clients), so no client-side custom-header configuration is
// needed — the developer just pastes their FoundryGate key into the normal env var.
param apimName string
param foundryAccounts array // [{ name, endpoint }]
param defaultDeveloperTpm int
param defaultDeveloperMonthlyTokenQuota int
#disable-next-line no-unused-params
param appInsightsLoggerId string // reserved: API-level diagnostics wiring

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' existing = {
  name: apimName
}

resource accounts 'Microsoft.CognitiveServices/accounts@2026-07-01' existing = [
  for fa in foundryAccounts: {
    name: fa.name
  }
]

// ---- Backends: one Anthropic-path backend per Foundry account ------------------
resource anthropicBackends 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = [
  for (fa, i) in foundryAccounts: {
    parent: apim
    name: 'foundry-anthropic-${fa.name}'
    properties: {
      protocol: 'http'
      url: '${fa.endpoint}anthropic'
      credentials: {
        header: {
          'x-api-key': [accounts[i].listKeys().key1]
        }
      }
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

// ---- Load-balanced pool across regions ----------------------------------------
resource anthropicPool 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = {
  parent: apim
  name: 'foundry-anthropic-pool'
  properties: {
    // Pool backends must not set url/protocol — ARM rejects both.
    type: 'Pool'
    pool: {
      services: [
        for (fa, i) in foundryAccounts: {
          id: anthropicBackends[i].id
          priority: 1
          weight: 1
        }
      ]
    }
  }
}

// ---- OpenAI-path backend (primary account only) --------------------------------
resource openaiBackend 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = {
  parent: apim
  name: 'foundry-openai-${foundryAccounts[0].name}'
  properties: {
    protocol: 'http'
    url: '${foundryAccounts[0].endpoint}openai/v1'
    credentials: {
      header: {
        'api-key': [accounts[0].listKeys().key1]
      }
    }
  }
}

// ---- Anthropic Messages API (Claude Code) --------------------------------------
resource anthropicApi 'Microsoft.ApiManagement/service/apis@2024-06-01-preview' = {
  parent: apim
  name: 'foundrygate-anthropic'
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
      replace(
        loadTextContent('../policies/anthropic-api.xml'),
        '__DEVELOPER_TPM__',
        string(defaultDeveloperTpm)
      ),
      '__MONTHLY_TOKEN_QUOTA__',
      string(defaultDeveloperMonthlyTokenQuota)
    )
  }
  dependsOn: [anthropicPool]
}

// ---- OpenAI v1 API (Codex CLI and OpenAI-compatible clients) -------------------
resource openaiApi 'Microsoft.ApiManagement/service/apis@2024-06-01-preview' = {
  parent: apim
  name: 'foundrygate-openai'
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
      replace(
        replace(
          loadTextContent('../policies/openai-api.xml'),
          '__DEVELOPER_TPM__',
          string(defaultDeveloperTpm)
        ),
        '__MONTHLY_TOKEN_QUOTA__',
        string(defaultDeveloperMonthlyTokenQuota)
      ),
      '__OPENAI_BACKEND_ID__',
      openaiBackend.name
    )
  }
}

// ---- Product: one subscription per developer -----------------------------------
resource product 'Microsoft.ApiManagement/service/products@2024-06-01-preview' = {
  parent: apim
  name: 'foundrygate'
  properties: {
    displayName: 'FoundryGate Developer Access'
    description: 'Per-developer access to Foundry models through the FoundryGate gateway. One subscription (key) per developer.'
    subscriptionRequired: true
    approvalRequired: false
    state: 'published'
  }
}

resource productAnthropic 'Microsoft.ApiManagement/service/products/apiLinks@2024-06-01-preview' = {
  parent: product
  name: 'anthropic-link'
  properties: {
    apiId: anthropicApi.id
  }
}

resource productOpenai 'Microsoft.ApiManagement/service/products/apiLinks@2024-06-01-preview' = {
  parent: product
  name: 'openai-link'
  properties: {
    apiId: openaiApi.id
  }
}

output anthropicApiUrl string = '${apim.properties.gatewayUrl}/anthropic'
output openaiApiUrl string = '${apim.properties.gatewayUrl}/openai/v1'
output productId string = product.name

