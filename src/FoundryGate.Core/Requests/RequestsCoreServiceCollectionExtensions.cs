using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Core.Requests;

/// <summary>
/// DI registration for the <c>Core/Requests</c> area — the quota-increase-request rules more than one
/// host needs (#119 structure, #159). Called by each host's own area extension: the Api from
/// <c>Services/Requests/RequestsServiceCollectionExtensions.AddRequestsServices()</c>, the Functions
/// host from <c>Services/FunctionsServiceCollectionExtensions</c> (the monthly reset expires stale
/// requests as part of its run).
/// </summary>
public static class RequestsCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IQuotaRequestExpiry"/> — scoped, because it shares the caller's
    /// <c>AppDbContext</c> so its rows commit with the caller's audit row and mutation.
    /// </summary>
    /// <remarks>
    /// Prerequisites the host must already have registered, exactly as for
    /// <c>AddQuotaCore</c>: <c>AddFoundryGateData</c>'s <c>TimeProvider</c> and <c>IAuditWriter</c>.
    /// </remarks>
    public static IServiceCollection AddRequestsCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IQuotaRequestExpiry, QuotaRequestExpiry>();

        return services;
    }
}
