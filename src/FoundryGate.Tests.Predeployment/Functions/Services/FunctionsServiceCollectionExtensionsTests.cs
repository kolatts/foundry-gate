using Azure.Core;
using FoundryGate.Core.Configuration;
using FoundryGate.Core.Gateway;
using FoundryGate.Core.Quota;
using FoundryGate.Data;
using FoundryGate.Functions.Services;
using FoundryGate.Functions.Services.Jobs;
using FoundryGate.Functions.Services.Quota;
using FoundryGate.Functions.Services.Usage;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FunctionsAppSettings = FoundryGate.Functions.Configuration.AppSettings;
using FunctionsStorageOptions = FoundryGate.Functions.Configuration.StorageOptions;

namespace FoundryGate.Tests.Predeployment.Functions.Services;

/// <summary>
/// The Functions host's DI graph, built the way <c>Program.cs</c> builds it and validated the way the
/// runtime would. Nothing else in this suite constructs the host, so without this a lifetime mistake —
/// a singleton job capturing the scoped <c>AppDbContext</c>, say — would first appear as a failed
/// invocation in Azure.
/// </summary>
public class FunctionsServiceCollectionExtensionsTests
{
    [Fact]
    public void Every_registered_service_resolves_with_scope_validation_on()
    {
        using var provider = BuildProvider();

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IMonthlyResetJob>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUsageSyncJob>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUsageQueryClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IQuotaResetService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IQuotaResolutionService>());
    }

    [Fact]
    public void With_APIM_addressed_this_host_moves_tiers_itself_rather_than_reporting_that_it_cannot()
    {
        // #194: a DefaultMonthlyTokenQuota change moves tiers on the next reset, and until this host
        // carried the management client all it could do was log that SQL and the gateway now disagree.
        using var provider = BuildProvider(configureGateway: gateway =>
        {
            gateway.SubscriptionId = "00000000-0000-0000-0000-000000000001";
            gateway.ResourceGroup = "rg-foundrygate-test";
            gateway.ApimName = "apim-foundrygate-test";
        });

        using var scope = provider.CreateScope();

        Assert.IsType<ApimGatewayTierSync>(scope.ServiceProvider.GetRequiredService<IGatewayTierSync>());
        Assert.IsType<ArmApimManagementClient>(scope.ServiceProvider.GetRequiredService<IApimManagementClient>());

        // Nobody's request drives a timer trigger, so the key.tier-changed row it writes is
        // system-attributed — the same shape as the run's own quota.monthly-reset row.
        Assert.IsType<SystemGatewayTierSyncActor>(scope.ServiceProvider.GetRequiredService<IGatewayTierSyncActor>());
    }

    [Fact]
    public void With_no_APIM_addressed_the_tier_sync_is_the_honest_no_op()
    {
        using var provider = BuildProvider();

        using var scope = provider.CreateScope();

        // "No gateway is configured" is simply true here — there is nothing to enforce a tier.
        Assert.IsType<NullGatewayTierSync>(scope.ServiceProvider.GetRequiredService<IGatewayTierSync>());
    }

    [Fact]
    public void The_hosts_own_storage_account_backs_the_reset_lock_without_any_extra_setting()
    {
        // infra sets AzureWebJobsStorage__accountName on every deployed Function App, so Storage:* stays
        // empty and the lease still works — the reason this feature needed no new environment variable.
        using var provider = BuildProvider(hostSettings: new Dictionary<string, string?>
        {
            ["AzureWebJobsStorage:accountName"] = "stfgdeve7k2",
        });

        Assert.IsType<BlobJobLock>(provider.GetRequiredService<IJobLock>());
    }

    [Fact]
    public void Local_Azurite_also_backs_the_lock()
    {
        using var provider = BuildProvider(hostSettings: new Dictionary<string, string?>
        {
            ["AzureWebJobsStorage"] = "UseDevelopmentStorage=true",
        });

        Assert.IsType<BlobJobLock>(provider.GetRequiredService<IJobLock>());
    }

    [Fact]
    public void With_no_storage_discoverable_the_lock_degrades_instead_of_failing_startup()
    {
        using var provider = BuildProvider();

        Assert.IsType<NullJobLock>(provider.GetRequiredService<IJobLock>());
    }

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?>? hostSettings = null,
        Action<GatewayOptions>? configureGateway = null)
    {
        var gateway = TestGatewayTiers.Options();
        configureGateway?.Invoke(gateway);

        var settings = new FunctionsAppSettings
        {
            ConnectionStrings = new FoundryGate.Functions.Configuration.ConnectionStringOptions
            {
                // Never opened: registration does not connect, and no test here resolves AppDbContext.
                FoundryGate = "Server=127.0.0.1,3433;Database=FoundryGate;User Id=sa;Password=x;TrustServerCertificate=True",
            },
            Gateway = gateway,
            Storage = new FunctionsStorageOptions(),
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(hostSettings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TokenCredential>(new StaticTokenCredential());
        services.AddSingleton(settings);
        services.AddFoundryGateData(settings.ConnectionStrings.FoundryGate);
        services.AddFoundryGateFunctionsServices(settings, configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }
}
