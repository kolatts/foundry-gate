using Azure.Core;
using Azure.ResourceManager;
using FoundryGate.Api.Configuration;

namespace FoundryGate.Api.Services.Foundry;

/// <summary>DI registration for the <c>Services/Foundry</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class FoundryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ARM client (singleton; built lazily from the host's <see cref="TokenCredential"/>
    /// and <see cref="GatewayOptions.SubscriptionId"/>, so an unconfigured local host never touches
    /// Azure), <see cref="IFoundryManagementClient"/> (singleton — stateless over the ARM client) and
    /// <see cref="IFoundryDeploymentService"/> (scoped — shares the request's <c>AppDbContext</c> with
    /// the audit writer).
    /// </summary>
    public static IServiceCollection AddFoundryServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(serviceProvider => new ArmClient(
            serviceProvider.GetRequiredService<TokenCredential>(),
            serviceProvider.GetRequiredService<AppSettings>().Gateway.SubscriptionId));
        services.AddSingleton<IFoundryManagementClient, ArmFoundryManagementClient>();
        services.AddScoped<IFoundryDeploymentService, FoundryDeploymentService>();

        return services;
    }
}
