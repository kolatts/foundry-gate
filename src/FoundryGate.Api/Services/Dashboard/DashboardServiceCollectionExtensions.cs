namespace FoundryGate.Api.Services.Dashboard;

/// <summary>DI registration for the <c>Services/Dashboard</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class DashboardServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <c>IMemoryCache</c> the summary is held in (<c>AddMemoryCache</c> is
    /// <c>TryAdd</c>-based, so calling it here as well as in <c>AddFoundryServices</c> is a no-op the
    /// second time) and <see cref="IDashboardService"/> (scoped — it reads through the request's
    /// <c>AppDbContext</c>).
    /// </summary>
    public static IServiceCollection AddDashboardServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();
        services.AddScoped<IDashboardService, DashboardService>();

        return services;
    }
}
