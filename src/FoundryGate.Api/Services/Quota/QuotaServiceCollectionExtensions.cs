using FoundryGate.Api.Configuration;

namespace FoundryGate.Api.Services.Quota;

/// <summary>DI registration for the <c>Services/Quota</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class QuotaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the validated <see cref="GatewayOptions"/> (singleton, lifted off the already-registered
    /// <see cref="AppSettings"/> — the tier table lives on <see cref="GatewayOptions.Tiers"/>), the pure
    /// <see cref="GatewayTierMapper"/> (singleton), the
    /// <see cref="IGatewayTierSync"/> seam (<see cref="ApimGatewayTierSync"/> when
    /// <see cref="GatewayOptions.IsApimConfigured"/>, otherwise <see cref="NullGatewayTierSync"/> — #118),
    /// and the two scoped services that share the request's <c>AppDbContext</c>:
    /// <see cref="IQuotaResolutionService"/> and <see cref="IQuotaAllocationService"/>.
    /// </summary>
    /// <remarks>
    /// The tier sync is <b>scoped</b>, not singleton: the APIM implementation composes the scoped
    /// <c>IApimKeyService</c> (which shares the request's <c>AppDbContext</c> so the <c>key.tier-changed</c>
    /// row commits with the allocation that caused it), and a singleton may not depend on a scoped
    /// service. <see cref="NullGatewayTierSync"/> is stateless, so the shorter lifetime costs it nothing.
    /// </remarks>
    public static IServiceCollection AddQuotaServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<AppSettings>().Gateway);
        services.AddSingleton<GatewayTierMapper>();
        services.AddScoped<IGatewayTierSync>(serviceProvider =>
            serviceProvider.GetRequiredService<GatewayOptions>().IsApimConfigured
                ? ActivatorUtilities.CreateInstance<ApimGatewayTierSync>(serviceProvider)
                : ActivatorUtilities.CreateInstance<NullGatewayTierSync>(serviceProvider));
        services.AddScoped<IQuotaResolutionService, QuotaResolutionService>();
        services.AddScoped<IQuotaAllocationService, QuotaAllocationService>();

        return services;
    }
}
