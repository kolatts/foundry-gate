using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Extensions;
using FoundryGate.Api.Middleware;
using FoundryGate.Api.Services;
using FoundryGate.Data;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using Imagile.Framework.Configuration.Extensions;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// Environment: lowercase local/qa/prod (CONVENTIONS.md), not ASP.NET Core's own
// Development/Staging/Production names. launchSettings.json and deployment configs set
// ASPNETCORE_ENVIRONMENT accordingly, which also drives the appsettings.{env}.json overlay.
if (!Enum.TryParse<AppEnvironment.Types>(builder.Environment.EnvironmentName, ignoreCase: true, out var environment))
{
    var validValues = string.Join(", ", Enum.GetNames<AppEnvironment.Types>());
    throw new Imagile.Framework.Configuration.Exceptions.ConfigurationValidationException(
        $"ASPNETCORE_ENVIRONMENT '{builder.Environment.EnvironmentName}' is not a recognized FoundryGate environment. " +
        $"Valid values (case-insensitive): {validValues}.");
}

builder.Services.AddSingleton(typeof(AppEnvironment.Types), _ => environment);

// AZURE_CLIENT_ID is set by the hosting environment's user-assigned managed identity;
// absent locally, which selects the Azure CLI / Visual Studio credential chain
// (Imagile.Framework.Configuration's AppTokenCredential — CONVENTIONS.md: "copy the class,
// not DefaultAzureCredential", except here we reference the framework's own copy directly).
var credential = new Imagile.Framework.Configuration.Azure.AppTokenCredential(builder.Configuration["AZURE_CLIENT_ID"]);
builder.Services.AddSingleton<TokenCredential>(credential);

// @KeyVault(SecretName) reference resolution — skipped cleanly when KeyVaultUrl isn't
// configured, so local dev never needs any Azure connectivity to start.
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

// Data layer (#92): AppDbContext, TimestampInterceptor, TimeProvider.
builder.Services.AddFoundryGateData(appSettings.ConnectionStrings.FoundryGate);

// Application services: the ONE call for every Services/<Area> group (each area registers itself
// from Services/ServiceCollectionExtensions.cs — add areas there, not here).
builder.Services.AddFoundryGateApiServices();

// Entra ID bearer auth (spec §4: "all endpoints require a valid Entra ID bearer token";
// §11: "FoundryGate.Admin app role in Entra"). PolicyNames.AdminOnly gates admin-only
// controller actions; the built-in DefaultPolicy (RequireAuthenticatedUser) covers everyone else.
builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration, "AzureAd");
builder.Services.AddAuthorization(options =>
    options.AddPolicy(PolicyNames.AdminOnly, policy => policy.RequireRole(RoleNames.Admin)));

// CORS: the Blazor WASM UI is served from a different origin. Applied globally via
// app.UseCors() below (the pipeline needs exactly one named policy for this — /api/v1's
// controllers are effectively the only browser-facing traffic today; /health is fetched
// server-to-server for probes, not from the WASM UI).
builder.Services.AddCors(options =>
    options.AddPolicy(CorsPolicyNames.Api, policy =>
        policy.WithOrigins([.. appSettings.Cors.AllowedOrigins])
            .AllowAnyHeader()
            .AllowAnyMethod()));

// OpenTelemetry → Azure Monitor, gated by Enabled (off in appsettings.local.json).
builder.Services.AddFoundryGateOpenTelemetry(appSettings.OpenTelemetry);

// Health checks: liveness (no dependencies) + readiness (AppDbContext connectivity).
builder.Services.AddFoundryGateHealthChecks();

// Per-user rate limiting for the two routes that hand a developer their own gateway credential
// (#136). Policies only — nothing is limited globally; an action opts in with [EnableRateLimiting].
builder.Services.AddFoundryGateRateLimiter();

// Controllers: every controller lives under /api/v1 (spec §4). Applying auth as a global MVC
// filter — rather than per-controller — means every controller added by a later issue is
// authenticated by default; opt out per-action with [AllowAnonymous].
builder.Services.AddControllers(options => options.Filters.Add(new AuthorizeFilter()));

// URL generation (CreatedAtRoute → Location headers) renders the whole path lowercase, so a 201
// points at /api/v1/foundry/... exactly as reference/api.md documents it rather than the
// [controller] token's class-name casing (/api/v1/Foundry/...) (#129). Matching was always
// case-insensitive, and so are the lookups behind every route value today (Foundry account and
// deployment names resolve case-insensitively in the service and in ARM). Query strings keep their
// casing: their values can be case-sensitive identifiers.
builder.Services.Configure<RouteOptions>(options =>
{
    options.LowercaseUrls = true;
    options.LowercaseQueryStrings = false;
});

// Global error handling (CONVENTIONS.md: "one IExceptionHandler + ProblemDetails, not
// per-controller try/catch"). AddProblemDetails also shapes the framework's own
// non-exception error responses (e.g. the 404 catch-all wired up via UseStatusCodePages below).
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// OpenAPI with bearer security scheme; Scalar UI is mapped dev-only below.
builder.Services.AddFoundryGateOpenApi();

var app = builder.Build();

if (environment == AppEnvironment.Types.local)
{
    app.MapFoundryGateOpenApi();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseMiddleware<RequestLoggingMiddleware>();

app.UseCors(CorsPolicyNames.Api);

app.UseAuthentication();
app.UseAuthorization();

// After authentication (the policies partition on the caller's oid claim) and after routing (they are
// selected from endpoint metadata, i.e. the [EnableRateLimiting] attribute on the action).
app.UseRateLimiter();

app.MapControllers();
app.MapFoundryGateHealthChecks();

app.Run();

/// <summary>Enables <c>WebApplicationFactory&lt;Program&gt;</c> in FoundryGate.Tests.Predeployment's
/// integration tests — top-level statement programs generate an internal <c>Program</c> class by
/// default, which the test project (a separate assembly) can't otherwise reference.</summary>
public partial class Program;
