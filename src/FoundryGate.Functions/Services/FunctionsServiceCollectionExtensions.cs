using Azure.Core;
using Azure.Monitor.Query;
using FoundryGate.Core.Entra;
using FoundryGate.Core.Gateway;
using FoundryGate.Core.Quota;
using FoundryGate.Core.Requests;
using FoundryGate.Functions.Configuration;
using FoundryGate.Functions.Services.Entra;
using FoundryGate.Functions.Services.Jobs;
using FoundryGate.Functions.Services.Quota;
using FoundryGate.Functions.Services.Usage;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Functions.Services;

/// <summary>
/// The single DI entry point for the Functions host's services, called once from <c>Program.cs</c> —
/// the same "one line per area" shape as the Api's <c>AddFoundryGateApiServices</c>
/// (CONVENTIONS.md §API service/controller conventions).
/// </summary>
public static class FunctionsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared Core quota services, the two jobs, and the Azure clients they need.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="settings">The validated settings — the gateway section and the storage/lock options come off it.</param>
    /// <param name="hostConfiguration">
    /// The raw host configuration, read only to discover the Functions storage account the host was
    /// already told about (<c>AzureWebJobsStorage__accountName</c>, or the local
    /// <c>UseDevelopmentStorage=true</c> connection string), so the scheduled jobs' locks need no new infra
    /// setting of its own.
    /// </param>
    public static IServiceCollection AddFoundryGateFunctionsServices(
        this IServiceCollection services,
        AppSettings settings,
        IConfiguration hostConfiguration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostConfiguration);

        services.AddSingleton(settings.Gateway);
        services.AddSingleton(settings.Entra);
        services.AddSingleton(settings.Storage);

        // Quota resolution + the monthly reset, shared with the Api (#119), and — since #194 — the same
        // tier sync the Api runs. A reset CAN change a tier: `PUT /config` on DefaultMonthlyTokenQuota
        // re-resolves nobody, so the next scheduled reset is the first thing to notice (#193), and a
        // user with no earlier allocation has no known previous tier. Until #194 this host could only
        // log that at Warning and let SQL and the gateway disagree; now it moves the subscription
        // itself, which is why the Functions identity holds API Management Service Contributor
        // (infra/modules/control-plane-rbac.bicep) — a deliberate widening of its blast radius,
        // documented in reference/infrastructure.
        AddGatewayTierSync(services, settings);
        services.AddQuotaCore();

        // The reset also closes requests left pending past their period (#159); the rule is Core's so a
        // timer and the admin's POST /quota/reset cannot disagree about what expiry means.
        services.AddRequestsCore();

        // The nightly directory sync (#151). Core owns both sync services and the Graph client; this
        // host supplies the two seams that differ — the run has no human actor
        // (SystemEntraSyncActor), and a departure is deprovisioned over Core's own APIM client rather
        // than through the Api's lifecycle orchestrator (DeprovisioningDepartureHandler).
        services.AddEntraCore();
        services.AddScoped<IEntraSyncActor, SystemEntraSyncActor>();
        services.AddScoped<IDepartureHandler, DeprovisioningDepartureHandler>();

        services.AddScoped<IMonthlyResetJob, MonthlyResetJob>();
        services.AddScoped<IUsageSyncJob, UsageSyncJob>();
        services.AddScoped<IUsageQueryClient, LogAnalyticsUsageQueryClient>();
        services.AddScoped<IEntraSyncJob, EntraSyncJob>();

        AddJobLock(services, settings.Storage, hostConfiguration);
        AddLogsQueryClient(services);

        return services;
    }

    /// <summary>
    /// The <see cref="IGatewayTierSync"/> this host runs, plus what it composes. With APIM addressed
    /// (<c>GatewayOptions.IsApimConfigured</c>) that is the real
    /// <see cref="ApimGatewayTierSync"/> over <see cref="ArmApimManagementClient"/>, so a tier change
    /// found by the monthly reset re-scopes the developer's subscription for real and writes the
    /// <c>key.tier-changed</c> row with the run's own unit of work. Without it —
    /// <c>func start</c> against docker SQL, or a fork with no gateway —
    /// <see cref="NullGatewayTierSync"/>, whose "no gateway is configured" Debug line is then true.
    /// </summary>
    /// <remarks>
    /// The actor is <see cref="SystemGatewayTierSyncActor"/>: nothing here runs on anybody's request,
    /// so the audit row is system-attributed (<c>ActorUserId IS NULL</c>) exactly like the reset's own
    /// <c>quota.monthly-reset</c> row. The management client is a singleton (<c>ArmClient</c> is
    /// thread-safe and caches its pipeline); the sync is scoped because it shares the job's
    /// <c>AppDbContext</c> through <c>IAuditWriter</c>.
    /// </remarks>
    private static void AddGatewayTierSync(IServiceCollection services, AppSettings settings)
    {
        if (!settings.Gateway.IsApimConfigured)
        {
            // The unconfigured client, not "no client at all" (the Api's KeysServiceCollectionExtensions
            // shape): since #151 the departure handler also needs one, and a missing registration would
            // be a startup failure on a host that is deliberately gateway-less rather than the
            // FeatureNotConfiguredException the one call that could reach it deserves. Nothing actually
            // calls it on such a host — a developer with no gateway has no APIM subscription to delete.
            services.AddSingleton<IApimManagementClient>(new UnconfiguredApimManagementClient());
            services.AddScoped<IGatewayTierSync, NullGatewayTierSync>();
            return;
        }

        services.AddSingleton<IApimManagementClient>(serviceProvider =>
            new ArmApimManagementClient(settings.Gateway, serviceProvider.GetRequiredService<TokenCredential>()));
        services.AddScoped<IGatewayTierSyncActor, SystemGatewayTierSyncActor>();
        services.AddScoped<IGatewayTierSync, ApimGatewayTierSync>();
    }

    /// <summary>
    /// Registers <see cref="BlobJobLock"/> when a blob endpoint can be worked out, otherwise
    /// <see cref="NullJobLock"/>. Order of preference: explicit <c>Storage:AccountName</c> /
    /// <c>Storage:ConnectionString</c>, then the host's own <c>AzureWebJobsStorage</c> settings, which
    /// infra already sets on every deployed Function App.
    /// </summary>
    private static void AddJobLock(IServiceCollection services, StorageOptions storage, IConfiguration hostConfiguration)
    {
        var accountName = FirstNonBlank(storage.AccountName, hostConfiguration["AzureWebJobsStorage:accountName"]);
        var connectionString = FirstNonBlank(storage.ConnectionString, hostConfiguration["AzureWebJobsStorage"]);

        if (accountName is null && connectionString is null)
        {
            services.AddSingleton<IJobLock, NullJobLock>();
            return;
        }

        services.AddAzureClients(clients =>
        {
            if (accountName is not null)
            {
                // Identity-based, per CONVENTIONS.md §Storage accounts — the account has shared-key
                // access disabled, so a key would not work even if one were available.
                _ = clients.AddBlobServiceClient(new Uri($"https://{accountName}.blob.core.windows.net"));
            }
            else
            {
                _ = clients.AddBlobServiceClient(connectionString);
            }

            clients.UseCredential(serviceProvider => serviceProvider.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton<IJobLock, BlobJobLock>();
    }

    /// <summary>
    /// The Log Analytics client for reconciliation, authenticated as the Function App's identity
    /// (Log Analytics Reader on the workspace). Registered unconditionally: the workspace id is checked
    /// by the query client at call time, so a fork without one gets a clear warning per pass rather
    /// than a host that will not start.
    /// </summary>
    private static void AddLogsQueryClient(IServiceCollection services) =>
        services.AddSingleton(serviceProvider => new LogsQueryClient(serviceProvider.GetRequiredService<TokenCredential>()));

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
