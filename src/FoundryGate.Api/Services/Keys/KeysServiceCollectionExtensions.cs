using Azure.Core;
using FoundryGate.Api.Configuration;
using FoundryGate.Core.Gateway;
using FoundryGate.Domain.Common;
using Imagile.Framework.Configuration.Exceptions;

namespace FoundryGate.Api.Services.Keys;

/// <summary>DI registration for the <c>Services/Keys</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class KeysServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IApimManagementClient"/> (singleton: <see cref="ArmApimManagementClient"/>
    /// when <c>Gateway:*</c> addresses an APIM instance, otherwise
    /// <see cref="UnconfiguredApimManagementClient"/> — allowed in <c>local</c> only, resolved eagerly
    /// so a cloud host without APIM configuration refuses to start) and the scoped
    /// <see cref="IApimKeyService"/>.
    /// </summary>
    public static IServiceCollection AddKeysServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IApimManagementClient>(serviceProvider =>
        {
            var gateway = serviceProvider.GetRequiredService<AppSettings>().Gateway;
            if (gateway.IsApimConfigured)
            {
                return new ArmApimManagementClient(gateway, serviceProvider.GetRequiredService<TokenCredential>());
            }

            var environment = serviceProvider.GetRequiredService<AppEnvironment.Types>();
            if (environment != AppEnvironment.Types.local)
            {
                throw new ConfigurationValidationException(
                    $"Gateway:SubscriptionId, Gateway:ResourceGroup and Gateway:ApimName are required in the '{environment}' environment " +
                    "(infra sets Gateway__* on the Container App); without them no APIM subscription key can be provisioned.");
            }

            return new UnconfiguredApimManagementClient();
        });
        services.AddResolveOnStartup<IApimManagementClient>();

        services.AddScoped<IApimKeyService, ApimKeyService>();

        return services;
    }
}
