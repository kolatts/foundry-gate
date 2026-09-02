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
    /// <see cref="IGatewayTierSync"/> — which implementation is right is a host question: the Api picks
    /// <c>ApimGatewayTierSync</c> or <see cref="NullGatewayTierSync"/> depending on whether a gateway is
    /// configured, and the Functions host always takes the null one (it runs no request-time tier change,
    /// and a reset cannot produce one).
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
