using FoundryGate.Api.Configuration;

namespace FoundryGate.Api.Services.Quota;

/// <summary>DI registration for the <c>Services/Quota</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class QuotaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the validated <see cref="GatewayOptions"/> (singleton, lifted off the already-registered
    /// <see cref="AppSettings"/> — the tier table lives on <see cref="GatewayOptions.Tiers"/>), the pure
    /// <see cref="GatewayTierMapper"/> (singleton), the
    /// <see cref="IGatewayTierSync"/> seam (currently <see cref="NullGatewayTierSync"/> — #118 replaces
    /// this registration with the APIM implementation), and the two scoped services that share the
    /// request's <c>AppDbContext</c>: <see cref="IQuotaResolutionService"/> and <see cref="IQuotaAllocationService"/>.
    /// </summary>
    public static IServiceCollection AddQuotaServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<AppSettings>().Gateway);
        services.AddSingleton<GatewayTierMapper>();
        services.AddSingleton<IGatewayTierSync, NullGatewayTierSync>();
        services.AddScoped<IQuotaResolutionService, QuotaResolutionService>();
        services.AddScoped<IQuotaAllocationService, QuotaAllocationService>();

        return services;
    }
}
