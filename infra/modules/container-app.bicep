// Container Apps environment + the FoundryGate.Api app.
//
// Logs go to the shared Log Analytics workspace through a diagnostic setting
// (`destination: 'azure-monitor'`) rather than the environment's own `log-analytics`
// destination, which needs the workspace SHARED KEY in the template — the one thing
// CONVENTIONS.md's storage/identity rules exist to avoid. Same workspace as the gateway's
// ApiManagementGatewayLlmLog, so one KQL join covers gateway + control plane.
//
// The image is a parameter because the registry is created by the same deployment: on the
// very first run nothing has been pushed yet, so the default is a public placeholder that
// answers on 8080 and lets the app (and its probes) provision. The deploy workflows own the
// image afterwards — they MUST pass the current tag on every infra run (see the
// apiContainerImage description in main.bicep), or a re-run resets the app to the
// placeholder.
param containerAppsEnvironmentName string
param containerAppName string
param location string
param tags object = {}

@description('Resource id of the shared Log Analytics workspace (modules/monitoring.bicep).')
param workspaceId string

@description('Resource id of the API user-assigned identity (pulls the image, reads Key Vault, talks to SQL/APIM/Foundry).')
param identityId string

@description('Login server of the registry the image is pulled from with the identity above.')
param registryLoginServer string

@description('Fully-qualified image reference. Empty = public placeholder for the bootstrap deploy.')
param containerImage string = ''

@description('Port the API listens on (ASPNETCORE_URLS is set to match in modules/control-plane.bicep).')
param targetPort int = 8080

@minValue(1)
@description('Minimum replicas. 1 keeps the API warm: it is the admin plane and the Blazor UI\'s only backend, so cold starts are user-visible.')
param minReplicas int = 1

@minValue(1)
param maxReplicas int = 3

@description('vCPU per replica, as a decimal string (Consumption profile pairs: 0.25/0.5Gi, 0.5/1Gi, 1/2Gi ...). The default matches the published cost model (docs reference/cost-and-capacity).')
param cpu string = '0.25'
param memory string = '0.5Gi'

@description('Environment variables: [{ name, value }]. No secretRef entries — the control plane resolves @KeyVault() references itself via its identity.')
param environmentVariables array = []

var placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var image = empty(containerImage) ? placeholderImage : containerImage

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: containerAppsEnvironmentName
  location: location
  tags: union(tags, { 'fg-component': 'api' })
  properties: {
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

resource environmentLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'foundrygate-containerapps-logs'
  scope: containerAppsEnvironment
  properties: {
    workspaceId: workspaceId
    logs: [
      { category: 'ContainerAppConsoleLogs', enabled: true }
      { category: 'ContainerAppSystemLogs', enabled: true }
    ]
  }
}

resource containerApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: containerAppName
  location: location
  tags: union(tags, { 'fg-component': 'api' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
        traffic: [
          { latestRevision: true, weight: 100 }
        ]
      }
      // Identity-based pull: no registry password anywhere. The entry is ignored while the
      // placeholder image (a different server) is in use.
      registries: [
        {
          server: registryLoginServer
          identity: identityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'foundrygate-api'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: environmentVariables
          // /health is hermetic liveness (no dependencies); /health/ready adds the
          // AppDbContext connectivity check — see FoundryGate.Api HealthCheckExtensions.
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/health', port: targetPort, scheme: 'HTTP' }
              initialDelaySeconds: 5
              periodSeconds: 5
              failureThreshold: 12
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: targetPort, scheme: 'HTTP' }
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: targetPort, scheme: 'HTTP' }
              periodSeconds: 15
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: { concurrentRequests: '50' }
            }
          }
        ]
      }
    }
  }
}

output containerAppsEnvironmentName string = containerAppsEnvironment.name
output containerAppsEnvironmentId string = containerAppsEnvironment.id
output containerAppName string = containerApp.name
output containerAppId string = containerApp.id
output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
