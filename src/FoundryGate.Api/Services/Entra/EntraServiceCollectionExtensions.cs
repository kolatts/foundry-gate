using Azure.Core;
using FoundryGate.Api.Configuration;
using Microsoft.Graph;

namespace FoundryGate.Api.Services.Entra;

/// <summary>DI registration for the <c>Services/Entra</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class EntraServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEntraDirectoryClient"/> (singleton: <see cref="GraphEntraDirectoryClient"/>
    /// over a <see cref="GraphServiceClient"/> authenticated with the app's <see cref="TokenCredential"/>
    /// when <c>Entra:Enabled</c>, otherwise <see cref="DisabledEntraDirectoryClient"/>) and
    /// <see cref="IEntraUserSyncService"/> (scoped — it shares the request's <c>AppDbContext</c> so the
    /// sync and its audit row save atomically). Reads <see cref="AppSettings"/> and the
    /// <see cref="TokenCredential"/> from the container, both registered by <c>Program.cs</c> before
    /// the services call.
    /// </summary>
    public static IServiceCollection AddEntraServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IEntraDirectoryClient>(serviceProvider =>
        {
            var settings = serviceProvider.GetRequiredService<AppSettings>();
            if (!settings.Entra.Enabled)
            {
                return new DisabledEntraDirectoryClient();
            }

            // GraphClientFactory's default pipeline (retry with Retry-After, redirect, compression)
            // is what this constructor builds; see GraphEntraDirectoryClient remarks on retries.
            var graph = new GraphServiceClient(
                serviceProvider.GetRequiredService<TokenCredential>(),
                [settings.Entra.GraphScope],
                settings.Entra.GraphBaseUrl);

            return new GraphEntraDirectoryClient(
                graph,
                settings.Entra,
                settings.AzureAd,
                serviceProvider.GetRequiredService<ILogger<GraphEntraDirectoryClient>>());
        });

        services.AddScoped<IEntraUserSyncService, EntraUserSyncService>();

        return services;
    }
}
