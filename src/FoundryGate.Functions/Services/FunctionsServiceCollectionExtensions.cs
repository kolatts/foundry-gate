using Azure.Core;
using Azure.Monitor.Query;
using FoundryGate.Core.Quota;
using FoundryGate.Core.Requests;
using FoundryGate.Functions.Configuration;
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
    /// <c>UseDevelopmentStorage=true</c> connection string), so the reset lock needs no new infra
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
        services.AddSingleton(settings.Storage);

        // Quota resolution + the monthly reset, shared with the Api (#119). The tier sync here reports
        // rather than moves: this host has no APIM management client, and a reset CAN change a tier —
        // `PUT /config` on DefaultMonthlyTokenQuota re-resolves nobody, so the next scheduled reset is
        // the first thing to notice (#193/#194). WarningGatewayTierSync says so at Warning and the run's
        // audit row counts it; NullGatewayTierSync would have logged "no gateway is configured", which
        // is false here, at Debug.
        services.AddScoped<IGatewayTierSync, WarningGatewayTierSync>();
        services.AddQuotaCore();

        // The reset also closes requests left pending past their period (#159); the rule is Core's so a
        // timer and the admin's POST /quota/reset cannot disagree about what expiry means.
        services.AddRequestsCore();

        services.AddScoped<IMonthlyResetJob, MonthlyResetJob>();
        services.AddScoped<IUsageSyncJob, UsageSyncJob>();
        services.AddScoped<IUsageQueryClient, LogAnalyticsUsageQueryClient>();

        AddResetLock(services, settings.Storage, hostConfiguration);
        AddLogsQueryClient(services);

        return services;
    }

    /// <summary>
    /// Registers <see cref="BlobResetLock"/> when a blob endpoint can be worked out, otherwise
    /// <see cref="NullResetLock"/>. Order of preference: explicit <c>Storage:AccountName</c> /
    /// <c>Storage:ConnectionString</c>, then the host's own <c>AzureWebJobsStorage</c> settings, which
    /// infra already sets on every deployed Function App.
    /// </summary>
    private static void AddResetLock(IServiceCollection services, StorageOptions storage, IConfiguration hostConfiguration)
    {
        var accountName = FirstNonBlank(storage.AccountName, hostConfiguration["AzureWebJobsStorage:accountName"]);
        var connectionString = FirstNonBlank(storage.ConnectionString, hostConfiguration["AzureWebJobsStorage"]);

        if (accountName is null && connectionString is null)
        {
            services.AddSingleton<IResetLock, NullResetLock>();
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

        services.AddSingleton<IResetLock, BlobResetLock>();
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
