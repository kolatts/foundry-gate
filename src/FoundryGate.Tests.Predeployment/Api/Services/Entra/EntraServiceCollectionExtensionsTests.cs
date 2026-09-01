using Azure.Core;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Entra;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Tests.Predeployment.Api.Services.Entra;

/// <summary>
/// <see cref="EntraServiceCollectionExtensions.AddEntraServices"/> picks the directory client from
/// <c>Entra:Enabled</c> at resolution time, building the Graph client over the container's
/// <see cref="TokenCredential"/> (#110) — constructing it acquires no token, so this is safe offline.
/// </summary>
public class EntraServiceCollectionExtensionsTests
{
    [Fact]
    public void Enabled_resolves_the_Graph_backed_client_as_a_singleton()
    {
        using var provider = BuildProvider(enabled: true);

        var first = provider.GetRequiredService<IEntraDirectoryClient>();
        var second = provider.GetRequiredService<IEntraDirectoryClient>();

        Assert.IsType<GraphEntraDirectoryClient>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Disabled_resolves_the_disabled_client()
    {
        using var provider = BuildProvider(enabled: false);

        Assert.IsType<DisabledEntraDirectoryClient>(provider.GetRequiredService<IEntraDirectoryClient>());
    }

    [Fact]
    public void Sync_service_is_scoped()
    {
        using var provider = BuildProvider(enabled: false);
        var descriptor = Assert.Single(
            new ServiceCollection().AddEntraServices(),
            d => d.ServiceType == typeof(IEntraUserSyncService));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(EntraUserSyncService), descriptor.ImplementationType);
    }

    private static ServiceProvider BuildProvider(bool enabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new AppSettings
        {
            AzureAd = new AzureAdOptions
            {
                TenantId = Guid.Empty.ToString(),
                ClientId = Guid.Empty.ToString(),
                Audience = "api://" + Guid.Empty,
            },
            Entra = new EntraOptions { Enabled = enabled },
        });
        services.AddSingleton<TokenCredential>(new NeverCalledTokenCredential());
        services.AddEntraServices();

        return services.BuildServiceProvider();
    }

    /// <summary>Proves the registration never acquires a token just to construct the client.</summary>
    private sealed class NeverCalledTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Constructing the Graph client must not acquire a token.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Constructing the Graph client must not acquire a token.");
    }
}
