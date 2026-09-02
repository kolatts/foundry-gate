using FoundryGate.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Core.Quota;

/// <summary>
/// DI registration for the <c>Core/Quota</c> area — the quota services every host needs (#119).
/// Called by each host's own area extension: the Api from
/// <c>Services/Quota/QuotaServiceCollectionExtensions.AddQuotaServices()</c>, the Functions host from
/// <c>Services/FunctionsServiceCollectionExtensions</c>.
/// </summary>
public static class QuotaCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GatewayTierMapper"/> (singleton — pure, over a tier table read once at
    /// startup), <see cref="IQuotaResolutionService"/> and <see cref="IQuotaResetService"/> (scoped,
    /// because they share the caller's <c>AppDbContext</c> so their rows commit with the caller's
    /// audit row).
    /// </summary>
    /// <remarks>
    /// Two things the host must have registered first, and neither is registered here on purpose:
    /// <list type="bullet">
    /// <item>
    /// <see cref="GatewayOptions"/> — each host binds its own <c>AppSettings</c> and lifts the
    /// <c>Gateway</c> section off it, so Core would have to know about a host's settings class to do it.
    /// </item>
    /// <item>
    /// <see cref="IGatewayTierSync"/> — the implementation is the same one in both hosts since #194
    /// (<see cref="ApimGatewayTierSync"/> when <c>GatewayOptions.IsApimConfigured</c>, otherwise
    /// <see cref="NullGatewayTierSync"/>), but <em>what it composes</em> is a host question: the
    /// management client's credential and the <see cref="IGatewayTierSyncActor"/> that decides whether
    /// the audit row belongs to a caller or to the system.
    /// </item>
    /// </list>
    /// <c>AddFoundryGateData</c>'s <c>TimeProvider</c> and <c>IAuditWriter</c> are the other
    /// prerequisites; every host calls it.
    /// </remarks>
    /// <seealso cref="Data.ServiceCollectionExtensions.AddFoundryGateData"/>
    public static IServiceCollection AddQuotaCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<GatewayTierMapper>();
        services.AddScoped<IQuotaResolutionService, QuotaResolutionService>();
        services.AddScoped<IQuotaResetService, QuotaResetService>();

        return services;
    }
}
