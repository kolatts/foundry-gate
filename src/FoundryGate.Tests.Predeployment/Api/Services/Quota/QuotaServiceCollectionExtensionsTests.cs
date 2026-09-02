using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Core.Gateway;
using FoundryGate.Core.Quota;
using FoundryGate.Data.Audit;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Tests.Predeployment.Api.Services.Quota;

/// <summary>
/// <see cref="QuotaServiceCollectionExtensions.AddQuotaServices"/>'s one conditional registration
/// (#118): the real gateway tier sync only where there is a gateway to talk to, and never a singleton
/// (it composes the scoped audit writer and the scoped actor seam).
/// </summary>
public class QuotaServiceCollectionExtensionsTests
{
    [Fact]
    public void A_configured_gateway_resolves_the_APIM_tier_sync()
    {
        using var provider = BuildProvider(apimName: "apim-foundrygate-test");

        using var scope = provider.CreateScope();
        Assert.IsType<ApimGatewayTierSync>(scope.ServiceProvider.GetRequiredService<IGatewayTierSync>());
    }

    [Fact]
    public void No_gateway_keeps_the_null_tier_sync_so_a_local_host_still_resolves_quota()
    {
        using var provider = BuildProvider(apimName: null);

        using var scope = provider.CreateScope();
        Assert.IsType<NullGatewayTierSync>(scope.ServiceProvider.GetRequiredService<IGatewayTierSync>());
    }

    [Fact]
    public void The_tier_sync_is_scoped_because_the_APIM_implementation_needs_the_requests_DbContext()
    {
        var descriptor = Assert.Single(
            new ServiceCollection().AddQuotaServices(),
            d => d.ServiceType == typeof(IGatewayTierSync));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    private static ServiceProvider BuildProvider(string? apimName)
    {
        var gateway = TestGatewayTiers.Options();
        if (apimName is not null)
        {
            gateway.SubscriptionId = Guid.Empty.ToString();
            gateway.ResourceGroup = "rg-foundrygate-test";
            gateway.ApimName = apimName;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new AppSettings { Gateway = gateway });

        // The APIM tier sync composes the management client, the audit writer and the actor seam; fakes
        // stand in for all three so this test is about the selection rule, not about their own graphs.
        services.AddSingleton<IApimManagementClient>(new FakeApimManagementClient());
        services.AddScoped<IAuditWriter, NeverCalledAuditWriter>();
        services.AddScoped<IGatewayTierSyncActor>(_ => new FixedGatewayTierSyncActor(null));
        services.AddQuotaServices();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
