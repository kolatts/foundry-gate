using Azure.Core;
using FoundryGate.Api.Configuration;
using FoundryGate.Domain.Common;
using Microsoft.AspNetCore.DataProtection;

namespace FoundryGate.Api.Services.Security;

/// <summary>DI registration for the <c>Services/Security</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IKeyProtector"/> singleton chosen by <see cref="KeyProtectorFactory"/>
    /// from the bound <see cref="AppSettings"/> and the <see cref="AppEnvironment.Types"/> singleton,
    /// resolved eagerly at host start so a bad <c>KeyProtection</c>/<c>Gateway</c> combination refuses
    /// to boot. Also registers Data Protection (idempotent) for the local provider.
    /// </summary>
    public static IServiceCollection AddSecurityServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDataProtection();

        services.AddSingleton(serviceProvider =>
        {
            var settings = serviceProvider.GetRequiredService<AppSettings>();
            return KeyProtectorFactory.Create(
                settings.KeyProtection,
                settings.Gateway,
                serviceProvider.GetRequiredService<AppEnvironment.Types>(),
                serviceProvider.GetRequiredService<TokenCredential>(),
                serviceProvider.GetRequiredService<IDataProtectionProvider>());
        });
        services.AddResolveOnStartup<IKeyProtector>();

        return services;
    }
}
