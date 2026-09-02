// Container Apps environment + the FoundryGate.Api app.
//
// Logs go to the shared Log Analytics workspace through a diagnostic setting
// (`destination: 'azure-monitor'`) rather than the environment's own `log-analytics`
// destination, which needs the workspace SHARED KEY in the template — the one thing
// CONVENTIONS.md's storage/identity rules exist to avoid. Same workspace as the gateway's
// ApiManagementGatewayLlmLog, so one KQL join covers gateway + control plane.
//
// BOOTSTRAP IMAGE. The registry is created by the same deployment, so on the very first
// run nothing has been pushed yet and the app is provisioned with a public placeholder
// (`mcr.microsoft.com/k8se/quickstart:latest`). That image listens on :80 and serves ONLY
// `/` and `/health` (verified under docker 2026-09-01: /health 200, /health/ready 404), so
// while it is in use the ingress port AND every probe path follow it — otherwise the first
// revision fails its startup probe and never provisions. The deploy workflows own the
// image afterwards and MUST pass the running tag on every infra run (main.bicep
// apiContainerImage); a re-run without it resets the app to the placeholder.
//
// PROBE STANCE. Startup = /health/ready (opens a SQL connection: a misconfigured
// connection string or identity fails the deploy right there, which is when you want to
// know). Liveness and readiness = /health (hermetic). Readiness deliberately does NOT hit
// the database: with minReplicas 1 a DB-touching readiness probe every 15 s would keep a
// serverless Azure SQL database from ever reaching its auto-pause delay, making the dev
// tier's auto-pause decorative — and for a single-replica admin API, "DB down" surfacing
// as 500s instead of 503s is not worth an always-on vCore.
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

@description('Fully-qualified image reference. Empty, or the public placeholder, = bootstrap mode (port 80, /health-only probes).')
param containerImage string = ''

@description('Port the real API listens on (ASPNETCORE_URLS is set to match in modules/control-plane.bicep). Ignored in bootstrap mode.')
param targetPort int = 8080

@minValue(1)
@description('Minimum replicas. 1 keeps the API warm: it is the admin plane and the Blazor UI\'s only backend, so cold starts are user-visible.')
param minReplicas int

@minValue(1)
param maxReplicas int

@description('vCPU per replica, as a decimal string (Consumption profile pairs: 0.25/0.5Gi, 0.5/1.0Gi, 1.0/2.0Gi ...). The default matches the published cost model (docs reference/cost-and-capacity).')
param cpu string = '0.25'
param memory string = '0.5Gi'

@description('Spread the Container Apps environment across availability zones. REQUIRES a VNet-integrated environment (properties.vnetConfiguration.infrastructureSubnetId) — ARM rejects zoneRedundant:true without one — so this stays false until private networking (spec §11 / #196) lands.')
param zoneRedundant bool = false

@description('Environment variables: [{ name, value }]. No secretRef entries — the control plane resolves @KeyVault() references itself via its identity.')
param environmentVariables array = []

var placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var isBootstrap = empty(containerImage) || containerImage == placeholderImage
var image = isBootstrap ? placeholderImage : containerImage
var port = isBootstrap ? 80 : targetPort
var livenessPath = '/health'
var startupPath = isBootstrap ? '/health' : '/health/ready'

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
    zoneRedundant: zoneRedundant
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
        targetPort: port
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
          probes: [
            {
              // Generous window: a paused serverless database takes up to ~a minute to
              // resume on the first connection this probe makes.
              type: 'Startup'
              httpGet: { path: startupPath, port: port, scheme: 'HTTP' }
              initialDelaySeconds: 5
              periodSeconds: 5
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: { path: livenessPath, port: port, scheme: 'HTTP' }
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: livenessPath, port: port, scheme: 'HTTP' }
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
@description('True when the app is running the public placeholder image (bootstrap run).')
output isBootstrapImage bool = isBootstrap
