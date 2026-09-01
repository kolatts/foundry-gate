param environmentName string
param location string
param tags object = {}

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-foundrygate-${environmentName}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    // Two full calendar months: reconciliation of a monthly quota period must be able
    // to run late without having lost the start of the period.
    retentionInDays: 62
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-foundrygate-${environmentName}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
  }
}

@description('ARM resource id of the workspace (diagnostic settings, role assignments, LogsQueryClient.QueryResourceAsync).')
output workspaceId string = law.id
output workspaceName string = law.name
@description('The workspace GUID ("workspace id" in the Log Analytics query API — LogsQueryClient.QueryWorkspaceAsync, /v1/workspaces/{id}/query). Not the ARM id.')
output workspaceCustomerId string = law.properties.customerId
output appInsightsId string = appInsights.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
