using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using FoundryGate.Data;
using FoundryGate.Domain.Common;
using FoundryGate.Functions.Configuration;
using FoundryGate.Functions.Extensions;
using FoundryGate.Functions.Services;
using Imagile.Framework.Configuration.Extensions;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// Environment: lowercase local/qa/prod (CONVENTIONS.md), from DOTNET_ENVIRONMENT — which
// infra/modules/control-plane.bicep sets alongside AZURE_FUNCTIONS_ENVIRONMENT. Parsed for the same
// reason the Api parses it: a typo in a deployment variable must fail here, not silently select
// production behaviour.
if (!Enum.TryParse<AppEnvironment.Types>(builder.Environment.EnvironmentName, ignoreCase: true, out var environment))
{
    var validValues = string.Join(", ", Enum.GetNames<AppEnvironment.Types>());
    throw new Imagile.Framework.Configuration.Exceptions.ConfigurationValidationException(
        $"DOTNET_ENVIRONMENT '{builder.Environment.EnvironmentName}' is not a recognized FoundryGate environment. " +
        $"Valid values (case-insensitive): {validValues}.");
}

builder.Services.AddSingleton(typeof(AppEnvironment.Types), _ => environment);

// AZURE_CLIENT_ID is the Functions user-assigned identity in Azure, absent locally (which selects the
// Azure CLI / Visual Studio credential chain). Same class the Api uses — CONVENTIONS.md: never
// DefaultAzureCredential.
var credential = new Imagile.Framework.Configuration.Azure.AppTokenCredential(builder.Configuration["AZURE_CLIENT_ID"]);
builder.Services.AddSingleton<TokenCredential>(credential);

// @KeyVault(SecretName) resolution, skipped cleanly when Azure:KeyVaultUrl is absent so `func start`
// needs no Azure connectivity.
var keyVaultUrl = builder.Configuration["Azure:KeyVaultUrl"];
if (!string.IsNullOrWhiteSpace(keyVaultUrl))
{
    var secretClient = new SecretClient(new Uri(keyVaultUrl), credential);
    builder.Configuration.ReplaceKeyVaultReferences(secretClient);
}

var appSettings = builder.Configuration.Get<AppSettings>()
    ?? throw new Imagile.Framework.Configuration.Exceptions.ConfigurationValidationException(
        "Failed to bind configuration to AppSettings.");
appSettings.ValidateRecursively();
builder.Services.AddSingleton(appSettings);

// Data layer: AppDbContext, TimestampInterceptor, TimeProvider, IAuditWriter — the same registration
// the Api calls, so both hosts write the same rows the same way.
builder.Services.AddFoundryGateData(appSettings.ConnectionStrings.FoundryGate);

// Core quota services + this host's two jobs and their Azure clients.
builder.Services.AddFoundryGateFunctionsServices(appSettings, builder.Configuration);

// Worker telemetry → Azure Monitor, gated by OpenTelemetry:Enabled (off locally).
builder.Services.AddFoundryGateOpenTelemetry(builder.Logging, appSettings.OpenTelemetry);

builder.Build().Run();
