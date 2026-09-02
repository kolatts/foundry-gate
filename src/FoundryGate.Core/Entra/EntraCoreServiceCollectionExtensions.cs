using Azure.Core;
using FoundryGate.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

namespace FoundryGate.Core.Entra;

/// <summary>
/// DI registration for the <c>Core/Entra</c> area — the directory client and the two sync services
/// both hosts run (#151), in the same shape as <see cref="Quota.QuotaCoreServiceCollectionExtensions"/>.
/// Called by each host's own area extension: the Api from
/// <c>Services/Entra/EntraServiceCollectionExtensions.AddEntraServices()</c>, the Functions host from
/// <c>Services/FunctionsServiceCollectionExtensions</c>.
/// </summary>
public static class EntraCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEntraDirectoryClient"/> — <see cref="GraphEntraDirectoryClient"/> over a
    /// <see cref="GraphServiceClient"/> authenticated with the host's <see cref="TokenCredential"/>
    /// when <c>Entra:Enabled</c>, otherwise <see cref="DisabledEntraDirectoryClient"/> — plus
    /// <see cref="IEntraUserSyncService"/> and <see cref="IEntraGroupSyncService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The directory client is a singleton: the Graph client is thread-safe and the only state is a
    /// cached service-principal id, which is exactly the thing worth caching for a process. The two
    /// sync services are scoped, because they share the caller's <c>AppDbContext</c> so a run and its
    /// audit row commit together.
    /// </para>
    /// <para>
    /// Two things the host must register itself, and neither is registered here on purpose, because
    /// both are the host-shaped half of this area: <see cref="IEntraSyncActor"/> (whose audit row a run
    /// is) and <see cref="IDepartureHandler"/> (what happens to someone the directory dropped). The Api
    /// answers "the calling admin" and "the lifecycle orchestrator"; the Functions worker answers "the
    /// system" and <see cref="DeprovisioningDepartureHandler"/>. <c>AddFoundryGateData</c>'s
    /// <c>TimeProvider</c> and <c>IAuditWriter</c>, the quota services (<c>AddQuotaCore</c>, which group
    /// sync re-resolves through) and a <see cref="TokenCredential"/> are the other prerequisites.
    /// </para>
    /// </remarks>
    /// <param name="services">
    /// The host's service collection. It must already carry an <see cref="EntraOptions"/> singleton —
    /// each host lifts the section off its own <c>AppSettings</c>, exactly as
    /// <see cref="Quota.QuotaCoreServiceCollectionExtensions.AddQuotaCore"/> expects
    /// <see cref="GatewayOptions"/>, because Core would otherwise have to know a host's settings class.
    /// </param>
    public static IServiceCollection AddEntraCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IEntraDirectoryClient>(serviceProvider =>
        {
            var entra = serviceProvider.GetRequiredService<EntraOptions>();
            if (!entra.Enabled)
            {
                return new DisabledEntraDirectoryClient();
            }

            // GraphClientFactory's default pipeline (retry with Retry-After, redirect, compression)
            // is what this constructor builds; see GraphEntraDirectoryClient remarks on retries.
            var graph = new GraphServiceClient(
                serviceProvider.GetRequiredService<TokenCredential>(),
                [entra.GraphScope],
                entra.GraphBaseUrl);

            return new GraphEntraDirectoryClient(
                graph,
                entra,
                serviceProvider.GetRequiredService<ILogger<GraphEntraDirectoryClient>>());
        });

        services.AddScoped<IEntraUserSyncService, EntraUserSyncService>();
        services.AddScoped<IEntraGroupSyncService, EntraGroupSyncService>();

        return services;
    }
}
