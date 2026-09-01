using Azure.Core;
using Azure.ResourceManager;

namespace FoundryGate.Api.Services.Foundry;

/// <summary>DI registration for the <c>Services/Foundry</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class FoundryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ARM client (singleton; built lazily from the host's <see cref="TokenCredential"/>,
    /// so an unconfigured local host never touches Azure — no default subscription: every resource id
    /// is fully qualified from <c>GatewayOptions</c>), <see cref="IFoundryManagementClient"/>
    /// (singleton — stateless over the ARM client), the <c>IMemoryCache</c> the developer model view
    /// uses, and <see cref="IFoundryDeploymentService"/> (scoped — shares the request's
    /// <c>AppDbContext</c> with the audit writer).
    /// </summary>
    public static IServiceCollection AddFoundryServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();
        services.AddSingleton(serviceProvider => new ArmClient(serviceProvider.GetRequiredService<TokenCredential>()));
        services.AddSingleton<IFoundryManagementClient, ArmFoundryManagementClient>();
        services.AddScoped<IFoundryDeploymentService, FoundryDeploymentService>();

        return services;
    }
}
