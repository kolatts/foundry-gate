using Azure.Core;
using FoundryGate.Core.Quota;
using FoundryGate.Data;
using FoundryGate.Functions.Services;
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
    public void The_tier_sync_reports_rather_than_silently_no_ops_because_a_reset_can_change_a_tier()
    {
        using var provider = BuildProvider();

        using var scope = provider.CreateScope();

        // Not NullGatewayTierSync: this host has a gateway, it just cannot reach the management plane
        // (#193/#194). A DefaultMonthlyTokenQuota change moves tiers on the next reset and must be loud.
        Assert.IsType<WarningGatewayTierSync>(scope.ServiceProvider.GetRequiredService<IGatewayTierSync>());
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

        Assert.IsType<BlobResetLock>(provider.GetRequiredService<IResetLock>());
    }

    [Fact]
    public void Local_Azurite_also_backs_the_lock()
    {
        using var provider = BuildProvider(hostSettings: new Dictionary<string, string?>
        {
            ["AzureWebJobsStorage"] = "UseDevelopmentStorage=true",
        });

        Assert.IsType<BlobResetLock>(provider.GetRequiredService<IResetLock>());
    }

    [Fact]
    public void With_no_storage_discoverable_the_lock_degrades_instead_of_failing_startup()
    {
        using var provider = BuildProvider();

        Assert.IsType<NullResetLock>(provider.GetRequiredService<IResetLock>());
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?>? hostSettings = null)
    {
        var settings = new FunctionsAppSettings
        {
            ConnectionStrings = new FoundryGate.Functions.Configuration.ConnectionStringOptions
            {
                // Never opened: registration does not connect, and no test here resolves AppDbContext.
                FoundryGate = "Server=127.0.0.1,3433;Database=FoundryGate;User Id=sa;Password=x;TrustServerCertificate=True",
            },
            Gateway = TestGatewayTiers.Options(),
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
