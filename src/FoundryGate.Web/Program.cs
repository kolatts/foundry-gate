using FoundryGate.Web;
using FoundryGate.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// MSAL: AzureAd:Authority / AzureAd:ClientId / AzureAd:ValidateAuthority bound straight
// from wwwroot/appsettings.json (placeholder values there — see that file's _README and
// foundrygate-spec.md §12 "Configuration for Forks"). This is a public client; nothing in
// this section is a secret.
builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    foreach (var scope in builder.Configuration.GetSection("Api:Scopes").Get<string[]>() ?? [])
    {
        options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
    }
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;
});

var apiBaseUrl = builder.Configuration["Api:BaseUrl"]
    ?? throw new InvalidOperationException("Configuration key 'Api:BaseUrl' is required (wwwroot/appsettings.json).");
var apiScopes = builder.Configuration.GetSection("Api:Scopes").Get<string[]>() ?? [];

// Typed client over FoundryGate.Api (spec §4). AuthorizationMessageHandler (registered by
// AddMsalAuthentication above) attaches a Bearer token to every request scoped to
// apiBaseUrl using the same Api:Scopes granted above — see foundrygate-spec.md §11 "Role
// separation" for why every endpoint requires an authenticated caller. Built by hand
// rather than via IHttpClientFactory's AddHttpClient (not part of the WASM hosting model
// this template pulls in) — this is the same pattern Microsoft's own "Secure a Blazor
// WebAssembly app with Microsoft Entra ID" sample uses.
builder.Services.AddScoped<IFoundryGateApiClient>(sp =>
{
    var authorizationHandler = sp.GetRequiredService<AuthorizationMessageHandler>()
        .ConfigureHandler(authorizedUrls: [apiBaseUrl], scopes: apiScopes);
    var apiHttpClient = new HttpClient(authorizationHandler) { BaseAddress = new Uri(apiBaseUrl) };
    return new FoundryGateApiClient(apiHttpClient);
});

await builder.Build().RunAsync();
