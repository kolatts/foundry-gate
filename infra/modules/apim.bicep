param apimName string
param location string
param publisherEmail string
param publisherName string
param appInsightsId string
param appInsightsConnectionString string
param workspaceId string
param tags object = {}

@description('APIM v2 tier. Anthropic-schema LLM policies require a v2 tier; BasicV2 is the cheapest viable option (all required features: token policies, pools, circuit breakers). StandardV2 adds headroom/features some tenants want.')
@allowed(['BasicV2', 'StandardV2', 'PremiumV2'])
param skuName string = 'StandardV2'

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' = {
  name: apimName
  location: location
  tags: tags
  sku: { name: skuName, capacity: 1 }
  identity: { type: 'SystemAssigned' }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
  }
}

resource appInsightsLogger 'Microsoft.ApiManagement/service/loggers@2024-06-01-preview' = {
  parent: apim
  name: 'foundrygate-appinsights'
  properties: {
    loggerType: 'applicationInsights'
    resourceId: appInsightsId
    credentials: {
      connectionString: appInsightsConnectionString
    }
  }
}

// Billing-grade per-request token accounting: ApiManagementGatewayLlmLog rows land in
// Log Analytics (join to ApiManagementGatewayLogs on CorrelationId for the developer's
// APIM subscription id).
resource llmLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'foundrygate-llm-logs'
  scope: apim
  properties: {
    workspaceId: workspaceId
    // REQUIRED, and its absence fails silently. Without `Dedicated`, Azure Monitor sends
    // both categories to the legacy catch-all `AzureDiagnostics` table instead of the
    // resource-specific `ApiManagementGatewayLlmLog` / `ApiManagementGatewayLogs` tables —
    // which are exactly the two tables src/FoundryGate.Functions/Kql/UsageBySubscription.kql
    // joins. The diagnostic setting reports healthy, rows really do arrive, and the
    // reconciliation query returns an empty result forever.
    // Verified live 2026-09-05: `search * | summarize count() by $table` showed 582 rows in
    // AzureDiagnostics (289 GatewayLlmLogs + 293 GatewayLogs) and zero rows in either
    // resource-specific table.
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'GatewayLogs', enabled: true }
      { category: 'GatewayLlmLogs', enabled: true }
    ]
  }
}

// App Insights diagnostic: required for llm-emit-token-metric custom metrics to flow.
resource appInsightsDiagnostic 'Microsoft.ApiManagement/service/diagnostics@2024-06-01-preview' = {
  parent: apim
  name: 'applicationinsights'
  properties: {
    loggerId: appInsightsLogger.id
    // 10% + allErrors: App Insights is the debugging lens; billing-grade accounting
    // lives in the 100% GatewayLlmLogs pipeline below. 100% here would double-ingest
    // every agent request for no additional truth.
    sampling: { samplingType: 'fixed', percentage: 10 }
    alwaysLog: 'allErrors'
    metrics: true // required for llm-emit-token-metric to land in customMetrics
  }
}

// APIM-side LLM diagnostic: turns on token/message capture for the LLM log category.
resource azureMonitorDiagnostic 'Microsoft.ApiManagement/service/diagnostics@2024-06-01-preview' = {
  parent: apim
  name: 'azuremonitor'
  properties: {
    loggerId: azureMonitorLogger.id
    sampling: { samplingType: 'fixed', percentage: 100 }
    // logs: token counts + model per request; message bodies deliberately not captured
    // (agent-harness prompts are 50-200KB/request — Log Analytics ingestion cost).
    largeLanguageModel: {
      logs: 'enabled'
    }
  }
}

resource azureMonitorLogger 'Microsoft.ApiManagement/service/loggers@2024-06-01-preview' = {
  parent: apim
  name: 'azuremonitor'
  properties: {
    loggerType: 'azureMonitor'
    isBuffered: true
  }
}

output apimName string = apim.name
output apimId string = apim.id
output gatewayUrl string = apim.properties.gatewayUrl
output principalId string = apim.identity.principalId
output appInsightsLoggerId string = appInsightsLogger.id
