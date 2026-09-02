namespace FoundryGate.Api.Services.Lifecycle;

/// <summary>DI registration for the <c>Services/Lifecycle</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class LifecycleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped <see cref="IUserLifecycleService"/> — scoped because the whole point of the
    /// orchestrator is to share the request's <c>AppDbContext</c> with the key, quota and audit services
    /// it composes, so one pipeline is one unit of work.
    /// </summary>
    public static IServiceCollection AddLifecycleServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IUserLifecycleService, UserLifecycleService>();

        return services;
    }
}
