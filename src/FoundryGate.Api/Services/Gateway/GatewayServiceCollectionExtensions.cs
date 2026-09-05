namespace FoundryGate.Api.Services.Gateway;

/// <summary>DI registration for the <c>Services/Gateway</c> area (#225). Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGatewayModelService"/> (scoped — it shares the request's
    /// <c>AppDbContext</c> with the audit writer). The APIM management client it composes is
    /// registered once by <c>Services/Keys</c>, which owns that seam's configuration check; the
    /// Foundry deployment service it reads placement from is registered by <c>Services/Foundry</c>.
    /// </summary>
    public static IServiceCollection AddGatewayServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IGatewayModelService, GatewayModelService>();

        return services;
    }
}
