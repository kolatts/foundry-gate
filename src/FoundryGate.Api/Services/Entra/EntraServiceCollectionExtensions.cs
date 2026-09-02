using FoundryGate.Api.Configuration;
using FoundryGate.Core.Entra;

namespace FoundryGate.Api.Services.Entra;

/// <summary>DI registration for the <c>Services/Entra</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class EntraServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <c>Entra</c> options section, Core's directory client and the two sync services
    /// (<c>AddEntraCore</c>, #151), and the Api's answers to the two host-shaped seams:
    /// <see cref="CurrentUserEntraSyncActor"/> (a sync run belongs to the admin who asked for it) and
    /// <see cref="LifecycleDepartureHandler"/> (a departure goes through plan 21's one orchestrator).
    /// Both seams are scoped, like everything that shares the request's <c>AppDbContext</c>.
    /// </summary>
    public static IServiceCollection AddEntraServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(serviceProvider =>
        {
            var settings = serviceProvider.GetRequiredService<AppSettings>();

            // The directory client resolves the FoundryGate service principal from
            // Entra:ApplicationClientId, which lives on the Entra section because the Functions host —
            // which now runs the same client — has no AzureAd section to read from (nothing there
            // serves a request, so there is no token to validate). On the Api the two name the same app
            // registration, so it is defaulted rather than made a second setting a fork could get wrong
            // in one place only. Infra sets Entra__ApplicationClientId on both hosts, so this fallback
            // is what keeps a local host and an older deployment working.
            if (string.IsNullOrWhiteSpace(settings.Entra.ApplicationClientId))
            {
                settings.Entra.ApplicationClientId = settings.AzureAd.ClientId;
            }

            return settings.Entra;
        });

        _ = services.AddEntraCore();

        services.AddScoped<IEntraSyncActor, CurrentUserEntraSyncActor>();
        services.AddScoped<IDepartureHandler, LifecycleDepartureHandler>();

        return services;
    }
}
