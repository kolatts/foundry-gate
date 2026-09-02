namespace FoundryGate.Api.Services.Cost;

/// <summary>DI registration for the <c>Services/Cost</c> area (#177). Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class CostServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICostEstimator"/> (scoped — it reads through the request's
    /// <c>AppDbContext</c>) and the <c>IMemoryCache</c> its rate card is held in
    /// (<c>AddMemoryCache</c> is <c>TryAdd</c>-based, so the other areas calling it too is a no-op).
    /// </summary>
    public static IServiceCollection AddCostServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();
        services.AddScoped<ICostEstimator, CostEstimator>();

        return services;
    }
}
