using FoundryGate.Core.Requests;

namespace FoundryGate.Api.Services.Requests;

/// <summary>DI registration for the <c>Services/Requests</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class RequestsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IQuotaRequestService"/> — scoped, because it shares the request's
    /// <c>AppDbContext</c> with <c>IQuotaResolutionService</c> and <c>IAuditService</c> so an
    /// approval's user update, allocation upsert and audit row commit together — plus the Core rules of
    /// this area (<see cref="RequestsCoreServiceCollectionExtensions.AddRequestsCore"/>: stale-request
    /// expiry, which the monthly reset runs too and so cannot live in the Api).
    /// </summary>
    public static IServiceCollection AddRequestsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRequestsCore();
        services.AddScoped<IQuotaRequestService, QuotaRequestService>();

        return services;
    }
}
