param apimName string
param location string
param publisherEmail string
param publisherName string
param appInsightsId string
param appInsightsConnectionString string
param workspaceId string
param tags object = {}

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' = {
  name: apimName
  location: location
  tags: tags
  sku: { name: 'StandardV2', capacity: 1 }
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
